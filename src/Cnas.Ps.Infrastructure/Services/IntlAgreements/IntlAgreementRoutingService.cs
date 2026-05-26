using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.IntlAgreements;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.IntlAgreements;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — production implementation of
/// <see cref="IIntlAgreementRoutingService"/>. Owns the 3-level routing
/// state machine and delegates per-benefit-kind reviewer role lookups +
/// evidence-shape checks to the injected
/// <see cref="IIntlAgreementRoutingPolicy"/> collection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit + metric.</b> Every lifecycle transition emits the stable
/// audit code at <see cref="AuditSeverity.Critical"/> severity per
/// CLAUDE.md §5.6 (PII / sensitive data). Metrics fire alongside the audit
/// row so operators can chart per-benefit-kind volume + per-level decision
/// distribution.
/// </para>
/// <para>
/// <b>PII safety.</b> Audit payloads NEVER contain the plaintext IDNP —
/// only the Sqid of the case + the deterministic-hash fingerprint of the
/// IDNP + stable codes + statuses. Reviewer notes are referenced by the
/// step Sqid; the note body is redacted out of audit / log payloads.
/// </para>
/// </remarks>
public sealed class IntlAgreementRoutingService : IIntlAgreementRoutingService
{
    /// <summary>Stable audit event code emitted when a case is created.</summary>
    public const string AuditCreated = "INTL_AGREEMENT.CREATED";

    /// <summary>Stable audit event code emitted when a case is submitted to level 1.</summary>
    public const string AuditSubmitted = "INTL_AGREEMENT.SUBMITTED";

    /// <summary>Stable audit event-code prefix for a recorded review (level + outcome suffixed).</summary>
    public const string AuditReviewPrefix = "INTL_AGREEMENT.REVIEW";

    /// <summary>Stable audit event code emitted on a re-submit after revision.</summary>
    public const string AuditResubmitted = "INTL_AGREEMENT.RESUBMITTED";

    /// <summary>Stable audit event code emitted when a case is cancelled.</summary>
    public const string AuditCancelled = "INTL_AGREEMENT.CANCELLED";

    /// <summary>Stable conflict message for invalid state transitions.</summary>
    public const string InvalidTransitionMessage = "INTL_AGREEMENT.INVALID_TRANSITION";

    /// <summary>Stable forbidden message returned when the caller lacks the level's reviewer role.</summary>
    public const string WrongReviewerRoleMessage = "INTL_AGREEMENT.WRONG_REVIEWER_ROLE";

    /// <summary>Stable conflict message when the case-number generator exceeds its retry budget.</summary>
    public const string CaseNumberGenerationFailedMessage = "INTL_AGREEMENT.CASE_NUMBER_GENERATION_FAILED";

    /// <summary>Stable conflict message when no policy is registered for the case's benefit kind.</summary>
    public const string PolicyNotRegisteredMessage = "INTL_AGREEMENT.POLICY_NOT_REGISTERED";

    /// <summary>Maximum re-attempts for the case-number generator under concurrent contention.</summary>
    private const int MaxCaseNumberRetries = 5;

    /// <summary>Cached JSON serializer options shared across audit-payload builders.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IDeterministicHasher _hasher;
    private readonly IReadOnlyDictionary<IntlAgreementBenefitKind, IIntlAgreementRoutingPolicy> _policies;
    private readonly IValidator<IntlAgreementReviewCaseCreateInputDto> _createValidator;
    private readonly IValidator<IntlAgreementReviewInputDto> _reviewValidator;
    private readonly IValidator<IntlAgreementReviewCaseResubmitInputDto> _resubmitValidator;
    private readonly IValidator<IntlAgreementReviewCaseReasonInputDto> _reasonValidator;
    private readonly IValidator<IntlAgreementReviewCaseFilterDto> _filterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">EF writer context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated-caller context.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="hasher">Deterministic hasher used to maintain the IDNP shadow hash column.</param>
    /// <param name="policies">Registered per-benefit-kind routing policies.</param>
    /// <param name="createValidator">Validator for create input.</param>
    /// <param name="reviewValidator">Validator for review input.</param>
    /// <param name="resubmitValidator">Validator for resubmit input.</param>
    /// <param name="reasonValidator">Validator for cancel input.</param>
    /// <param name="filterValidator">Validator for filter input.</param>
    public IntlAgreementRoutingService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IDeterministicHasher hasher,
        IEnumerable<IIntlAgreementRoutingPolicy> policies,
        IValidator<IntlAgreementReviewCaseCreateInputDto> createValidator,
        IValidator<IntlAgreementReviewInputDto> reviewValidator,
        IValidator<IntlAgreementReviewCaseResubmitInputDto> resubmitValidator,
        IValidator<IntlAgreementReviewCaseReasonInputDto> reasonValidator,
        IValidator<IntlAgreementReviewCaseFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(reviewValidator);
        ArgumentNullException.ThrowIfNull(resubmitValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _hasher = hasher;
        _policies = policies.ToDictionary(p => p.BenefitKind);
        _createValidator = createValidator;
        _reviewValidator = reviewValidator;
        _resubmitValidator = resubmitValidator;
        _reasonValidator = reasonValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<IntlAgreementReviewCaseDto>> CreateAsync(
        IntlAgreementReviewCaseCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var benefitKind = Enum.Parse<IntlAgreementBenefitKind>(input.BenefitKind, ignoreCase: false);
        if (!_policies.TryGetValue(benefitKind, out var policy))
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.Conflict, PolicyNotRegisteredMessage);
        }

