using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.CapitalisedPayments;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.CapitalisedPayments;

/// <summary>
/// R1202 / TOR §3.4-C — production implementation of
/// <see cref="ICapitalisedPaymentService"/>. Owns the request lifecycle
/// (create / modify / submit / compute / approve / reject / mark-settled /
/// cancel) plus the lookup / listing surface. Delegates the present-value
/// computation to the injected <see cref="IPresentValueAnnuityCalculator"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit + metric.</b> Every lifecycle transition emits the stable audit
/// code at <see cref="AuditSeverity.Critical"/> severity per CLAUDE.md §5.6
/// (PII / financial data). Metrics fire alongside the audit row so operators
/// can chart per-obligation-kind volume and per-outcome distribution.
/// </para>
/// <para>
/// <b>PII safety.</b> Audit payloads NEVER contain the plaintext IDNP or
/// IDNO — only the Sqid id of the request and the deterministic-hash
/// fingerprints. The breakdown JSON carries per-period factor rows
/// (period index, survival probability, discount factor, contribution) and
/// NEVER embeds beneficiary identifiers.
/// </para>
/// </remarks>
public sealed class CapitalisedPaymentService : ICapitalisedPaymentService
{
    /// <summary>Stable audit event code emitted when a request is created.</summary>
    public const string AuditCreated = "CAP_PAY.CREATED";

    /// <summary>Stable audit event code emitted when a request is modified.</summary>
    public const string AuditModified = "CAP_PAY.MODIFIED";

    /// <summary>Stable audit event code emitted when a request is submitted.</summary>
    public const string AuditSubmitted = "CAP_PAY.SUBMITTED";

    /// <summary>Stable audit event code emitted when a request is computed.</summary>
    public const string AuditComputed = "CAP_PAY.COMPUTED";

    /// <summary>Stable audit event code emitted when a request is approved.</summary>
    public const string AuditApproved = "CAP_PAY.APPROVED";

    /// <summary>Stable audit event code emitted when a request is rejected.</summary>
    public const string AuditRejected = "CAP_PAY.REJECTED";

    /// <summary>Stable audit event code emitted when a request is marked settled.</summary>
    public const string AuditSettled = "CAP_PAY.SETTLED";

    /// <summary>Stable audit event code emitted when a request is cancelled.</summary>
    public const string AuditCancelled = "CAP_PAY.CANCELLED";

    /// <summary>Stable conflict message for invalid state transitions.</summary>
    public const string InvalidTransitionMessage = "CAP_PAY.INVALID_TRANSITION";

    /// <summary>Stable conflict message when the request-number generator exceeds its retry budget.</summary>
    public const string RequestNumberGenerationFailedMessage = "CAP_PAY.REQUEST_NUMBER_GENERATION_FAILED";

