using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Reporting;

/// <summary>
/// R1906 / TOR Annex 6 — production implementation of
/// <see cref="IReportDistributionService"/>. Manages the per-report
/// distribution rule registry, emits Critical-severity audit rows on every
/// mutation, and exposes paged read endpoints over the rule + dispatch
/// ledgers.
/// </summary>
public sealed class ReportDistributionService : IReportDistributionService
{
    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly ISqidService _sqids;
    private readonly IAuditService _audit;
    private readonly IDeterministicHasher _hasher;
    private readonly IValidator<ReportDistributionRuleCreateInputDto> _createValidator;
    private readonly IValidator<ReportDistributionRuleModifyInputDto> _modifyValidator;
    private readonly IValidator<ReportDistributionReasonInputDto> _reasonValidator;
    private readonly IValidator<ReportDistributionRuleFilterDto> _ruleFilterValidator;
    private readonly IValidator<ReportDispatchFilterDto> _dispatchFilterValidator;

    /// <summary>Stable audit code for rule creation.</summary>
    public const string AuditCodeRuleCreated = "REPORT_DIST.RULE_CREATED";

    /// <summary>Stable audit code for rule modification.</summary>
    public const string AuditCodeRuleModified = "REPORT_DIST.RULE_MODIFIED";

    /// <summary>Stable audit code for rule disable.</summary>
    public const string AuditCodeRuleDisabled = "REPORT_DIST.RULE_DISABLED";

    /// <summary>Stable audit code for rule enable.</summary>
    public const string AuditCodeRuleEnabled = "REPORT_DIST.RULE_ENABLED";

    /// <summary>Stable audit code for rule (soft) delete.</summary>
    public const string AuditCodeRuleDeleted = "REPORT_DIST.RULE_DELETED";

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer DB context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="caller">Caller context for audit attribution.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="audit">Audit service for the <c>REPORT_DIST.*</c> codes.</param>
    /// <param name="hasher">Deterministic hasher used to populate the email shadow column.</param>
    /// <param name="createValidator">Create-input validator.</param>
    /// <param name="modifyValidator">Modify-input validator.</param>
    /// <param name="reasonValidator">Disable/enable/delete reason validator.</param>
    /// <param name="ruleFilterValidator">Rule-filter validator.</param>
    /// <param name="dispatchFilterValidator">Dispatch-filter validator.</param>
    public ReportDistributionService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ICallerContext caller,
        ISqidService sqids,
        IAuditService audit,
        IDeterministicHasher hasher,
        IValidator<ReportDistributionRuleCreateInputDto> createValidator,
        IValidator<ReportDistributionRuleModifyInputDto> modifyValidator,
        IValidator<ReportDistributionReasonInputDto> reasonValidator,
        IValidator<ReportDistributionRuleFilterDto> ruleFilterValidator,
        IValidator<ReportDispatchFilterDto> dispatchFilterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(ruleFilterValidator);
        ArgumentNullException.ThrowIfNull(dispatchFilterValidator);
        _db = db;
        _clock = clock;
        _caller = caller;
        _sqids = sqids;
        _audit = audit;
        _hasher = hasher;
        _createValidator = createValidator;
        _modifyValidator = modifyValidator;
        _reasonValidator = reasonValidator;
        _ruleFilterValidator = ruleFilterValidator;
        _dispatchFilterValidator = dispatchFilterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<ReportDistributionRuleDto>> CreateRuleAsync(
        ReportDistributionRuleCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ReportDistributionRuleDto>.Failure(ErrorCodes.ValidationFailed, v.ToString());
        }

        var channel = Enum.Parse<ReportDistributionChannel>(input.Channel);
        var recipientKind = Enum.Parse<ReportRecipientKind>(input.RecipientKind);
        var format = Enum.Parse<ReportDeliveryFormat>(input.Format);
        var priority = Enum.Parse<ReportDeliveryPriority>(input.Priority);

        var hash = recipientKind == ReportRecipientKind.EmailAddress
            ? _hasher.ComputeHash(input.RecipientCode)
            : null;

