using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ManagementPeriods;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Penalties;

/// <summary>
/// R0820 / TOR BP 1.2-K — concrete implementation of
/// <see cref="IManagementPeriodService"/>. Owns the close / re-open lifecycle
/// of the management-period anchor row and exposes
/// <see cref="IsMonthClosedAsync"/> for the declaration-registration guard.
/// </summary>
public sealed class ManagementPeriodService : IManagementPeriodService
{
    /// <summary>Stable audit event code emitted on a successful close.</summary>
    public const string AuditClosed = "MANAGEMENT_PERIOD.CLOSED";

    /// <summary>Stable audit event code emitted on a successful re-open.</summary>
    public const string AuditReopened = "MANAGEMENT_PERIOD.REOPENED";

    /// <summary>Stable failure message attached when the month is already closed.</summary>
    public const string MonthAlreadyClosedMessage = "MONTH_ALREADY_CLOSED";

    /// <summary>Stable failure message attached when the month is not re-openable.</summary>
    public const string MonthAlreadyReopenedMessage = "MONTH_ALREADY_REOPENED";

    /// <summary>Cached JSON serializer options shared across audit-payload builders.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _readDb;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IValidator<ManagementPeriodCloseInputDto> _closeValidator;
    private readonly IValidator<ManagementPeriodReopenInputDto> _reopenValidator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">Write-side DbContext.</param>
    /// <param name="readDb">Read-only DbContext used to compute aggregates (R0026 routing).</param>
    /// <param name="clock">UTC clock.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller context for audit attribution.</param>
    /// <param name="audit">Audit-log façade.</param>
    /// <param name="closeValidator">Validator for the close-input shape.</param>
    /// <param name="reopenValidator">Validator for the re-open-input shape.</param>
    public ManagementPeriodService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext readDb,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<ManagementPeriodCloseInputDto> closeValidator,
        IValidator<ManagementPeriodReopenInputDto> reopenValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(readDb);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(closeValidator);
        ArgumentNullException.ThrowIfNull(reopenValidator);
        _db = db;
        _readDb = readDb;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _closeValidator = closeValidator;
        _reopenValidator = reopenValidator;
    }

    /// <inheritdoc />
    public async Task<Result<ManagementPeriodCloseDto>> CloseAsync(
        DateOnly month,
        string? notes,
        CancellationToken ct = default)
    {
        var input = new ManagementPeriodCloseInputDto(month, notes);
        var validation = await _closeValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ManagementPeriodCloseDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        // Refuse a re-close when an active close row exists AND it is not
        // re-opened. A re-opened month is treated as open, so closing it again
        // re-affirms the close (we replay against the same row).
        var existing = await _db.ManagementPeriodCloses
            .SingleOrDefaultAsync(r => r.Month == month && r.IsActive, ct)
            .ConfigureAwait(false);
        if (existing is not null && !existing.IsReopened)
        {
            return Result<ManagementPeriodCloseDto>.Failure(
                ErrorCodes.Conflict,
                MonthAlreadyClosedMessage);
        }

        // Compute generalising-report aggregates from the monthly roll-ups.
        var aggregates = await _readDb.MonthlyContributionCalculations
            .Where(r => r.Month == month && r.IsActive)
            .Select(r => new
            {
                r.ContributorId,
                r.TotalAdjusted,
                r.UnderpaymentAmount,
                r.DeclarationCount,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        decimal totalDeclared = 0m;
        decimal totalPaid = 0m;
        int declarationCount = 0;
        var distinctPayers = new HashSet<long>();
        foreach (var row in aggregates)
        {
            totalDeclared += row.TotalAdjusted;
            // Pragmatic shortcut — until a payments table exists, treat
            // "paid" as adjusted minus underpayment so the close row carries a
            // sensible aggregate for the generalising report.
            totalPaid += row.TotalAdjusted - (row.UnderpaymentAmount ?? 0m);
            declarationCount += row.DeclarationCount;
            distinctPayers.Add(row.ContributorId);
        }
        var payerCount = distinctPayers.Count;

        var now = _clock.UtcNow;
        ManagementPeriodClose entity;
        if (existing is null)
        {
            entity = new ManagementPeriodClose
            {
                Month = month,
                ClosedAtUtc = now,
                ClosedByUserId = _caller.UserId ?? 0L,
                Notes = notes,
                TotalDeclaredAcrossPayers = totalDeclared,
                TotalPaidAcrossPayers = totalPaid,
                PayerCount = payerCount,
                DeclarationCount = declarationCount,
                IsReopened = false,
                ReopenedAtUtc = null,
                ReopenedByUserId = null,
                ReopenReason = null,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.ManagementPeriodCloses.Add(entity);
        }
        else
        {
            // Re-closing a previously re-opened month — clear the re-open
            // flags and refresh the snapshot aggregates.
            existing.ClosedAtUtc = now;
            existing.ClosedByUserId = _caller.UserId ?? 0L;
            existing.Notes = notes;
            existing.TotalDeclaredAcrossPayers = totalDeclared;
            existing.TotalPaidAcrossPayers = totalPaid;
            existing.PayerCount = payerCount;
            existing.DeclarationCount = declarationCount;
            existing.IsReopened = false;
            existing.ReopenedAtUtc = null;
            existing.ReopenedByUserId = null;
            existing.ReopenReason = null;
            existing.UpdatedAtUtc = now;
            existing.UpdatedBy = _caller.UserSqid;
            entity = existing;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                month = month.ToString("O", CultureInfo.InvariantCulture),
                totalDeclared,
                totalPaid,
                payerCount,
                declarationCount,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditClosed,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(ManagementPeriodClose),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.ManagementPeriodClosed.Add(1);

        return Result<ManagementPeriodCloseDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result> ReopenAsync(DateOnly month, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var validation = await _reopenValidator
            .ValidateAsync(new ManagementPeriodReopenInputDto(month, reason), ct)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var entity = await _db.ManagementPeriodCloses
            .SingleOrDefaultAsync(r => r.Month == month && r.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Management period is not closed.");
        }
        if (entity.IsReopened)
        {
            return Result.Failure(ErrorCodes.Conflict, MonthAlreadyReopenedMessage);
        }

        var now = _clock.UtcNow;
        entity.IsReopened = true;
        entity.ReopenedAtUtc = now;
        entity.ReopenedByUserId = _caller.UserId;
        entity.ReopenReason = reason;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                month = month.ToString("O", CultureInfo.InvariantCulture),
                reason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditReopened,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(ManagementPeriodClose),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<bool> IsMonthClosedAsync(DateOnly month, CancellationToken ct = default)
    {
        return await _readDb.ManagementPeriodCloses
            .AnyAsync(r => r.Month == month && r.IsActive && !r.IsReopened, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ManagementPeriodCloseDto?> GetAsync(DateOnly month, CancellationToken ct = default)
    {
        var entity = await _readDb.ManagementPeriodCloses
            .SingleOrDefaultAsync(r => r.Month == month && r.IsActive, ct)
            .ConfigureAwait(false);
        return entity is null ? null : ToDto(entity);
    }

    /// <summary>Projects a <see cref="ManagementPeriodClose"/> entity into its outbound DTO.</summary>
    /// <param name="entity">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private ManagementPeriodCloseDto ToDto(ManagementPeriodClose entity) => new(
        Id: _sqids.Encode(entity.Id),
        Month: entity.Month,
        ClosedAtUtc: entity.ClosedAtUtc,
        ClosedByUserSqid: _sqids.Encode(entity.ClosedByUserId),
        Notes: entity.Notes,
        TotalDeclaredAcrossPayers: entity.TotalDeclaredAcrossPayers,
        TotalPaidAcrossPayers: entity.TotalPaidAcrossPayers,
        PayerCount: entity.PayerCount,
        DeclarationCount: entity.DeclarationCount,
        IsReopened: entity.IsReopened,
        ReopenedAtUtc: entity.ReopenedAtUtc,
        ReopenedByUserSqid: entity.ReopenedByUserId is { } rid ? _sqids.Encode(rid) : null,
        ReopenReason: entity.ReopenReason);
}