    /// <summary>Maximum re-attempts for the request-number generator under concurrent contention.</summary>
    private const int MaxRequestNumberRetries = 5;

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
    private readonly IPresentValueAnnuityCalculator _calculator;
    private readonly IValidator<CapitalisedPaymentRequestCreateInputDto> _createValidator;
    private readonly IValidator<CapitalisedPaymentRequestModifyInputDto> _modifyValidator;
    private readonly IValidator<CapitalisedPaymentReasonInputDto> _reasonValidator;
    private readonly IValidator<CapitalisedPaymentApprovalInputDto> _approvalValidator;
    private readonly IValidator<CapitalisedPaymentSettlementInputDto> _settlementValidator;
    private readonly IValidator<CapitalisedPaymentRequestFilterDto> _filterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">EF writer context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated-caller context.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="hasher">Deterministic hasher used to maintain the IDNP / IDNO shadow hash columns.</param>
    /// <param name="calculator">Present-value annuity calculator.</param>
    /// <param name="createValidator">Validator for create input.</param>
    /// <param name="modifyValidator">Validator for modify input.</param>
    /// <param name="reasonValidator">Validator for reject / cancel input.</param>
    /// <param name="approvalValidator">Validator for approval input.</param>
    /// <param name="settlementValidator">Validator for settlement input.</param>
    /// <param name="filterValidator">Validator for filter input.</param>
    public CapitalisedPaymentService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IDeterministicHasher hasher,
        IPresentValueAnnuityCalculator calculator,
        IValidator<CapitalisedPaymentRequestCreateInputDto> createValidator,
        IValidator<CapitalisedPaymentRequestModifyInputDto> modifyValidator,
        IValidator<CapitalisedPaymentReasonInputDto> reasonValidator,
        IValidator<CapitalisedPaymentApprovalInputDto> approvalValidator,
        IValidator<CapitalisedPaymentSettlementInputDto> settlementValidator,
        IValidator<CapitalisedPaymentRequestFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(calculator);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(approvalValidator);
        ArgumentNullException.ThrowIfNull(settlementValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _hasher = hasher;
        _calculator = calculator;
        _createValidator = createValidator;
        _modifyValidator = modifyValidator;
        _reasonValidator = reasonValidator;
        _approvalValidator = approvalValidator;
        _settlementValidator = settlementValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<CapitalisedPaymentRequestDto>> CreateAsync(
        CapitalisedPaymentRequestCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<CapitalisedPaymentRequestDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        if (!Enum.TryParse<CapitalisedPaymentObligationKind>(input.ObligationKind, ignoreCase: false, out var kind))
        {
            return Result<CapitalisedPaymentRequestDto>.Failure(
                ErrorCodes.ValidationFailed, "ObligationKind must be a known CapitalisedPaymentObligationKind enum name.");
        }
        if (!Enum.TryParse<BeneficiarySex>(input.BeneficiarySex, ignoreCase: false, out var sex))
        {
            return Result<CapitalisedPaymentRequestDto>.Failure(
                ErrorCodes.ValidationFailed, "BeneficiarySex must be a known BeneficiarySex enum name.");
        }

        var canonicalIdnp = input.BeneficiaryIdnp.Trim().ToUpperInvariant();
        var canonicalIdno = input.LiquidatedDebtorIdno.Trim().ToUpperInvariant();
        var idnpHash = _hasher.ComputeHash(canonicalIdnp);
        var idnoHash = _hasher.ComputeHash(canonicalIdno);
        var now = _clock.UtcNow;

        // Auto-generate CPR-{year}-{seq:000000}. Retry on contention.
        CapitalisedPaymentRequest? created = null;
        DbUpdateException? lastFailure = null;
        for (var attempt = 0; attempt < MaxRequestNumberRetries; attempt++)
        {
            var year = input.ValuationDate.Year;
            var prefix = $"CPR-{year}-";
            var existingCount = await _db.CapitalisedPaymentRequests
                .CountAsync(r => r.RequestNumber.StartsWith(prefix), cancellationToken)
                .ConfigureAwait(false);
            var requestNumber = $"{prefix}{(existingCount + 1 + attempt):D6}";

            var entity = new CapitalisedPaymentRequest
            {
                RequestNumber = requestNumber,
                BeneficiaryIdnp = canonicalIdnp,
                BeneficiaryIdnpHash = idnpHash,
                BeneficiaryBirthDate = input.BeneficiaryBirthDate,
                BeneficiarySex = sex,
                LiquidatedDebtorIdno = canonicalIdno,
                LiquidatedDebtorIdnoHash = idnoHash,
                LiquidatedDebtorName = input.LiquidatedDebtorName,
                Status = CapitalisedPaymentRequestStatus.Draft,
                ObligationKind = kind,
                MonthlyAmountMdl = input.MonthlyAmountMdl,
                ObligationStartDate = input.ObligationStartDate,
                ObligationEndDate = input.ObligationEndDate,
                ValuationDate = input.ValuationDate,
                LegalDiscountRatePercent = input.LegalDiscountRatePercent,
                RegisteredByUserId = (int)(_caller.UserId ?? 0),
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.CapitalisedPaymentRequests.Add(entity);

            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                created = entity;
                break;
            }
            catch (DbUpdateException ex)
            {
                lastFailure = ex;
                _db.CapitalisedPaymentRequests.Remove(entity);
            }
        }
        if (created is null)
        {
            return Result<CapitalisedPaymentRequestDto>.Failure(
                ErrorCodes.Conflict,
                lastFailure?.Message ?? RequestNumberGenerationFailedMessage);
        }

        await EmitAuditAsync(AuditCreated, created, extra: new
            {
                obligationKind = created.ObligationKind.ToString(),
                beneficiarySex = created.BeneficiarySex.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        CnasMeter.CapitalisedPaymentRequested.Add(1);

        return Result<CapitalisedPaymentRequestDto>.Success(ToDto(created));
    }

    /// <inheritdoc />
    public async Task<Result<CapitalisedPaymentRequestDto>> ModifyAsync(
        string sqid,
        CapitalisedPaymentRequestModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _modifyValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<CapitalisedPaymentRequestDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var requestResult = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (requestResult.IsFailure)
        {
            return Result<CapitalisedPaymentRequestDto>.Failure(requestResult.ErrorCode!, requestResult.ErrorMessage!);
        }
        var request = requestResult.Value;

        if (request.Status != CapitalisedPaymentRequestStatus.Draft)
        {
            return Result<CapitalisedPaymentRequestDto>.Failure(ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        if (input.BeneficiaryBirthDate.HasValue)
        {
            request.BeneficiaryBirthDate = input.BeneficiaryBirthDate.Value;
        }
        if (input.BeneficiarySex is not null)
        {
            if (!Enum.TryParse<BeneficiarySex>(input.BeneficiarySex, ignoreCase: false, out var newSex))
            {
                return Result<CapitalisedPaymentRequestDto>.Failure(
                    ErrorCodes.ValidationFailed, "BeneficiarySex must be a known BeneficiarySex enum name.");
            }
            request.BeneficiarySex = newSex;
        }
        if (input.LiquidatedDebtorName is not null)
        {
            request.LiquidatedDebtorName = input.LiquidatedDebtorName;
        }
        if (input.ObligationKind is not null)
        {
            if (!Enum.TryParse<CapitalisedPaymentObligationKind>(input.ObligationKind, ignoreCase: false, out var newKind))
            {
                return Result<CapitalisedPaymentRequestDto>.Failure(
                    ErrorCodes.ValidationFailed, "ObligationKind must be a known CapitalisedPaymentObligationKind enum name.");
            }
            request.ObligationKind = newKind;
        }
        if (input.MonthlyAmountMdl.HasValue)
        {
            request.MonthlyAmountMdl = input.MonthlyAmountMdl.Value;
        }
        if (input.ObligationStartDate.HasValue)
        {
            request.ObligationStartDate = input.ObligationStartDate.Value;
        }
        if (input.ObligationEndDate.HasValue)
        {
            request.ObligationEndDate = input.ObligationEndDate.Value;
        }
        if (input.ValuationDate.HasValue)
        {
            request.ValuationDate = input.ValuationDate.Value;
        }
        if (input.LegalDiscountRatePercent.HasValue)
        {
            request.LegalDiscountRatePercent = input.LegalDiscountRatePercent.Value;
        }

        var now = _clock.UtcNow;
        request.UpdatedAtUtc = now;
        request.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(AuditModified, request, extra: new
            {
                changeReason = input.ChangeReason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<CapitalisedPaymentRequestDto>.Success(ToDto(request));
    }

    /// <inheritdoc />
    public Task<Result<CapitalisedPaymentRequestDto>> SubmitAsync(
        string sqid,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            sqid,
            requiredCurrent: new[] { CapitalisedPaymentRequestStatus.Draft },
            target: CapitalisedPaymentRequestStatus.Submitted,
            auditCode: AuditSubmitted,
            extras: null,
            extraAuditPayload: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<CapitalisedPaymentDecisionDto>> ComputeAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var requestResult = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (requestResult.IsFailure)
        {
            return Result<CapitalisedPaymentDecisionDto>.Failure(requestResult.ErrorCode!, requestResult.ErrorMessage!);
        }
        var request = requestResult.Value;
        if (request.Status != CapitalisedPaymentRequestStatus.Submitted)
        {
            return Result<CapitalisedPaymentDecisionDto>.Failure(ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        var ageMonths = MonthsBetween(request.BeneficiaryBirthDate, request.ValuationDate);
        var ageYears = decimal.Round(ageMonths / 12m, 2, MidpointRounding.ToEven);

        var now = _clock.UtcNow;
        request.Status = CapitalisedPaymentRequestStatus.Computing;
        request.UpdatedAtUtc = now;
        request.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var calcInput = new CapitalisedAnnuityInputDto(
            BeneficiarySex: request.BeneficiarySex.ToString(),
            AgeAtValuationYears: ageYears,
            MonthlyAmountMdl: request.MonthlyAmountMdl,
            ValuationDate: request.ValuationDate,
            ObligationEndDate: request.ObligationEndDate,
            AnnualDiscountRatePercent: request.LegalDiscountRatePercent);
        var compute = _calculator.Compute(calcInput);
        if (compute.IsFailure)
        {
            // Revert the in-flight Computing flag so the operator can retry
            // after fixing the input (e.g. via Modify on a re-opened Draft).
            request.Status = CapitalisedPaymentRequestStatus.Submitted;
            request.UpdatedAtUtc = _clock.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<CapitalisedPaymentDecisionDto>.Failure(compute.ErrorCode!, compute.ErrorMessage!);
        }
        var computation = compute.Value;

        var decision = new CapitalisedPaymentDecision
        {
            RequestId = request.Id,
            DecisionStatus = CapitalisedPaymentDecisionStatus.Approved, // placeholder until Approve/Reject — see remarks
            ComputedAtUtc = _clock.UtcNow,
            EffectiveAgeYears = computation.EffectiveAgeYears,
            LifeExpectancyMonths = computation.LifeExpectancyMonths,
            EffectiveDiscountMonthly = computation.EffectiveDiscountMonthly,
            CapitalisedAmountMdl = computation.CapitalisedAmountMdl,
            ComputationBreakdownJson = computation.ComputationBreakdownJson,
            CreatedAtUtc = _clock.UtcNow,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        // The Approved sentinel above is a model-level default; the actual
        // approval / rejection transition lives on Approve/Reject. Persisting
        // the row at ComputedAwaitingApproval status with DecisionStatus
        // pre-defaulted simplifies the subsequent update — at compute time the
        // request lifecycle is the authoritative state-machine, not the
        // per-decision status. The Approve / Reject paths flip the decision
        // row alongside the request status.
        _db.CapitalisedPaymentDecisions.Add(decision);

        request.Status = CapitalisedPaymentRequestStatus.ComputedAwaitingApproval;
        request.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(AuditComputed, request, extra: new
            {
                decisionSqid = _sqids.Encode(decision.Id),
                capitalisedAmountMdl = decision.CapitalisedAmountMdl,
                lifeExpectancyMonths = decision.LifeExpectancyMonths,
                effectiveDiscountMonthly = decision.EffectiveDiscountMonthly,
                effectiveAgeYears = decision.EffectiveAgeYears,
            },
            cancellationToken).ConfigureAwait(false);

        CnasMeter.CapitalisedPaymentComputed.Add(
            1,
            new KeyValuePair<string, object?>("obligation_kind", request.ObligationKind.ToString()));

        return Result<CapitalisedPaymentDecisionDto>.Success(ToDto(decision));
    }

    /// <inheritdoc />
    public async Task<Result<CapitalisedPaymentDecisionDto>> ApproveAsync(
        string sqid,
        CapitalisedPaymentApprovalInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _approvalValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<CapitalisedPaymentDecisionDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var requestResult = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (requestResult.IsFailure)
        {
            return Result<CapitalisedPaymentDecisionDto>.Failure(requestResult.ErrorCode!, requestResult.ErrorMessage!);
        }
        var request = requestResult.Value;
        if (request.Status != CapitalisedPaymentRequestStatus.ComputedAwaitingApproval)
        {
            return Result<CapitalisedPaymentDecisionDto>.Failure(ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        var decision = await FetchLatestDecisionAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (decision is null)
        {
            return Result<CapitalisedPaymentDecisionDto>.Failure(ErrorCodes.NotFound, "No decision row found for request.");
        }

        var now = _clock.UtcNow;
        decision.DecisionStatus = CapitalisedPaymentDecisionStatus.Approved;
        decision.ApprovedByUserId = (int)(_caller.UserId ?? 0);
        decision.UpdatedAtUtc = now;
        decision.UpdatedBy = _caller.UserSqid;
        request.Status = CapitalisedPaymentRequestStatus.Approved;
        request.UpdatedAtUtc = now;
        request.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(AuditApproved, request, extra: new
            {
                decisionSqid = _sqids.Encode(decision.Id),
                note = input.Note,
            },
            cancellationToken).ConfigureAwait(false);

        CnasMeter.CapitalisedPaymentDecisionOutcome.Add(
            1,
            new KeyValuePair<string, object?>("obligation_kind", request.ObligationKind.ToString()),
            new KeyValuePair<string, object?>("outcome", "approved"));

        return Result<CapitalisedPaymentDecisionDto>.Success(ToDto(decision));
    }

    /// <inheritdoc />
    public async Task<Result<CapitalisedPaymentDecisionDto>> RejectAsync(
        string sqid,
        CapitalisedPaymentReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<CapitalisedPaymentDecisionDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var requestResult = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (requestResult.IsFailure)
        {
            return Result<CapitalisedPaymentDecisionDto>.Failure(requestResult.ErrorCode!, requestResult.ErrorMessage!);
        }
        var request = requestResult.Value;
        if (request.Status != CapitalisedPaymentRequestStatus.ComputedAwaitingApproval)
        {
            return Result<CapitalisedPaymentDecisionDto>.Failure(ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        var decision = await FetchLatestDecisionAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (decision is null)
        {
            return Result<CapitalisedPaymentDecisionDto>.Failure(ErrorCodes.NotFound, "No decision row found for request.");
        }

        var now = _clock.UtcNow;
        decision.DecisionStatus = CapitalisedPaymentDecisionStatus.Rejected;
        decision.RejectionReason = input.Reason;
        decision.UpdatedAtUtc = now;
        decision.UpdatedBy = _caller.UserSqid;
        request.Status = CapitalisedPaymentRequestStatus.Rejected;
        request.UpdatedAtUtc = now;
        request.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(AuditRejected, request, extra: new
            {
                decisionSqid = _sqids.Encode(decision.Id),
                reason = input.Reason,
            },
            cancellationToken).ConfigureAwait(false);

        CnasMeter.CapitalisedPaymentDecisionOutcome.Add(
            1,
            new KeyValuePair<string, object?>("obligation_kind", request.ObligationKind.ToString()),
            new KeyValuePair<string, object?>("outcome", "rejected"));

        return Result<CapitalisedPaymentDecisionDto>.Success(ToDto(decision));
    }

    /// <inheritdoc />
    public async Task<Result<CapitalisedPaymentRequestDto>> MarkSettledAsync(
        string sqid,
        CapitalisedPaymentSettlementInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _settlementValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<CapitalisedPaymentRequestDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var result = await TransitionAsync(
            sqid,
            requiredCurrent: new[] { CapitalisedPaymentRequestStatus.Approved },
            target: CapitalisedPaymentRequestStatus.Settled,
            auditCode: AuditSettled,
            extras: null,
            extraAuditPayload: new
            {
                treasuryReceiptSqid = input.TreasuryReceiptSqid,
                settlementNote = input.SettlementNote,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            CnasMeter.CapitalisedPaymentDecisionOutcome.Add(
                1,
                new KeyValuePair<string, object?>("obligation_kind", Enum.Parse<CapitalisedPaymentObligationKind>(result.Value.ObligationKind).ToString()),
                new KeyValuePair<string, object?>("outcome", "settled"));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<Result<CapitalisedPaymentRequestDto>> CancelAsync(
        string sqid,
        CapitalisedPaymentReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<CapitalisedPaymentRequestDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var allowedNonTerminal = new[]
        {
            CapitalisedPaymentRequestStatus.Draft,
            CapitalisedPaymentRequestStatus.Submitted,
            CapitalisedPaymentRequestStatus.Computing,
            CapitalisedPaymentRequestStatus.ComputedAwaitingApproval,
            CapitalisedPaymentRequestStatus.Approved,
        };
        var result = await TransitionAsync(
            sqid,
            requiredCurrent: allowedNonTerminal,
            target: CapitalisedPaymentRequestStatus.Cancelled,
            auditCode: AuditCancelled,
            extras: (req) => { req.CancellationReason = input.Reason; },
            extraAuditPayload: new { reason = input.Reason },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            CnasMeter.CapitalisedPaymentDecisionOutcome.Add(
                1,
                new KeyValuePair<string, object?>("obligation_kind", Enum.Parse<CapitalisedPaymentObligationKind>(result.Value.ObligationKind).ToString()),
                new KeyValuePair<string, object?>("outcome", "cancelled"));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<Result<CapitalisedPaymentRequestDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess
            ? Result<CapitalisedPaymentRequestDto>.Success(ToDto(loaded.Value))
            : Result<CapitalisedPaymentRequestDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
    }

    /// <inheritdoc />
    public async Task<Result<CapitalisedPaymentRequestPageDto>> ListAsync(
        CapitalisedPaymentRequestFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<CapitalisedPaymentRequestPageDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<CapitalisedPaymentRequest> q = _db.CapitalisedPaymentRequests
            .Where(r => r.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<CapitalisedPaymentRequestStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(r => r.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.ObligationKind)
            && Enum.TryParse<CapitalisedPaymentObligationKind>(filter.ObligationKind, ignoreCase: false, out var kind))
        {
            q = q.Where(r => r.ObligationKind == kind);
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

        return Result<CapitalisedPaymentRequestPageDto>.Success(new CapitalisedPaymentRequestPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take));
    }

    /// <inheritdoc />
    public async Task<Result<CapitalisedPaymentDecisionDto>> GetLatestDecisionAsync(
        string requestSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(requestSqid);
        if (decoded.IsFailure)
        {
            return Result<CapitalisedPaymentDecisionDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var decision = await FetchLatestDecisionAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return decision is null
            ? Result<CapitalisedPaymentDecisionDto>.Failure(ErrorCodes.NotFound, "Decision not found.")
            : Result<CapitalisedPaymentDecisionDto>.Success(ToDto(decision));
    }

    /// <summary>Loads a tracked request row by Sqid; returns NotFound when missing.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result carrying the entity when found.</returns>
    private async Task<Result<CapitalisedPaymentRequest>> LoadAsync(string sqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<CapitalisedPaymentRequest>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var entity = await _db.CapitalisedPaymentRequests
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return entity is null
            ? Result<CapitalisedPaymentRequest>.Failure(ErrorCodes.NotFound, "Capitalised-payment request not found.")
            : Result<CapitalisedPaymentRequest>.Success(entity);
    }

    /// <summary>Fetches the most recent decision row for a request id (or null when none exists).</summary>
    /// <param name="requestId">Internal request primary key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The most recent decision entity, or null.</returns>
    private Task<CapitalisedPaymentDecision?> FetchLatestDecisionAsync(long requestId, CancellationToken cancellationToken) =>
        _db.CapitalisedPaymentDecisions
            .Where(d => d.RequestId == requestId && d.IsActive)
            .OrderByDescending(d => d.ComputedAtUtc)
            .ThenByDescending(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Shared lifecycle-transition helper used by Submit / MarkSettled /
    /// Cancel. Validates the request is in one of <paramref name="requiredCurrent"/>,
    /// flips the status, runs an optional <paramref name="extras"/> delegate,
    /// and emits the stable audit event.
    /// </summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="requiredCurrent">Statuses from which the transition is allowed.</param>
    /// <param name="target">Status to transition into.</param>
    /// <param name="auditCode">Stable audit event code to emit.</param>
    /// <param name="extras">Optional delegate run before persistence (e.g. set CancellationReason).</param>
    /// <param name="extraAuditPayload">Optional anonymous object merged into the audit details JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success; conflict / not-found otherwise.</returns>
    private async Task<Result<CapitalisedPaymentRequestDto>> TransitionAsync(
        string sqid,
        CapitalisedPaymentRequestStatus[] requiredCurrent,
        CapitalisedPaymentRequestStatus target,
        string auditCode,
        Action<CapitalisedPaymentRequest>? extras,
        object? extraAuditPayload,
        CancellationToken cancellationToken)
    {
        var requestResult = await LoadAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (requestResult.IsFailure)
        {
            return Result<CapitalisedPaymentRequestDto>.Failure(requestResult.ErrorCode!, requestResult.ErrorMessage!);
        }
        var request = requestResult.Value;
        if (!requiredCurrent.Contains(request.Status))
        {
            return Result<CapitalisedPaymentRequestDto>.Failure(ErrorCodes.Conflict, InvalidTransitionMessage);
        }

        var now = _clock.UtcNow;
        request.Status = target;
        extras?.Invoke(request);
        request.UpdatedAtUtc = now;
        request.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(auditCode, request, extraAuditPayload, cancellationToken).ConfigureAwait(false);
        return Result<CapitalisedPaymentRequestDto>.Success(ToDto(request));
    }

    /// <summary>Emits the stable audit row attached to the request.</summary>
    /// <param name="code">Stable audit code.</param>
    /// <param name="request">Loaded entity.</param>
    /// <param name="extra">Optional extra fields merged into the JSON payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task EmitAuditAsync(
        string code,
        CapitalisedPaymentRequest request,
        object? extra,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            requestSqid = _sqids.Encode(request.Id),
            requestNumber = request.RequestNumber,
            status = request.Status.ToString(),
            obligationKind = request.ObligationKind.ToString(),
            beneficiaryIdnpHash = request.BeneficiaryIdnpHash,
            liquidatedDebtorIdnoHash = request.LiquidatedDebtorIdnoHash,
            extra,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            code,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(CapitalisedPaymentRequest),
            request.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the count of whole months from <paramref name="start"/> to
    /// <paramref name="end"/>; clamped at zero when the order is reversed.
    /// </summary>
    /// <param name="start">Earlier calendar date (e.g. birth date).</param>
    /// <param name="end">Later calendar date (e.g. valuation date).</param>
    /// <returns>Difference in whole months (≥ 0).</returns>
    internal static int MonthsBetween(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            return 0;
        }
        var months = ((end.Year - start.Year) * 12) + (end.Month - start.Month);
        if (end.Day < start.Day)
        {
            months -= 1;
        }
        return Math.Max(0, months);
    }

    /// <summary>Projects a request entity to the wire DTO.</summary>
    /// <param name="r">Loaded entity.</param>
    /// <returns>Wire DTO.</returns>
    private CapitalisedPaymentRequestDto ToDto(CapitalisedPaymentRequest r) => new(
        Id: _sqids.Encode(r.Id),
        RequestNumber: r.RequestNumber,
        BeneficiaryIdnpHash: r.BeneficiaryIdnpHash,
        BeneficiaryBirthDate: r.BeneficiaryBirthDate,
        BeneficiarySex: r.BeneficiarySex.ToString(),
        LiquidatedDebtorIdnoHash: r.LiquidatedDebtorIdnoHash,
        LiquidatedDebtorName: r.LiquidatedDebtorName,
        Status: r.Status.ToString(),
        ObligationKind: r.ObligationKind.ToString(),
        MonthlyAmountMdl: r.MonthlyAmountMdl,
        ObligationStartDate: r.ObligationStartDate,
        ObligationEndDate: r.ObligationEndDate,
        ValuationDate: r.ValuationDate,
        LegalDiscountRatePercent: r.LegalDiscountRatePercent,
        RegisteredAt: r.CreatedAtUtc,
        CancellationReason: r.CancellationReason);

    /// <summary>Projects a decision entity to the wire DTO.</summary>
    /// <param name="d">Loaded entity.</param>
    /// <returns>Wire DTO.</returns>
    private CapitalisedPaymentDecisionDto ToDto(CapitalisedPaymentDecision d) => new(
        Id: _sqids.Encode(d.Id),
        RequestSqid: _sqids.Encode(d.RequestId),
        DecisionStatus: d.DecisionStatus.ToString(),
        ComputedAtUtc: d.ComputedAtUtc,
        EffectiveAgeYears: d.EffectiveAgeYears,
        LifeExpectancyMonths: d.LifeExpectancyMonths,
        EffectiveDiscountMonthly: d.EffectiveDiscountMonthly,
        CapitalisedAmountMdl: d.CapitalisedAmountMdl,
        ComputationBreakdownJson: d.ComputationBreakdownJson,
        ApprovedByUserSqid: d.ApprovedByUserId.HasValue ? _sqids.Encode(d.ApprovedByUserId.Value) : null,
        RejectionReason: d.RejectionReason);
}