        // Duplicate-rule guard. The composite unique index enforces this in
        // production; in tests with the EF in-memory provider we perform an
        // explicit pre-flight check so the contract holds without the index.
        var alreadyExists = await _db.ReportDistributionRules
            .AnyAsync(r => r.ReportCode == input.ReportCode
                && r.Channel == channel
                && r.RecipientKind == recipientKind
                && (hash != null ? r.RecipientCodeHash == hash : true)
                && r.IsActive, cancellationToken).ConfigureAwait(false);
        if (alreadyExists)
        {
            return Result<ReportDistributionRuleDto>.Failure(
                ErrorCodes.Conflict,
                "An active rule for the same (report, channel, kind, recipient) tuple already exists.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var rule = new ReportDistributionRule
        {
            ReportCode = input.ReportCode,
            Channel = channel,
            RecipientKind = recipientKind,
            RecipientCode = input.RecipientCode,
            RecipientCodeHash = hash,
            Format = format,
            Priority = priority,
            EffectiveFrom = input.EffectiveFrom,
            EffectiveUntil = input.EffectiveUntil,
            CreatedByUserId = _caller.UserId ?? 0,
            Notes = input.Notes,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.ReportDistributionRules.Add(rule);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            ruleId = rule.Id,
            reportCode = rule.ReportCode,
            channel = rule.Channel.ToString(),
            recipientKind = rule.RecipientKind.ToString(),
        });
        await _audit.RecordAsync(
            AuditCodeRuleCreated,
            AuditSeverity.Critical,
            actor,
            nameof(ReportDistributionRule),
            rule.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
        CnasMeter.ReportDistributionRuleCreated.Add(1);

        return Result<ReportDistributionRuleDto>.Success(ToDto(rule));
    }

    /// <inheritdoc />
    public async Task<Result<ReportDistributionRuleDto>> ModifyRuleAsync(
        string sqid,
        ReportDistributionRuleModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _modifyValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ReportDistributionRuleDto>.Failure(ErrorCodes.ValidationFailed, v.ToString());
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<ReportDistributionRuleDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var rule = await _db.ReportDistributionRules
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (rule is null)
        {
            return Result<ReportDistributionRuleDto>.Failure(ErrorCodes.NotFound, "Distribution rule not found.");
        }

        if (input.Channel is not null) rule.Channel = Enum.Parse<ReportDistributionChannel>(input.Channel);
        if (input.RecipientKind is not null) rule.RecipientKind = Enum.Parse<ReportRecipientKind>(input.RecipientKind);
        if (input.RecipientCode is not null)
        {
            rule.RecipientCode = input.RecipientCode;
            rule.RecipientCodeHash = rule.RecipientKind == ReportRecipientKind.EmailAddress
                ? _hasher.ComputeHash(input.RecipientCode)
                : null;
        }
        if (input.Format is not null) rule.Format = Enum.Parse<ReportDeliveryFormat>(input.Format);
        if (input.Priority is not null) rule.Priority = Enum.Parse<ReportDeliveryPriority>(input.Priority);
        if (input.EffectiveFrom is not null) rule.EffectiveFrom = input.EffectiveFrom.Value;
        if (input.EffectiveUntil is not null) rule.EffectiveUntil = input.EffectiveUntil.Value;
        if (input.Notes is not null) rule.Notes = input.Notes;

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        rule.UpdatedAtUtc = now;
        rule.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            ruleId = rule.Id,
            changeReason = input.ChangeReason,
        });
        await _audit.RecordAsync(
            AuditCodeRuleModified,
            AuditSeverity.Critical,
            actor,
            nameof(ReportDistributionRule),
            rule.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<ReportDistributionRuleDto>.Success(ToDto(rule));
    }

    /// <inheritdoc />
    public Task<Result<ReportDistributionRuleDto>> DisableRuleAsync(
        string sqid,
        ReportDistributionReasonInputDto input,
        CancellationToken cancellationToken = default)
        => TransitionActiveFlagAsync(sqid, input, targetIsActive: false, AuditCodeRuleDisabled, cancellationToken);

    /// <inheritdoc />
    public Task<Result<ReportDistributionRuleDto>> EnableRuleAsync(
        string sqid,
        ReportDistributionReasonInputDto input,
        CancellationToken cancellationToken = default)
        => TransitionActiveFlagAsync(sqid, input, targetIsActive: true, AuditCodeRuleEnabled, cancellationToken);

