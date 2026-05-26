using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Recalculation;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Recalculation;

/// <summary>
/// R1503 / TOR §3.7-D — production implementation of
/// <see cref="ILegalChangeEventService"/>. Owns the register / modify /
/// mark-ready / cancel / lookup / list endpoints over the
/// <see cref="LegalChangeEvent"/> registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Code generation.</b> <see cref="RegisterAsync"/> uses the caller-
/// supplied <c>Code</c> when present; otherwise it auto-generates
/// <c>LCE-{year}-{seq:000000}</c> per year. A retry loop tolerates
/// concurrent registrations colliding on the unique index.
/// </para>
/// <para>
/// <b>Audit.</b> Every successful mutation emits a Critical-severity audit
/// row (legal-framework changes are high-trust events; see CLAUDE.md §5.6).
/// </para>
/// </remarks>
public sealed class LegalChangeEventService : ILegalChangeEventService
{
    /// <summary>Stable audit code emitted on register.</summary>
    public const string AuditRegistered = "LEGAL_CHANGE_EVENT.REGISTERED";

    /// <summary>Stable audit code emitted on modify.</summary>
    public const string AuditModified = "LEGAL_CHANGE_EVENT.MODIFIED";

    /// <summary>Stable audit code emitted on mark-ready.</summary>
    public const string AuditMarkedReady = "LEGAL_CHANGE_EVENT.MARKED_READY";

    /// <summary>Stable audit code emitted on cancel.</summary>
    public const string AuditCancelled = "LEGAL_CHANGE_EVENT.CANCELLED";

    /// <summary>Stable conflict message for non-Draft modify attempts.</summary>
    public const string ModifyOnlyDraftMessage = "LEGAL_CHANGE_EVENT_NOT_DRAFT";

    /// <summary>Stable conflict message for mark-ready attempts on non-Draft rows.</summary>
    public const string MarkReadyOnlyDraftMessage = "LEGAL_CHANGE_EVENT_NOT_DRAFT_FOR_READY";

    /// <summary>Stable conflict message for cancel attempts on Applied rows.</summary>
    public const string CancelAppliedForbiddenMessage = "LEGAL_CHANGE_EVENT_ALREADY_APPLIED";

    private const int MaxCodeRetries = 5;

    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IValidator<LegalChangeEventRegisterInputDto> _registerValidator;
    private readonly IValidator<LegalChangeEventModifyInputDto> _modifyValidator;
    private readonly IValidator<LegalChangeEventReasonInputDto> _reasonValidator;
    private readonly IValidator<LegalChangeEventFilterDto> _filterValidator;