        var evidenceCheck = policy.ValidateEvidence(input.EvidenceJson);
        if (evidenceCheck.IsFailure)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                evidenceCheck.ErrorCode!, evidenceCheck.ErrorMessage!);
        }

        var canonicalIdnp = input.BeneficiaryIdnp.Trim().ToUpperInvariant();
        var idnpHash = _hasher.ComputeHash(canonicalIdnp);
        var now = _clock.UtcNow;
        var year = now.Year;

        IntlAgreementReviewCase? created = null;
        DbUpdateException? lastFailure = null;
        for (var attempt = 0; attempt < MaxCaseNumberRetries; attempt++)
        {
            var prefix = $"IAR-{year}-";
            var existingCount = await _db.IntlAgreementReviewCases
                .CountAsync(r => r.CaseNumber.StartsWith(prefix), cancellationToken)
                .ConfigureAwait(false);
            var caseNumber = $"{prefix}{(existingCount + 1 + attempt):D6}";

            var entity = new IntlAgreementReviewCase
            {
                CaseNumber = caseNumber,
                BenefitKind = benefitKind,
                BeneficiaryIdnp = canonicalIdnp,
                BeneficiaryIdnpHash = idnpHash,
                BeneficiaryDisplayName = input.BeneficiaryDisplayName,
                AgreementCode = input.AgreementCode,
                HostCountryCode = input.HostCountryCode,
                Status = IntlAgreementReviewCaseStatus.Draft,
                CurrentLevel = IntlAgreementReviewLevel.Local,
                ReferenceBenefitPassportSqid = input.ReferenceBenefitPassportSqid,
                EvidenceJson = input.EvidenceJson,
                RegisteredByUserId = (int)(_caller.UserId ?? 0),
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.IntlAgreementReviewCases.Add(entity);

            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                created = entity;
                break;
            }
            catch (DbUpdateException ex)
            {
                lastFailure = ex;
                _db.IntlAgreementReviewCases.Remove(entity);
            }
        }
        if (created is null)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.Conflict,
                lastFailure?.Message ?? CaseNumberGenerationFailedMessage);
        }

        await EmitAuditAsync(
            AuditCreated,
            created,
            extra: new
            {
                benefitKind = created.BenefitKind.ToString(),
                agreementCode = created.AgreementCode,
                hostCountryCode = created.HostCountryCode,
            },
            cancellationToken).ConfigureAwait(false);

        CnasMeter.IntlAgreementCaseCreated.Add(
            1,
            new KeyValuePair<string, object?>("benefit_kind", benefitKind.ToString()));

        return Result<IntlAgreementReviewCaseDto>.Success(
            await ToDtoAsync(created, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<IntlAgreementReviewCaseDto>> SubmitAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var @case = loaded.Value;
        if (@case.Status != IntlAgreementReviewCaseStatus.Draft)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        var now = _clock.UtcNow;
        @case.Status = IntlAgreementReviewCaseStatus.AtLocalReview;
        @case.CurrentLevel = IntlAgreementReviewLevel.Local;
        @case.SubmittedAt = now;
        @case.UpdatedAtUtc = now;
        @case.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            AuditSubmitted,
            @case,
            extra: null,
            cancellationToken).ConfigureAwait(false);

        CnasMeter.IntlAgreementCaseSubmitted.Add(
            1,
            new KeyValuePair<string, object?>("benefit_kind", @case.BenefitKind.ToString()));

        return Result<IntlAgreementReviewCaseDto>.Success(
            await ToDtoAsync(@case, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<IntlAgreementReviewCaseDto>> RecordReviewAsync(
        string sqid,
        IntlAgreementReviewInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reviewValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var loaded = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var @case = loaded.Value;

        if (@case.Status != IntlAgreementReviewCaseStatus.AtLocalReview
            && @case.Status != IntlAgreementReviewCaseStatus.AtRegionalReview
            && @case.Status != IntlAgreementReviewCaseStatus.AtNationalReview)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        if (!_policies.TryGetValue(@case.BenefitKind, out var policy))
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.Conflict, PolicyNotRegisteredMessage);
        }

        // Resolve the reviewer role required for the case's current level.
        var requiredRole = @case.CurrentLevel switch
        {
            IntlAgreementReviewLevel.Local => policy.LocalReviewerRoleCode,
            IntlAgreementReviewLevel.Regional => policy.RegionalReviewerRoleCode,
            IntlAgreementReviewLevel.National => policy.NationalReviewerRoleCode,
            _ => null,
        };
        if (requiredRole is null)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.Conflict, InvalidTransitionMessage);
        }
        if (!_caller.Roles.Contains(requiredRole, StringComparer.Ordinal))
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.Forbidden, WrongReviewerRoleMessage);
        }

        var outcome = Enum.Parse<IntlAgreementReviewStepOutcome>(input.Outcome, ignoreCase: false);
        var now = _clock.UtcNow;

        // Persist the immutable step row first — it captures the level + outcome at decision time.
        var step = new IntlAgreementReviewStep
        {
            CaseId = @case.Id,
            Level = @case.CurrentLevel,
            Outcome = outcome,
            ReviewedAt = now,
            ReviewedByUserId = (int)(_caller.UserId ?? 0),
            Note = input.Note,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.IntlAgreementReviewSteps.Add(step);

        var levelAtDecision = @case.CurrentLevel;
        var benefitKindTag = @case.BenefitKind.ToString();
        var terminal = false;
        var terminalTag = string.Empty;

        // Transition the case based on the outcome + current level.
        switch (outcome)
        {
            case IntlAgreementReviewStepOutcome.Approved:
                switch (@case.CurrentLevel)
                {
                    case IntlAgreementReviewLevel.Local:
                        @case.CurrentLevel = IntlAgreementReviewLevel.Regional;
                        @case.Status = IntlAgreementReviewCaseStatus.AtRegionalReview;
                        break;
                    case IntlAgreementReviewLevel.Regional:
                        @case.CurrentLevel = IntlAgreementReviewLevel.National;
                        @case.Status = IntlAgreementReviewCaseStatus.AtNationalReview;
                        break;
                    case IntlAgreementReviewLevel.National:
                        @case.CurrentLevel = IntlAgreementReviewLevel.Complete;
                        @case.Status = IntlAgreementReviewCaseStatus.Approved;
                        @case.ApprovedAt = now;
                        terminal = true;
                        terminalTag = nameof(IntlAgreementReviewCaseStatus.Approved);
                        break;
                }
                break;
            case IntlAgreementReviewStepOutcome.Rejected:
                @case.Status = IntlAgreementReviewCaseStatus.Rejected;
                @case.RejectedAt = now;
                @case.RejectionReason = input.Note;
                terminal = true;
                terminalTag = nameof(IntlAgreementReviewCaseStatus.Rejected);
                break;
            case IntlAgreementReviewStepOutcome.RevisionRequested:
                @case.Status = IntlAgreementReviewCaseStatus.RevisionRequested;
                @case.CurrentLevel = IntlAgreementReviewLevel.RevisionRequired;
                @case.RevisionRequestedAt = now;
                @case.RevisionRequestNote = input.Note;
                break;
        }

        @case.UpdatedAtUtc = now;
        @case.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var auditCode = $"{AuditReviewPrefix}.{levelAtDecision.ToString().ToUpperInvariant()}." +
            outcome.ToString().ToUpperInvariant();
        await EmitAuditAsync(
            auditCode,
            @case,
            extra: new
            {
                level = levelAtDecision.ToString(),
                outcome = outcome.ToString(),
                stepSqid = _sqids.Encode(step.Id),
            },
            cancellationToken).ConfigureAwait(false);

        CnasMeter.IntlAgreementReviewRecorded.Add(
            1,
            new KeyValuePair<string, object?>("benefit_kind", benefitKindTag),
            new KeyValuePair<string, object?>("level", levelAtDecision.ToString()),
            new KeyValuePair<string, object?>("outcome", outcome.ToString()));

        if (terminal)
        {
            CnasMeter.IntlAgreementCaseFinalised.Add(
                1,
                new KeyValuePair<string, object?>("benefit_kind", benefitKindTag),
                new KeyValuePair<string, object?>("terminal_status", terminalTag));
        }

        return Result<IntlAgreementReviewCaseDto>.Success(
            await ToDtoAsync(@case, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<IntlAgreementReviewCaseDto>> ResubmitAsync(
        string sqid,
        IntlAgreementReviewCaseResubmitInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _resubmitValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var loaded = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var @case = loaded.Value;
        if (@case.Status != IntlAgreementReviewCaseStatus.RevisionRequested)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        if (!_policies.TryGetValue(@case.BenefitKind, out var policy))
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.Conflict, PolicyNotRegisteredMessage);
        }

        if (input.EvidenceJson is not null)
        {
            var evidenceCheck = policy.ValidateEvidence(input.EvidenceJson);
            if (evidenceCheck.IsFailure)
            {
                return Result<IntlAgreementReviewCaseDto>.Failure(
                    evidenceCheck.ErrorCode!, evidenceCheck.ErrorMessage!);
            }
            @case.EvidenceJson = input.EvidenceJson;
        }

        var now = _clock.UtcNow;
        @case.Status = IntlAgreementReviewCaseStatus.AtLocalReview;
        @case.CurrentLevel = IntlAgreementReviewLevel.Local;
        @case.SubmittedAt = now;
        @case.RevisionRequestedAt = null;
        @case.RevisionRequestNote = null;
        @case.UpdatedAtUtc = now;
        @case.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            AuditResubmitted,
            @case,
            extra: null,
            cancellationToken).ConfigureAwait(false);

        CnasMeter.IntlAgreementCaseSubmitted.Add(
            1,
            new KeyValuePair<string, object?>("benefit_kind", @case.BenefitKind.ToString()));

        return Result<IntlAgreementReviewCaseDto>.Success(
            await ToDtoAsync(@case, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<IntlAgreementReviewCaseDto>> CancelAsync(
        string sqid,
        IntlAgreementReviewCaseReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var loaded = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var @case = loaded.Value;
        if (@case.Status == IntlAgreementReviewCaseStatus.Approved
            || @case.Status == IntlAgreementReviewCaseStatus.Rejected
            || @case.Status == IntlAgreementReviewCaseStatus.Cancelled)
        {
            return Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        var now = _clock.UtcNow;
        @case.Status = IntlAgreementReviewCaseStatus.Cancelled;
        @case.CancelledAt = now;
        @case.CancelReason = input.Reason;
        @case.UpdatedAtUtc = now;
        @case.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            AuditCancelled,
            @case,
            extra: null,
            cancellationToken).ConfigureAwait(false);

        CnasMeter.IntlAgreementCaseFinalised.Add(
            1,
            new KeyValuePair<string, object?>("benefit_kind", @case.BenefitKind.ToString()),
            new KeyValuePair<string, object?>("terminal_status", nameof(IntlAgreementReviewCaseStatus.Cancelled)));

        return Result<IntlAgreementReviewCaseDto>.Success(
            await ToDtoAsync(@case, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<IntlAgreementReviewCaseDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess
            ? Result<IntlAgreementReviewCaseDto>.Success(
                await ToDtoAsync(loaded.Value, cancellationToken).ConfigureAwait(false))
            : Result<IntlAgreementReviewCaseDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
    }

    /// <inheritdoc />
    public async Task<Result<IntlAgreementReviewCasePageDto>> ListAsync(
        IntlAgreementReviewCaseFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<IntlAgreementReviewCasePageDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<IntlAgreementReviewCase> q = _db.IntlAgreementReviewCases
            .Where(r => r.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<IntlAgreementReviewCaseStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(r => r.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.BenefitKind)
            && Enum.TryParse<IntlAgreementBenefitKind>(filter.BenefitKind, ignoreCase: false, out var bk))
        {
            q = q.Where(r => r.BenefitKind == bk);
        }
        if (!string.IsNullOrWhiteSpace(filter.CurrentLevel)
            && Enum.TryParse<IntlAgreementReviewLevel>(filter.CurrentLevel, ignoreCase: false, out var lvl))
        {
            q = q.Where(r => r.CurrentLevel == lvl);
        }
        if (!string.IsNullOrWhiteSpace(filter.AgreementCode))
        {
            q = q.Where(r => r.AgreementCode == filter.AgreementCode);
        }
        if (!string.IsNullOrWhiteSpace(filter.HostCountryCode))
        {
            q = q.Where(r => r.HostCountryCode == filter.HostCountryCode);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = new List<IntlAgreementReviewCaseDto>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(await ToDtoAsync(row, cancellationToken).ConfigureAwait(false));
        }
        return Result<IntlAgreementReviewCasePageDto>.Success(new IntlAgreementReviewCasePageDto(
            Items: items,
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take));
    }

    /// <summary>Loads a tracked case by Sqid; returns NotFound when missing or soft-deleted.</summary>
    /// <param name="sqid">Sqid-encoded case id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result carrying the entity when found.</returns>
    private async Task<Result<IntlAgreementReviewCase>> LoadAsync(string sqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<IntlAgreementReviewCase>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var entity = await _db.IntlAgreementReviewCases
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return entity is null
            ? Result<IntlAgreementReviewCase>.Failure(ErrorCodes.NotFound, "International-agreements case not found.")
            : Result<IntlAgreementReviewCase>.Success(entity);
    }

    /// <summary>Emits the stable audit row attached to the case.</summary>
    /// <param name="code">Stable audit code.</param>
    /// <param name="case">Loaded entity.</param>
    /// <param name="extra">Optional extra fields merged into the JSON payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task EmitAuditAsync(
        string code,
        IntlAgreementReviewCase @case,
        object? extra,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            caseSqid = _sqids.Encode(@case.Id),
            caseNumber = @case.CaseNumber,
            status = @case.Status.ToString(),
            currentLevel = @case.CurrentLevel.ToString(),
            benefitKind = @case.BenefitKind.ToString(),
            beneficiaryIdnpHash = @case.BeneficiaryIdnpHash,
            extra,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            code,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(IntlAgreementReviewCase),
            @case.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects a case entity to the wire DTO (with its review-step history).</summary>
    /// <param name="r">Loaded entity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Wire DTO.</returns>
    private async Task<IntlAgreementReviewCaseDto> ToDtoAsync(
        IntlAgreementReviewCase r,
        CancellationToken cancellationToken)
    {
        var steps = await _db.IntlAgreementReviewSteps
            .Where(s => s.CaseId == r.Id && s.IsActive)
            .OrderBy(s => s.ReviewedAt)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var stepDtos = steps.Select(s => new IntlAgreementReviewStepDto(
            Id: _sqids.Encode(s.Id),
            CaseSqid: _sqids.Encode(s.CaseId),
            Level: s.Level.ToString(),
            Outcome: s.Outcome.ToString(),
            ReviewedAt: s.ReviewedAt,
            ReviewedByUserSqid: s.ReviewedByUserId > 0 ? _sqids.Encode(s.ReviewedByUserId) : null,
            Note: s.Note)).ToList();
        return new IntlAgreementReviewCaseDto(
            Id: _sqids.Encode(r.Id),
            CaseNumber: r.CaseNumber,
            BenefitKind: r.BenefitKind.ToString(),
            BeneficiaryIdnpHash: r.BeneficiaryIdnpHash,
            BeneficiaryDisplayName: r.BeneficiaryDisplayName,
            AgreementCode: r.AgreementCode,
            HostCountryCode: r.HostCountryCode,
            Status: r.Status.ToString(),
            CurrentLevel: r.CurrentLevel.ToString(),
            ReferenceBenefitPassportSqid: r.ReferenceBenefitPassportSqid,
            SubmittedAt: r.SubmittedAt,
            ApprovedAt: r.ApprovedAt,
            RejectedAt: r.RejectedAt,
            RejectionReason: r.RejectionReason,
            RevisionRequestedAt: r.RevisionRequestedAt,
            RevisionRequestNote: r.RevisionRequestNote,
            CancelledAt: r.CancelledAt,
            CancelReason: r.CancelReason,
            EvidenceJson: r.EvidenceJson,
            RegisteredAt: r.CreatedAtUtc,
            Steps: stepDtos);
    }
}