    /// <summary>
    /// Shared transition path for the enable/disable verbs. Validates the
    /// reason payload, resolves the rule (allowing soft-deleted rows on the
    /// re-enable branch), flips the rule's <c>IsActive</c> flag, and
    /// emits the appropriate audit row.
    /// </summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="targetIsActive">Target IsActive value.</param>
    /// <param name="auditCode">Audit code to emit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    private async Task<Result<ReportDistributionRuleDto>> TransitionActiveFlagAsync(
        string sqid,
        ReportDistributionReasonInputDto input,
        bool targetIsActive,
        string auditCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ReportDistributionRuleDto>.Failure(ErrorCodes.ValidationFailed, v.ToString());
        }
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<ReportDistributionRuleDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var rule = await _db.ReportDistributionRules
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (rule is null)
        {
            return Result<ReportDistributionRuleDto>.Failure(ErrorCodes.NotFound, "Distribution rule not found.");
        }

        rule.IsActive = targetIsActive;
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        rule.UpdatedAtUtc = now;
        rule.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new { ruleId = rule.Id, reason = input.Reason });
        await _audit.RecordAsync(
            auditCode,
            AuditSeverity.Critical,
            actor,
            nameof(ReportDistributionRule),
            rule.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<ReportDistributionRuleDto>.Success(ToDto(rule));
    }

    /// <inheritdoc />
    public async Task<Result> DeleteRuleAsync(
        string sqid,
        ReportDistributionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, v.ToString());
        }
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var rule = await _db.ReportDistributionRules
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (rule is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Distribution rule not found.");
        }
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        rule.IsActive = false;
        rule.UpdatedAtUtc = now;
        rule.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new { ruleId = rule.Id, reason = input.Reason });
        await _audit.RecordAsync(
            AuditCodeRuleDeleted,
            AuditSeverity.Critical,
            actor,
            nameof(ReportDistributionRule),
            rule.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<ReportDistributionRuleDto>> GetRuleByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<ReportDistributionRuleDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var rule = await _db.ReportDistributionRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return rule is null
            ? Result<ReportDistributionRuleDto>.Failure(ErrorCodes.NotFound, "Distribution rule not found.")
            : Result<ReportDistributionRuleDto>.Success(ToDto(rule));
    }

    /// <inheritdoc />
    public async Task<Result<ReportDistributionRulePageDto>> ListRulesAsync(
        ReportDistributionRuleFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _ruleFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ReportDistributionRulePageDto>.Failure(ErrorCodes.ValidationFailed, v.ToString());
        }

        IQueryable<ReportDistributionRule> query = _db.ReportDistributionRules.AsNoTracking();
        if (filter.IsActive is null)
        {
            query = query.IgnoreQueryFilters();
        }
        else
        {
            query = filter.IsActive.Value
                ? query.Where(r => r.IsActive)
                : query.IgnoreQueryFilters().Where(r => !r.IsActive);
        }
        if (!string.IsNullOrWhiteSpace(filter.ReportCode))
        {
            query = query.Where(r => r.ReportCode == filter.ReportCode);
        }
        if (!string.IsNullOrWhiteSpace(filter.Channel)
            && Enum.TryParse<ReportDistributionChannel>(filter.Channel, out var channelEnum))
        {
            query = query.Where(r => r.Channel == channelEnum);
        }
        if (!string.IsNullOrWhiteSpace(filter.RecipientKind)
            && Enum.TryParse<ReportRecipientKind>(filter.RecipientKind, out var kindEnum))
        {
            query = query.Where(r => r.RecipientKind == kindEnum);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result<ReportDistributionRulePageDto>.Success(new ReportDistributionRulePageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take));
    }

    /// <inheritdoc />
    public async Task<Result<ReportDispatchPageDto>> ListDispatchesAsync(
        ReportDispatchFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _dispatchFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ReportDispatchPageDto>.Failure(ErrorCodes.ValidationFailed, v.ToString());
        }

        IQueryable<ReportDistributionDispatch> query = _db.ReportDistributionDispatches.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.ReportRunSqid))
        {
            query = query.Where(d => d.ReportRunSqid == filter.ReportRunSqid);
        }
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<ReportDispatchStatus>(filter.Status, out var statusEnum))
        {
            query = query.Where(d => d.Status == statusEnum);
        }
        if (!string.IsNullOrWhiteSpace(filter.RuleSqid))
        {
            var ruleId = _sqids.TryDecode(filter.RuleSqid);
            if (ruleId.IsSuccess)
            {
                query = query.Where(d => d.RuleId == ruleId.Value);
            }
        }
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(d => d.DispatchedAt)
            .ThenByDescending(d => d.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result<ReportDispatchPageDto>.Success(new ReportDispatchPageDto(
            Items: rows.Select(ToDispatchDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take));
    }

    /// <summary>Projects a rule row to its DTO shape.</summary>
    /// <param name="rule">Persisted row.</param>
    /// <returns>The wire DTO.</returns>
    private ReportDistributionRuleDto ToDto(ReportDistributionRule rule) => new(
        Id: _sqids.Encode(rule.Id),
        ReportCode: rule.ReportCode,
        Channel: rule.Channel.ToString(),
        RecipientKind: rule.RecipientKind.ToString(),
        RecipientCode: rule.RecipientCode,
        Format: rule.Format.ToString(),
        Priority: rule.Priority.ToString(),
        IsActive: rule.IsActive,
        EffectiveFrom: rule.EffectiveFrom,
        EffectiveUntil: rule.EffectiveUntil,
        CreatedAt: rule.CreatedAtUtc,
        Notes: rule.Notes);

    /// <summary>Projects a dispatch row to its DTO shape.</summary>
    /// <param name="dispatch">Persisted row.</param>
    /// <returns>The wire DTO.</returns>
    private ReportDistributionDispatchDto ToDispatchDto(ReportDistributionDispatch dispatch) => new(
        Id: _sqids.Encode(dispatch.Id),
        RuleSqid: _sqids.Encode(dispatch.RuleId),
        ReportRunSqid: dispatch.ReportRunSqid,
        Channel: dispatch.Channel.ToString(),
        RecipientKind: dispatch.RecipientKind.ToString(),
        RecipientCode: dispatch.RecipientCode,
        Status: dispatch.Status.ToString(),
        DispatchedAt: dispatch.DispatchedAt,
        DeliveredAt: dispatch.DeliveredAt,
        FailureReason: dispatch.FailureReason,
        RetryCount: dispatch.RetryCount);
}
