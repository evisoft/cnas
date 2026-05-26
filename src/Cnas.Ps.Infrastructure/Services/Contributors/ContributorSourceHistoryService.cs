using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Contributors;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Contributors;

/// <summary>
/// R0302 / TOR §2.1 — concrete implementation of
/// <see cref="IContributorSourceHistoryService"/>. Persists one
/// <see cref="ContributorSourceChangeHistory"/> row per change, emits the
/// Notice-severity <c>CONTRIBUTOR.SOURCE_CHANGED</c> audit event, and bumps the
/// <c>cnas.contributor.source_change.recorded</c> counter (tagged with
/// <c>new_source</c>).
/// </summary>
public sealed class ContributorSourceHistoryService : IContributorSourceHistoryService
{
    /// <summary>Cached JSON serializer options shared across audit payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Hard upper bound on the page size accepted at the boundary.</summary>
    private const int MaxTake = 200;

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _read;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IValidator<ContributorSourceChangeArgs> _argsValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context for the history listing path.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="argsValidator">Validator for the record-change input shape.</param>
    public ContributorSourceHistoryService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<ContributorSourceChangeArgs> argsValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(argsValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _argsValidator = argsValidator;
    }

    /// <inheritdoc />
    public async Task<Result> RecordChangeAsync(
        long contributorId,
        string? oldSource,
        string newSource,
        long? actorUserId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newSource);

        var args = new ContributorSourceChangeArgs(contributorId, oldSource, newSource, reason);
        var v = await _argsValidator.ValidateAsync(args, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var now = _clock.UtcNow;
        var row = new ContributorSourceChangeHistory
        {
            ContributorId = contributorId,
            OldSourceSystem = oldSource,
            NewSourceSystem = newSource,
            ChangedAtUtc = now,
            ChangedByUserId = actorUserId is null ? null : (int?)(int)Math.Min(int.MaxValue, actorUserId.Value),
            Reason = reason,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ContributorSourceChangeHistory.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // PII-free details: only the source attributions + actor sqid.
        var details = JsonSerializer.Serialize(new
        {
            contributorId,
            oldSource,
            newSource,
            actorSqid = _caller.UserSqid,
        }, CachedJsonOptions);

        await _audit.RecordAsync(
            IContributorSourceHistoryService.AuditSourceChanged,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(ContributorSourceChangeHistory),
            row.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        CnasMeter.ContributorSourceChangeRecorded.Add(1,
            new KeyValuePair<string, object?>("new_source", newSource));

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<ContributorSourceChangeHistoryPageDto>> GetHistoryAsync(
        string contributorSqid,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return Result<ContributorSourceChangeHistoryPageDto>.Failure(
                decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var contributorId = decoded.Value;
        var exists = await _read.Contributors
            .AnyAsync(c => c.Id == contributorId, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return Result<ContributorSourceChangeHistoryPageDto>.Failure(
                ErrorCodes.NotFound, "Contributor not found.");
        }

        var clampedSkip = Math.Max(0, skip);
        var clampedTake = Math.Clamp(take <= 0 ? 20 : take, 1, MaxTake);

        var query = _read.ContributorSourceChangeHistory
            .Where(h => h.ContributorId == contributorId);

        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(h => h.ChangedAtUtc)
            .ThenByDescending(h => h.Id)
            .Skip(clampedSkip)
            .Take(clampedTake)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = new List<ContributorSourceChangeHistoryDto>(rows.Count);
        foreach (var r in rows)
        {
            items.Add(ToDto(r, contributorSqid));
        }

        return Result<ContributorSourceChangeHistoryPageDto>.Success(
            new ContributorSourceChangeHistoryPageDto(items, total, clampedSkip, clampedTake));
    }

    /// <summary>Projects an entity into its outbound DTO with Sqid-encoded ids.</summary>
    /// <param name="r">Loaded entity.</param>
    /// <param name="contributorSqid">Cached Sqid of the parent contributor (avoids re-encoding).</param>
    /// <returns>Populated DTO.</returns>
    private ContributorSourceChangeHistoryDto ToDto(ContributorSourceChangeHistory r, string contributorSqid)
        => new(
            Id: _sqids.Encode(r.Id),
            ContributorSqid: contributorSqid,
            OldSourceSystem: r.OldSourceSystem,
            NewSourceSystem: r.NewSourceSystem,
            ChangedAtUtc: r.ChangedAtUtc,
            ChangedByUserSqid: r.ChangedByUserId is null ? null : _sqids.Encode(r.ChangedByUserId.Value),
            Reason: r.Reason);
}
