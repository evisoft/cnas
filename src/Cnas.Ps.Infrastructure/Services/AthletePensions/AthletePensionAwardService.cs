using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.AthletePensions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.AthletePensions;

/// <summary>
/// R1403 / TOR §3.6-D — production implementation of
/// <see cref="IAthletePensionAwardService"/>. Owns the award lifecycle and
/// delegates the eligibility evaluation + amount computation to the injected
/// pure-function collaborators.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit + metric.</b> Every lifecycle transition emits the stable audit
/// code at <see cref="AuditSeverity.Critical"/> severity per CLAUDE.md §5.6
/// (PII / financial data). Metrics fire alongside the audit row so operators
/// can chart per-role volume + per-outcome distribution.
/// </para>
/// <para>
/// <b>PII safety.</b> Audit payloads NEVER contain the plaintext IDNP — only
/// the Sqid id of the award and the deterministic-hash fingerprint of the
/// IDNP. The eligibility-notes JSON + the amount-calculator breakdown JSON
/// likewise carry only stable rule codes + enum-name codes + numerics.
/// </para>
/// </remarks>
public sealed class AthletePensionAwardService : IAthletePensionAwardService
{
    /// <summary>Stable audit event code emitted when an award is created.</summary>
    public const string AuditCreated = "ATHLETE_PENSION.CREATED";

    /// <summary>Stable audit event code emitted when a career-record row is added.</summary>
    public const string AuditRecordAdded = "ATHLETE_PENSION.RECORD_ADDED";

    /// <summary>Stable audit event code emitted when a career-record row is verified.</summary>
    public const string AuditRecordVerified = "ATHLETE_PENSION.RECORD_VERIFIED";

    /// <summary>Stable audit event code emitted when an award is submitted.</summary>
    public const string AuditSubmitted = "ATHLETE_PENSION.SUBMITTED";

    /// <summary>Stable audit event code emitted when an eligibility evaluation is run.</summary>
    public const string AuditEligibilityEvaluated = "ATHLETE_PENSION.ELIGIBILITY_EVALUATED";

    /// <summary>Stable audit event code emitted when an award is approved.</summary>
    public const string AuditApproved = "ATHLETE_PENSION.APPROVED";

    /// <summary>Stable audit event code emitted when an award is rejected.</summary>
    public const string AuditRejected = "ATHLETE_PENSION.REJECTED";

    /// <summary>Stable audit event code emitted when an award is activated.</summary>
    public const string AuditActivated = "ATHLETE_PENSION.ACTIVATED";

    /// <summary>Stable audit event code emitted when an award is suspended.</summary>
    public const string AuditSuspended = "ATHLETE_PENSION.SUSPENDED";

    /// <summary>Stable audit event code emitted when an award is resumed.</summary>
    public const string AuditResumed = "ATHLETE_PENSION.RESUMED";

    /// <summary>Stable audit event code emitted when an award is terminated.</summary>
    public const string AuditTerminated = "ATHLETE_PENSION.TERMINATED";

    /// <summary>Stable conflict message for invalid state transitions.</summary>
    public const string InvalidTransitionMessage = "ATHLETE_PENSION.INVALID_TRANSITION";

    /// <summary>Stable conflict message returned when approval is attempted without a positive eligibility verdict.</summary>
    public const string NotEligibleMessage = "ATHLETE_PENSION.NOT_ELIGIBLE";

    /// <summary>Stable conflict message when the award-number generator exceeds its retry budget.</summary>
    public const string AwardNumberGenerationFailedMessage = "ATHLETE_PENSION.AWARD_NUMBER_GENERATION_FAILED";

