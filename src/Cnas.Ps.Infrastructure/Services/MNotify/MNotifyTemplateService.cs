using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.MNotify;

/// <summary>
/// R0115 / TOR CF 14.07 — concrete implementation of
/// <see cref="IMNotifyTemplateService"/>. Persists templates through the
/// per-request <see cref="ICnasDbContext"/> and reads through the read-only
/// replica via <see cref="IReadOnlyCnasDbContext"/>.
/// </summary>
public sealed class MNotifyTemplateService : IMNotifyTemplateService
{
    /// <summary>Audit code emitted on a state-changing upsert.</summary>
    public const string AuditUpserted = "MNOTIFY.TEMPLATE.UPSERTED";

    /// <summary>Audit code emitted on a deactivate.</summary>
    public const string AuditDeactivated = "MNOTIFY.TEMPLATE.DEACTIVATED";

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _readOnlyDb;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;

    /// <summary>Constructs the service.</summary>
    /// <param name="db">Per-request write context.</param>
    /// <param name="readOnlyDb">Per-request read-only context.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC clock.</param>
    /// <param name="caller">Authenticated caller information.</param>
    /// <param name="audit">Audit journal façade.</param>
    public MNotifyTemplateService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext readOnlyDb,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(readOnlyDb);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);

        _db = db;
        _readOnlyDb = readOnlyDb;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<MNotifyTemplateDto>>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var query = _readOnlyDb.MNotifyTemplates.AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(t => t.IsActive);
        }
        var rows = await query
            .OrderBy(t => t.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<MNotifyTemplateDto> dtos = rows.Select(Project).ToList();
        return Result<IReadOnlyList<MNotifyTemplateDto>>.Success(dtos);
    }

    /// <inheritdoc />
    public async Task<Result<MNotifyTemplateDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<MNotifyTemplateDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _readOnlyDb.MNotifyTemplates
            .SingleOrDefaultAsync(t => t.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<MNotifyTemplateDto>.Failure(
                ErrorCodes.NotFound,
                $"MNotify template id={decoded.Value} not found.");
        }
        return Result<MNotifyTemplateDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<MNotifyTemplateDto>> UpsertAsync(
        MNotifyTemplateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var inboundShape = ValidateInput(input);
        if (inboundShape.IsFailure)
        {
            return Result<MNotifyTemplateDto>.Failure(inboundShape.ErrorCode!, inboundShape.ErrorMessage!);
        }

        var channelKind = (MNotifyChannelKind)(int)input.ChannelKind;

        var existing = await _db.MNotifyTemplates
            .SingleOrDefaultAsync(t => t.Code == input.Code, cancellationToken)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        bool isInsert = existing is null;
        MNotifyTemplate row;
        if (isInsert)
        {
            row = new MNotifyTemplate
            {
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                Code = input.Code,
                ChannelKind = channelKind,
                Subject = input.Subject,
                BodyMarkdown = input.BodyMarkdown,
                UpdatedByUserId = _caller.UserId,
                IsActive = true,
            };
            _db.MNotifyTemplates.Add(row);
        }
        else
        {
            row = existing!;
            row.ChannelKind = channelKind;
            row.Subject = input.Subject;
            row.BodyMarkdown = input.BodyMarkdown;
            row.IsActive = true;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
            row.UpdatedByUserId = _caller.UserId;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            templateId = row.Id,
            code = row.Code,
            channelKind = row.ChannelKind.ToString(),
            inserted = isInsert,
        });
        await _audit.RecordAsync(
            AuditUpserted,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(MNotifyTemplate),
            row.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<MNotifyTemplateDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result> DeactivateAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.MNotifyTemplates
            .SingleOrDefaultAsync(t => t.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"MNotify template id={decoded.Value} not found.");
        }
        if (!row.IsActive)
        {
            // Idempotent — already deactivated.
            return Result.Success();
        }
        var now = _clock.UtcNow;
        row.IsActive = false;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        row.UpdatedByUserId = _caller.UserId;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            templateId = row.Id,
            code = row.Code,
        });
        await _audit.RecordAsync(
            AuditDeactivated,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(MNotifyTemplate),
            row.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>Projects a domain row into the wire DTO.</summary>
    private MNotifyTemplateDto Project(MNotifyTemplate row) => new(
        Sqid: _sqids.Encode(row.Id),
        Code: row.Code,
        ChannelKind: (MNotifyChannelKindDto)(int)row.ChannelKind,
        Subject: row.Subject,
        BodyMarkdown: row.BodyMarkdown,
        IsActive: row.IsActive,
        UpdatedAtUtc: row.UpdatedAtUtc);

    /// <summary>
    /// Defence-in-depth shape validation duplicating the application validator. Allows
    /// the service to refuse bad input even when callers bypass FluentValidation.
    /// </summary>
    private static Result ValidateInput(MNotifyTemplateInputDto input)
    {
        if (string.IsNullOrWhiteSpace(input.Code) || input.Code.Length > MNotifyTemplate.MaxCodeLength)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Code is required (≤ 80 chars).");
        }
        if (!Enum.IsDefined(input.ChannelKind))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "ChannelKind is invalid.");
        }
        if (input.ChannelKind == MNotifyChannelKindDto.Email
            && string.IsNullOrWhiteSpace(input.Subject))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Subject is required for Email templates.");
        }
        if (input.Subject is { Length: > MNotifyTemplate.MaxSubjectLength })
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Subject exceeds 256 characters.");
        }
        if (string.IsNullOrWhiteSpace(input.BodyMarkdown)
            || input.BodyMarkdown.Length > MNotifyTemplate.MaxBodyLength)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "BodyMarkdown is required and ≤ 16 KiB.");
        }
        return Result.Success();
    }
}