    /// <summary>Constructs the service.</summary>
    /// <param name="db">EF Core writer context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated-caller context.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="registerValidator">Register-input validator.</param>
    /// <param name="modifyValidator">Modify-input validator.</param>
    /// <param name="reasonValidator">Reason-input validator (cancel).</param>
    /// <param name="filterValidator">Filter validator (list).</param>
    public LegalChangeEventService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<LegalChangeEventRegisterInputDto> registerValidator,
        IValidator<LegalChangeEventModifyInputDto> modifyValidator,
        IValidator<LegalChangeEventReasonInputDto> reasonValidator,
        IValidator<LegalChangeEventFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(registerValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _registerValidator = registerValidator;
        _modifyValidator = modifyValidator;
        _reasonValidator = reasonValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<LegalChangeEventDto>> RegisterAsync(
        LegalChangeEventRegisterInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var v = await _registerValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<LegalChangeEventDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        if (!Enum.TryParse<LegalChangeScope>(input.Scope, ignoreCase: false, out var scope))
        {
            return Result<LegalChangeEventDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Scope must be a known LegalChangeScope enum-name.");
        }

        var benefitTypes = scope == LegalChangeScope.All
            ? Enum.GetNames<BenefitType>().ToList()
            : input.BenefitTypesInScope.Distinct(StringComparer.Ordinal).ToList();

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";
        LegalChangeEvent? created = null;
        DbUpdateException? lastConflict = null;

        var explicitCode = !string.IsNullOrWhiteSpace(input.Code);
        for (var attempt = 0; attempt < MaxCodeRetries; attempt++)
        {
            string code;
            if (explicitCode)
            {
                code = input.Code!;
                var clash = await _db.LegalChangeEvents
                    .AnyAsync(e => e.Code == code, cancellationToken)
                    .ConfigureAwait(false);
                if (clash)
                {
                    return Result<LegalChangeEventDto>.Failure(
                        ErrorCodes.Conflict, "Code already in use.");
                }
            }
            else
            {
                var year = input.EffectiveFrom.Year;
                var prefix = $"LCE-{year}-";
                var count = await _db.LegalChangeEvents
                    .CountAsync(e => e.Code.StartsWith(prefix), cancellationToken)
                    .ConfigureAwait(false);
                code = $"{prefix}{(count + 1):D6}";
            }

            var entity = new LegalChangeEvent
            {
                Code = code,
                Title = input.Title,
                Description = input.Description,
                EffectiveFrom = input.EffectiveFrom,
                Scope = scope,
                BenefitTypesInScope = benefitTypes,
                ChangePayloadJson = input.ChangePayloadJson,
                Status = LegalChangeEventStatus.Draft,
                RegisteredByUserId = (int)(_caller.UserId ?? 0),
                CreatedAtUtc = now,
                CreatedBy = actor,
                IsActive = true,
            };
            _db.LegalChangeEvents.Add(entity);

            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                created = entity;
                break;
            }
            catch (DbUpdateException ex)
            {
                lastConflict = ex;
                _db.LegalChangeEvents.Remove(entity);
                if (explicitCode)
                {
                    break;
                }
            }
        }

        if (created is null)
        {
            return Result<LegalChangeEventDto>.Failure(
                ErrorCodes.Conflict,
                lastConflict?.Message ?? "Code generation contention exceeded retry budget.");
        }

        var details = JsonSerializer.Serialize(new
        {
            eventSqid = _sqids.Encode(created.Id),
            code = created.Code,
            scope = created.Scope.ToString(),
            effectiveFrom = created.EffectiveFrom.ToString("yyyy-MM-dd"),
            benefitTypesInScopeCount = created.BenefitTypesInScope.Count,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditRegistered, AuditSeverity.Critical, actor,
            nameof(LegalChangeEvent), created.Id, details,
            _caller.SourceIp, _caller.CorrelationId, cancellationToken).ConfigureAwait(false);

        CnasMeter.LegalChangeEventRegistered.Add(1);

        return Result<LegalChangeEventDto>.Success(ToDto(created));
    }

    /// <inheritdoc />
    public async Task<Result<LegalChangeEventDto>> ModifyAsync(
        string sqid,
        LegalChangeEventModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var v = await _modifyValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<LegalChangeEventDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<LegalChangeEventDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var evt = await _db.LegalChangeEvents
            .FirstOrDefaultAsync(e => e.Id == decoded.Value && e.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (evt is null)
        {
            return Result<LegalChangeEventDto>.Failure(ErrorCodes.NotFound, "Legal-change event not found.");
        }
        if (evt.Status != LegalChangeEventStatus.Draft)
        {
            return Result<LegalChangeEventDto>.Failure(ErrorCodes.Conflict, ModifyOnlyDraftMessage);
        }

        if (input.Title is not null)
        {
            evt.Title = input.Title;
        }
        if (input.Description is not null)
        {
            evt.Description = input.Description;
        }
        if (input.EffectiveFrom.HasValue)
        {
            evt.EffectiveFrom = input.EffectiveFrom.Value;
        }
        if (input.Scope is not null)
        {
            if (!Enum.TryParse<LegalChangeScope>(input.Scope, ignoreCase: false, out var newScope))
            {
                return Result<LegalChangeEventDto>.Failure(
                    ErrorCodes.ValidationFailed,
                    "Scope must be a known LegalChangeScope enum-name.");
            }
            evt.Scope = newScope;
            if (newScope == LegalChangeScope.All)
            {
                evt.BenefitTypesInScope = Enum.GetNames<BenefitType>().ToList();
            }
        }
        if (input.BenefitTypesInScope is not null && evt.Scope != LegalChangeScope.All)
        {
            evt.BenefitTypesInScope = input.BenefitTypesInScope.Distinct(StringComparer.Ordinal).ToList();
        }
        if (input.ChangePayloadJson is not null)
        {
            evt.ChangePayloadJson = input.ChangePayloadJson;
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";
        evt.UpdatedAtUtc = now;
        evt.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            eventSqid = _sqids.Encode(evt.Id),
            changeReason = input.ChangeReason,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditModified, AuditSeverity.Critical, actor,
            nameof(LegalChangeEvent), evt.Id, details,
            _caller.SourceIp, _caller.CorrelationId, cancellationToken).ConfigureAwait(false);

        return Result<LegalChangeEventDto>.Success(ToDto(evt));
    }

    /// <inheritdoc />
    public async Task<Result<LegalChangeEventDto>> MarkReadyAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<LegalChangeEventDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var evt = await _db.LegalChangeEvents
            .FirstOrDefaultAsync(e => e.Id == decoded.Value && e.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (evt is null)
        {
            return Result<LegalChangeEventDto>.Failure(ErrorCodes.NotFound, "Legal-change event not found.");
        }
        if (evt.Status != LegalChangeEventStatus.Draft)
        {
            return Result<LegalChangeEventDto>.Failure(ErrorCodes.Conflict, MarkReadyOnlyDraftMessage);
        }

        if (!string.IsNullOrWhiteSpace(evt.ChangePayloadJson))
        {
            try
            {
                using var _ = JsonDocument.Parse(evt.ChangePayloadJson);
            }
            catch (JsonException)
            {
                return Result<LegalChangeEventDto>.Failure(
                    ErrorCodes.ValidationFailed,
                    "Stored ChangePayloadJson is not valid JSON; modify the event before marking ready.");
            }
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";
        evt.Status = LegalChangeEventStatus.Ready;
        evt.UpdatedAtUtc = now;
        evt.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            eventSqid = _sqids.Encode(evt.Id),
            code = evt.Code,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditMarkedReady, AuditSeverity.Critical, actor,
            nameof(LegalChangeEvent), evt.Id, details,
            _caller.SourceIp, _caller.CorrelationId, cancellationToken).ConfigureAwait(false);

        return Result<LegalChangeEventDto>.Success(ToDto(evt));
    }

    /// <inheritdoc />
    public async Task<Result<LegalChangeEventDto>> CancelAsync(
        string sqid,
        LegalChangeEventReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var v = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<LegalChangeEventDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<LegalChangeEventDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var evt = await _db.LegalChangeEvents
            .FirstOrDefaultAsync(e => e.Id == decoded.Value && e.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (evt is null)
        {
            return Result<LegalChangeEventDto>.Failure(ErrorCodes.NotFound, "Legal-change event not found.");
        }
        if (evt.Status == LegalChangeEventStatus.Applied)
        {
            return Result<LegalChangeEventDto>.Failure(ErrorCodes.Conflict, CancelAppliedForbiddenMessage);
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";
        evt.Status = LegalChangeEventStatus.Cancelled;
        evt.CancellationReason = input.Reason;
        evt.UpdatedAtUtc = now;
        evt.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            eventSqid = _sqids.Encode(evt.Id),
            reason = input.Reason,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCancelled, AuditSeverity.Critical, actor,
            nameof(LegalChangeEvent), evt.Id, details,
            _caller.SourceIp, _caller.CorrelationId, cancellationToken).ConfigureAwait(false);

        return Result<LegalChangeEventDto>.Success(ToDto(evt));
    }

    /// <inheritdoc />
    public async Task<Result<LegalChangeEventDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<LegalChangeEventDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var evt = await _db.LegalChangeEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == decoded.Value && e.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (evt is null)
        {
            return Result<LegalChangeEventDto>.Failure(ErrorCodes.NotFound, "Legal-change event not found.");
        }
        return Result<LegalChangeEventDto>.Success(ToDto(evt));
    }

    /// <inheritdoc />
    public async Task<Result<LegalChangeEventPageDto>> ListAsync(
        LegalChangeEventFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<LegalChangeEventPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<LegalChangeEvent> q = _db.LegalChangeEvents.AsNoTracking().Where(e => e.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<LegalChangeEventStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(e => e.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.Scope)
            && Enum.TryParse<LegalChangeScope>(filter.Scope, ignoreCase: false, out var scope))
        {
            q = q.Where(e => e.Scope == scope);
        }
        if (filter.EffectiveFromAfter.HasValue)
        {
            var bound = filter.EffectiveFromAfter.Value;
            q = q.Where(e => e.EffectiveFrom >= bound);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(e => e.EffectiveFrom)
            .ThenByDescending(e => e.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result<LegalChangeEventPageDto>.Success(new LegalChangeEventPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take));
    }

    /// <summary>Projects an entity to its DTO form.</summary>
    /// <param name="evt">Persisted row.</param>
    /// <returns>Wire DTO.</returns>
    private LegalChangeEventDto ToDto(LegalChangeEvent evt)
        => new(
            Id: _sqids.Encode(evt.Id),
            Code: evt.Code,
            Title: evt.Title,
            Description: evt.Description,
            EffectiveFrom: evt.EffectiveFrom,
            Scope: evt.Scope.ToString(),
            BenefitTypesInScope: evt.BenefitTypesInScope.ToList(),
            ChangePayloadJson: evt.ChangePayloadJson,
            Status: evt.Status.ToString(),
            RegisteredAt: evt.CreatedAtUtc,
            CancellationReason: evt.CancellationReason);
}