    /// <summary>Maximum re-attempts for the award-number generator under concurrent contention.</summary>
    private const int MaxAwardNumberRetries = 5;

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
    private readonly IAthletePensionEligibilityEvaluator _evaluator;
    private readonly IAthletePensionAmountCalculator _calculator;
    private readonly IValidator<AthletePensionAwardCreateInputDto> _createValidator;
    private readonly IValidator<AthleteCareerRecordInputDto> _recordValidator;
    private readonly IValidator<AthleteCareerRecordVerificationInputDto> _recordVerificationValidator;
    private readonly IValidator<AthletePensionApprovalInputDto> _approvalValidator;
    private readonly IValidator<AthletePensionActivationInputDto> _activationValidator;
    private readonly IValidator<AthletePensionReasonInputDto> _reasonValidator;
    private readonly IValidator<AthletePensionAwardFilterDto> _filterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">EF writer context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated-caller context.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="hasher">Deterministic hasher used to maintain the IDNP shadow hash column.</param>
    /// <param name="evaluator">Eligibility evaluator (pure function).</param>
    /// <param name="calculator">Amount calculator (pure function).</param>
    /// <param name="createValidator">Validator for create input.</param>
    /// <param name="recordValidator">Validator for career-record input.</param>
    /// <param name="recordVerificationValidator">Validator for record verification input.</param>
    /// <param name="approvalValidator">Validator for approval input.</param>
    /// <param name="activationValidator">Validator for activation input.</param>
    /// <param name="reasonValidator">Validator for reject / suspend / resume / terminate input.</param>
    /// <param name="filterValidator">Validator for filter input.</param>
    public AthletePensionAwardService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IDeterministicHasher hasher,
        IAthletePensionEligibilityEvaluator evaluator,
        IAthletePensionAmountCalculator calculator,
        IValidator<AthletePensionAwardCreateInputDto> createValidator,
        IValidator<AthleteCareerRecordInputDto> recordValidator,
        IValidator<AthleteCareerRecordVerificationInputDto> recordVerificationValidator,
        IValidator<AthletePensionApprovalInputDto> approvalValidator,
        IValidator<AthletePensionActivationInputDto> activationValidator,
        IValidator<AthletePensionReasonInputDto> reasonValidator,
        IValidator<AthletePensionAwardFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(calculator);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(recordValidator);
        ArgumentNullException.ThrowIfNull(recordVerificationValidator);
        ArgumentNullException.ThrowIfNull(approvalValidator);
        ArgumentNullException.ThrowIfNull(activationValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _hasher = hasher;
        _evaluator = evaluator;
        _calculator = calculator;
        _createValidator = createValidator;
        _recordValidator = recordValidator;
        _recordVerificationValidator = recordVerificationValidator;
        _approvalValidator = approvalValidator;
        _activationValidator = activationValidator;
        _reasonValidator = reasonValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<AthletePensionAwardDto>> CreateAsync(
        AthletePensionAwardCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var role = Enum.Parse<AthletePensionRole>(input.Role, ignoreCase: false);
        var sex = Enum.Parse<BeneficiarySex>(input.BeneficiarySex, ignoreCase: false);
        var canonicalIdnp = input.BeneficiaryIdnp.Trim().ToUpperInvariant();
        var idnpHash = _hasher.ComputeHash(canonicalIdnp);
        var now = _clock.UtcNow;
        var year = now.Year;

        AthletePensionAward? created = null;
        DbUpdateException? lastFailure = null;
        for (var attempt = 0; attempt < MaxAwardNumberRetries; attempt++)
        {
            var prefix = $"APE-{year}-";
            var existingCount = await _db.AthletePensionAwards
                .CountAsync(r => r.AwardNumber.StartsWith(prefix), cancellationToken)
                .ConfigureAwait(false);
            var awardNumber = $"{prefix}{(existingCount + 1 + attempt):D6}";

            var entity = new AthletePensionAward
            {
                AwardNumber = awardNumber,
                BeneficiaryIdnp = canonicalIdnp,
                BeneficiaryIdnpHash = idnpHash,
                BeneficiaryDisplayName = input.BeneficiaryDisplayName,
                BeneficiaryBirthDate = input.BeneficiaryBirthDate,
                BeneficiarySex = sex,
                Role = role,
                SportDiscipline = input.SportDiscipline,
                Status = AthletePensionAwardStatus.Draft,
                RequestedAt = now,
                MonthlyAmountMdl = 0m,
                RegulatoryBaseMdl = 0m,
                MultiplierPercent = 0m,
                RegisteredByUserId = (int)(_caller.UserId ?? 0),
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.AthletePensionAwards.Add(entity);

            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                created = entity;
                break;
            }
            catch (DbUpdateException ex)
            {
                lastFailure = ex;
                _db.AthletePensionAwards.Remove(entity);
            }
        }
        if (created is null)
        {
            return Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.Conflict,
                lastFailure?.Message ?? AwardNumberGenerationFailedMessage);
        }

        await EmitAuditAsync(AuditCreated, created, extra: new
            {
                role = created.Role.ToString(),
                sportDiscipline = created.SportDiscipline,
            },
            cancellationToken).ConfigureAwait(false);

        CnasMeter.AthletePensionRequested.Add(1);

        return Result<AthletePensionAwardDto>.Success(await ToDtoAsync(created, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<AthletePensionAwardDto>> AddCareerRecordAsync(
        string sqid,
        AthleteCareerRecordInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _recordValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var awardResult = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (awardResult.IsFailure)
        {
            return Result<AthletePensionAwardDto>.Failure(awardResult.ErrorCode!, awardResult.ErrorMessage!);
        }
        var award = awardResult.Value;
        if (award.Status != AthletePensionAwardStatus.Draft
            && award.Status != AthletePensionAwardStatus.Submitted)
        {
            return Result<AthletePensionAwardDto>.Failure(ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        var kind = Enum.Parse<AthleteAchievementKind>(input.AchievementKind, ignoreCase: false);
        var now = _clock.UtcNow;
        var record = new AthleteCareerRecord
        {
            AwardId = award.Id,
            AchievementKind = kind,
            AchievementYear = input.AchievementYear,
            Event = input.Event,
            Years = input.Years,
            Verified = false,
            EvidenceDocumentReference = input.EvidenceDocumentReference,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.AthleteCareerRecords.Add(record);

        award.UpdatedAtUtc = now;
        award.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(AuditRecordAdded, award, extra: new
            {
                recordSqid = _sqids.Encode(record.Id),
                achievementKind = record.AchievementKind.ToString(),
                achievementYear = record.AchievementYear,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<AthletePensionAwardDto>.Success(await ToDtoAsync(award, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<AthletePensionAwardDto>> VerifyCareerRecordAsync(
        string awardSqid,
        string recordSqid,
        AthleteCareerRecordVerificationInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _recordVerificationValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var awardResult = await LoadAsync(awardSqid, cancellationToken).ConfigureAwait(false);
        if (awardResult.IsFailure)
        {
            return Result<AthletePensionAwardDto>.Failure(awardResult.ErrorCode!, awardResult.ErrorMessage!);
        }
        var award = awardResult.Value;

        var decodedRecord = _sqids.TryDecode(recordSqid);
        if (decodedRecord.IsFailure)
        {
            return Result<AthletePensionAwardDto>.Failure(decodedRecord.ErrorCode!, decodedRecord.ErrorMessage!);
        }
        var record = await _db.AthleteCareerRecords
            .FirstOrDefaultAsync(
                r => r.Id == decodedRecord.Value && r.AwardId == award.Id && r.IsActive,
                cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return Result<AthletePensionAwardDto>.Failure(ErrorCodes.NotFound, "Career record not found.");
        }

        var now = _clock.UtcNow;
        record.Verified = true;
        record.VerifiedAt = now;
        record.VerifiedByUserId = (int)(_caller.UserId ?? 0);
        record.VerificationNote = input.VerificationNote;
        record.UpdatedAtUtc = now;
        record.UpdatedBy = _caller.UserSqid;

        award.UpdatedAtUtc = now;
        award.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(AuditRecordVerified, award, extra: new
            {
                recordSqid = _sqids.Encode(record.Id),
                achievementKind = record.AchievementKind.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<AthletePensionAwardDto>.Success(await ToDtoAsync(award, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task<Result<AthletePensionAwardDto>> SubmitAsync(
        string sqid,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            sqid,
            requiredCurrent: new[] { AthletePensionAwardStatus.Draft },
            target: AthletePensionAwardStatus.Submitted,
            auditCode: AuditSubmitted,
            extras: null,
            extraAuditPayload: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<AthletePensionEligibilityVerdictDto>> EvaluateEligibilityAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var awardResult = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (awardResult.IsFailure)
        {
            return Result<AthletePensionEligibilityVerdictDto>.Failure(awardResult.ErrorCode!, awardResult.ErrorMessage!);
        }
        var award = awardResult.Value;
        var verdict = await EvaluateAsync(award, cancellationToken).ConfigureAwait(false);
        if (verdict.IsFailure)
        {
            return verdict;
        }

        await EmitAuditAsync(AuditEligibilityEvaluated, award, extra: new
            {
                isEligible = verdict.Value.IsEligible,
            },
            cancellationToken).ConfigureAwait(false);

        CnasMeter.AthletePensionEligibilityEvaluated.Add(
            1,
            new KeyValuePair<string, object?>("outcome", verdict.Value.IsEligible ? "eligible" : "ineligible"));

        return verdict;
    }

    /// <inheritdoc />
    public async Task<Result<AthletePensionAwardDto>> ApproveAsync(
        string sqid,
        AthletePensionApprovalInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _approvalValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var awardResult = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (awardResult.IsFailure)
        {
            return Result<AthletePensionAwardDto>.Failure(awardResult.ErrorCode!, awardResult.ErrorMessage!);
        }
        var award = awardResult.Value;
        if (award.Status != AthletePensionAwardStatus.Submitted)
        {
            return Result<AthletePensionAwardDto>.Failure(ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        var verdict = await EvaluateAsync(award, cancellationToken).ConfigureAwait(false);
        if (verdict.IsFailure)
        {
            return Result<AthletePensionAwardDto>.Failure(verdict.ErrorCode!, verdict.ErrorMessage!);
        }
        if (!verdict.Value.IsEligible)
        {
            return Result<AthletePensionAwardDto>.Failure(ErrorCodes.Conflict, NotEligibleMessage);
        }

        var verifiedRecords = await _db.AthleteCareerRecords
            .Where(r => r.AwardId == award.Id && r.IsActive && r.Verified)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var computeInput = new AthletePensionAmountInputDto(
            Role: award.Role.ToString(),
            VerifiedRecords: verifiedRecords
                .Select(r => new EligibilityRecordDto(
                    AchievementKind: r.AchievementKind.ToString(),
                    AchievementYear: r.AchievementYear,
                    Years: r.Years))
                .ToList(),
            RegulatoryBaseMdl: input.RegulatoryBaseMdl,
            AdditionalMultipliers: input.AdditionalMultipliers);
        var compute = _calculator.Compute(computeInput);
        if (compute.IsFailure)
        {
            return Result<AthletePensionAwardDto>.Failure(compute.ErrorCode!, compute.ErrorMessage!);
        }
        var computation = compute.Value;

        var now = _clock.UtcNow;
        award.Status = AthletePensionAwardStatus.Approved;
        award.ApprovedAt = now;
        award.MonthlyAmountMdl = computation.MonthlyAmountMdl;
        award.RegulatoryBaseMdl = computation.RegulatoryBaseMdl;
        award.MultiplierPercent = computation.FinalMultiplierPercent;
        award.EligibilityNotesJson = JsonSerializer.Serialize(verdict.Value, CachedJsonOptions);
        award.LastRecomputedAt = now;
        award.UpdatedAtUtc = now;
        award.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(AuditApproved, award, extra: new
            {
                note = input.Note,
                monthlyAmountMdl = award.MonthlyAmountMdl,
                multiplierPercent = award.MultiplierPercent,
            },
            cancellationToken).ConfigureAwait(false);

        CnasMeter.AthletePensionDecisionOutcome.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "approved"));

        return Result<AthletePensionAwardDto>.Success(await ToDtoAsync(award, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<AthletePensionAwardDto>> RejectAsync(
        string sqid,
        AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var result = await TransitionAsync(
            sqid,
            requiredCurrent: new[] { AthletePensionAwardStatus.Submitted },
            target: AthletePensionAwardStatus.Rejected,
            auditCode: AuditRejected,
            extras: (award) =>
            {
                award.RejectedAt = _clock.UtcNow;
                award.RejectionReason = input.Reason;
            },
            extraAuditPayload: new { reason = input.Reason },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            CnasMeter.AthletePensionDecisionOutcome.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "rejected"));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<Result<AthletePensionAwardDto>> ActivateAsync(
        string sqid,
        AthletePensionActivationInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _activationValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var result = await TransitionAsync(
            sqid,
            requiredCurrent: new[] { AthletePensionAwardStatus.Approved },
            target: AthletePensionAwardStatus.Active,
            auditCode: AuditActivated,
            extras: (award) => { award.EffectiveFrom = input.EffectiveFrom; },
            extraAuditPayload: new
            {
                effectiveFrom = input.EffectiveFrom,
                note = input.Note,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            CnasMeter.AthletePensionDecisionOutcome.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "activated"));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<Result<AthletePensionAwardDto>> SuspendAsync(
        string sqid,
        AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var result = await TransitionAsync(
            sqid,
            requiredCurrent: new[] { AthletePensionAwardStatus.Active },
            target: AthletePensionAwardStatus.Suspended,
            auditCode: AuditSuspended,
            extras: (award) =>
            {
                award.SuspendedAt = _clock.UtcNow;
                award.SuspensionReason = input.Reason;
            },
            extraAuditPayload: new { reason = input.Reason },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            CnasMeter.AthletePensionDecisionOutcome.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "suspended"));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<Result<AthletePensionAwardDto>> ResumeAsync(
        string sqid,
        AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var result = await TransitionAsync(
            sqid,
            requiredCurrent: new[] { AthletePensionAwardStatus.Suspended },
            target: AthletePensionAwardStatus.Active,
            auditCode: AuditResumed,
            extras: (award) =>
            {
                award.SuspendedAt = null;
                award.SuspensionReason = null;
            },
            extraAuditPayload: new { reason = input.Reason },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            CnasMeter.AthletePensionDecisionOutcome.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "resumed"));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<Result<AthletePensionAwardDto>> TerminateAsync(
        string sqid,
        AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var allowedNonTerminal = new[]
        {
            AthletePensionAwardStatus.Draft,
            AthletePensionAwardStatus.Submitted,
            AthletePensionAwardStatus.Approved,
            AthletePensionAwardStatus.Active,
            AthletePensionAwardStatus.Suspended,
        };
        var result = await TransitionAsync(
            sqid,
            requiredCurrent: allowedNonTerminal,
            target: AthletePensionAwardStatus.Terminated,
            auditCode: AuditTerminated,
            extras: (award) =>
            {
                award.TerminatedAt = _clock.UtcNow;
                award.TerminationReason = input.Reason;
            },
            extraAuditPayload: new { reason = input.Reason },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            CnasMeter.AthletePensionDecisionOutcome.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "terminated"));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<Result<AthletePensionAwardDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess
            ? Result<AthletePensionAwardDto>.Success(await ToDtoAsync(loaded.Value, cancellationToken).ConfigureAwait(false))
            : Result<AthletePensionAwardDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
    }

    /// <inheritdoc />
    public async Task<Result<AthletePensionAwardPageDto>> ListAsync(
        AthletePensionAwardFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<AthletePensionAwardPageDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<AthletePensionAward> q = _db.AthletePensionAwards
            .Where(r => r.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<AthletePensionAwardStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(r => r.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.Role)
            && Enum.TryParse<AthletePensionRole>(filter.Role, ignoreCase: false, out var role))
        {
            q = q.Where(r => r.Role == role);
        }
        if (!string.IsNullOrWhiteSpace(filter.SportDiscipline))
        {
            q = q.Where(r => r.SportDiscipline == filter.SportDiscipline);
        }
        if (!string.IsNullOrWhiteSpace(filter.BeneficiaryIdnpHash))
        {
            q = q.Where(r => r.BeneficiaryIdnpHash == filter.BeneficiaryIdnpHash);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = new List<AthletePensionAwardDto>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(await ToDtoAsync(row, cancellationToken).ConfigureAwait(false));
        }
        return Result<AthletePensionAwardPageDto>.Success(new AthletePensionAwardPageDto(
            Items: items,
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take));
    }

    /// <summary>
    /// Runs the eligibility evaluator using the award's verified career
    /// records, today's UTC date as evaluation date, and the award's
    /// snapshotted role + birth date.
    /// </summary>
    /// <param name="award">Loaded award entity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verdict result.</returns>
    private async Task<Result<AthletePensionEligibilityVerdictDto>> EvaluateAsync(
        AthletePensionAward award,
        CancellationToken cancellationToken)
    {
        var verifiedRecords = await _db.AthleteCareerRecords
            .Where(r => r.AwardId == award.Id && r.IsActive && r.Verified)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var evalInput = new AthletePensionEligibilityInputDto(
            Role: award.Role.ToString(),
            BirthDate: award.BeneficiaryBirthDate,
            EvaluationDate: DateOnly.FromDateTime(_clock.UtcNow),
            VerifiedRecords: verifiedRecords
                .Select(r => new EligibilityRecordDto(
                    AchievementKind: r.AchievementKind.ToString(),
                    AchievementYear: r.AchievementYear,
                    Years: r.Years))
                .ToList());
        return _evaluator.Evaluate(evalInput);
    }

    /// <summary>Loads a tracked award row by Sqid; returns NotFound when missing.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result carrying the entity when found.</returns>
    private async Task<Result<AthletePensionAward>> LoadAsync(string sqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<AthletePensionAward>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var entity = await _db.AthletePensionAwards
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return entity is null
            ? Result<AthletePensionAward>.Failure(ErrorCodes.NotFound, "Athlete-pension award not found.")
            : Result<AthletePensionAward>.Success(entity);
    }

    /// <summary>
    /// Shared lifecycle-transition helper used by Submit / Reject / Activate /
    /// Suspend / Resume / Terminate. Validates the award is in one of
    /// <paramref name="requiredCurrent"/>, flips the status, runs an optional
    /// <paramref name="extras"/> delegate, and emits the stable audit event.
    /// </summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="requiredCurrent">Statuses from which the transition is allowed.</param>
    /// <param name="target">Status to transition into.</param>
    /// <param name="auditCode">Stable audit event code to emit.</param>
    /// <param name="extras">Optional delegate run before persistence.</param>
    /// <param name="extraAuditPayload">Optional anonymous object merged into the audit details JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success; conflict / not-found otherwise.</returns>
    private async Task<Result<AthletePensionAwardDto>> TransitionAsync(
        string sqid,
        AthletePensionAwardStatus[] requiredCurrent,
        AthletePensionAwardStatus target,
        string auditCode,
        Action<AthletePensionAward>? extras,
        object? extraAuditPayload,
        CancellationToken cancellationToken)
    {
        var awardResult = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (awardResult.IsFailure)
        {
            return Result<AthletePensionAwardDto>.Failure(awardResult.ErrorCode!, awardResult.ErrorMessage!);
        }
        var award = awardResult.Value;
        if (!requiredCurrent.Contains(award.Status))
        {
            return Result<AthletePensionAwardDto>.Failure(ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        var now = _clock.UtcNow;
        award.Status = target;
        extras?.Invoke(award);
        award.UpdatedAtUtc = now;
        award.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(auditCode, award, extraAuditPayload, cancellationToken).ConfigureAwait(false);
        return Result<AthletePensionAwardDto>.Success(await ToDtoAsync(award, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Emits the stable audit row attached to the award.</summary>
    /// <param name="code">Stable audit code.</param>
    /// <param name="award">Loaded entity.</param>
    /// <param name="extra">Optional extra fields merged into the JSON payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task EmitAuditAsync(
        string code,
        AthletePensionAward award,
        object? extra,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            awardSqid = _sqids.Encode(award.Id),
            awardNumber = award.AwardNumber,
            status = award.Status.ToString(),
            role = award.Role.ToString(),
            beneficiaryIdnpHash = award.BeneficiaryIdnpHash,
            extra,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            code,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(AthletePensionAward),
            award.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an award entity to the wire DTO (with its career records).</summary>
    /// <param name="r">Loaded entity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Wire DTO.</returns>
    private async Task<AthletePensionAwardDto> ToDtoAsync(AthletePensionAward r, CancellationToken cancellationToken)
    {
        var records = await _db.AthleteCareerRecords
            .Where(c => c.AwardId == r.Id && c.IsActive)
            .OrderBy(c => c.AchievementYear)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new AthletePensionAwardDto(
            Id: _sqids.Encode(r.Id),
            AwardNumber: r.AwardNumber,
            BeneficiaryIdnpHash: r.BeneficiaryIdnpHash,
            BeneficiaryDisplayName: r.BeneficiaryDisplayName,
            BeneficiaryBirthDate: r.BeneficiaryBirthDate,
            BeneficiarySex: r.BeneficiarySex.ToString(),
            Role: r.Role.ToString(),
            SportDiscipline: r.SportDiscipline,
            Status: r.Status.ToString(),
            RequestedAt: r.RequestedAt,
            ApprovedAt: r.ApprovedAt,
            RejectedAt: r.RejectedAt,
            RejectionReason: r.RejectionReason,
            EffectiveFrom: r.EffectiveFrom,
            SuspendedAt: r.SuspendedAt,
            SuspensionReason: r.SuspensionReason,
            TerminatedAt: r.TerminatedAt,
            TerminationReason: r.TerminationReason,
            MonthlyAmountMdl: r.MonthlyAmountMdl,
            RegulatoryBaseMdl: r.RegulatoryBaseMdl,
            MultiplierPercent: r.MultiplierPercent,
            EligibilityNotesJson: r.EligibilityNotesJson,
            RegisteredAt: r.CreatedAtUtc,
            LastRecomputedAt: r.LastRecomputedAt,
            CareerRecords: records.Select(c => new AthleteCareerRecordDto(
                Id: _sqids.Encode(c.Id),
                AwardSqid: _sqids.Encode(c.AwardId),
                AchievementKind: c.AchievementKind.ToString(),
                AchievementYear: c.AchievementYear,
                Event: c.Event,
                Years: c.Years,
                Verified: c.Verified,
                VerifiedAt: c.VerifiedAt,
                VerificationNote: c.VerificationNote,
                EvidenceDocumentReference: c.EvidenceDocumentReference)).ToList());
    }
}
