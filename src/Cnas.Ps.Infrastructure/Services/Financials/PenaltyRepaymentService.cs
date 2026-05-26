using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Financials;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Financials;

/// <summary>
/// R0817 / TOR BP 1.2-H — concrete implementation of
/// <see cref="IPenaltyRepaymentService"/>. Owns the
/// <c>Active → Completed | Defaulted | Cancelled</c> lifecycle of a
/// <see cref="PenaltyRepaymentPlan"/> plus the per-installment payment-
/// registration path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Active-uniqueness defence.</b> <see cref="CreatePlanAsync"/> pre-checks
/// the filtered unique index (one Active plan per penalty) before insert so
/// the failure surfaces as a stable <c>ACTIVE_PLAN_EXISTS</c> message rather
/// than a raw <c>DbUpdateException</c>.
/// </para>
/// <para>
/// <b>Defaulted-detection contract.</b> <see cref="MarkDefaultedAsync"/>
/// flips an Active plan to <c>Defaulted</c> when any installment is past due
/// AND not paid for &gt; 30 days. The background job iterates every Active
/// plan and calls this method per row.
/// </para>
/// </remarks>
public sealed class PenaltyRepaymentService : IPenaltyRepaymentService
{
    /// <summary>Default-detection threshold (days past the installment due date).</summary>
    public const int DefaultDetectionWindowDays = 30;

    /// <summary>Stable audit event code emitted on plan creation.</summary>
    public const string AuditCreated = "PENALTY_PLAN.CREATED";

    /// <summary>Stable audit event code emitted on each registered installment payment.</summary>
    public const string AuditInstallmentPaid = "PENALTY_PLAN.INSTALLMENT_PAID";

    /// <summary>Stable audit event code emitted when the plan transitions to Completed.</summary>
    public const string AuditCompleted = "PENALTY_PLAN.COMPLETED";

    /// <summary>Stable audit event code emitted on administrative cancellation.</summary>
    public const string AuditCancelled = "PENALTY_PLAN.CANCELLED";

    /// <summary>Stable audit event code emitted when the background detector marks the plan defaulted.</summary>
    public const string AuditDefaulted = "PENALTY_PLAN.DEFAULTED";

    /// <summary>Stable failure message when the parent penalty is waived.</summary>
    public const string PenaltyWaivedMessage = "PENALTY_WAIVED";

    /// <summary>Stable failure message when an Active plan already exists for the penalty.</summary>
    public const string ActivePlanExistsMessage = "ACTIVE_PLAN_EXISTS";

    /// <summary>Stable failure message used when the lifecycle state forbids the transition.</summary>
    public const string InvalidStateMessage = "PENALTY_PLAN_INVALID_STATE";

    /// <summary>Stable failure message when the installment is already paid.</summary>
    public const string AlreadyPaidMessage = "INSTALLMENT_ALREADY_PAID";

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
    private readonly IValidator<PenaltyRepaymentCreatePlanInputDto> _createValidator;
    private readonly IValidator<PenaltyRepaymentRegisterPaymentInputDto> _payValidator;
    private readonly IValidator<PenaltyRepaymentCancelPlanInputDto> _cancelValidator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (write surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated-caller context for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="createValidator">Validator for the create-plan input shape.</param>
    /// <param name="payValidator">Validator for the register-payment input shape.</param>
    /// <param name="cancelValidator">Validator for the cancel-plan input shape.</param>
    public PenaltyRepaymentService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<PenaltyRepaymentCreatePlanInputDto> createValidator,
        IValidator<PenaltyRepaymentRegisterPaymentInputDto> payValidator,
        IValidator<PenaltyRepaymentCancelPlanInputDto> cancelValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(payValidator);
        ArgumentNullException.ThrowIfNull(cancelValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _createValidator = createValidator;
        _payValidator = payValidator;
        _cancelValidator = cancelValidator;
    }

    /// <inheritdoc />
    public async Task<Result<PenaltyRepaymentPlanDto>> CreatePlanAsync(
        PenaltyRepaymentCreatePlanInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _createValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<PenaltyRepaymentPlanDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(input.LatePaymentPenaltySqid);
        if (decoded.IsFailure)
        {
            return Result<PenaltyRepaymentPlanDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var penaltyId = decoded.Value;

        var penalty = await _db.LatePaymentPenalties
            .SingleOrDefaultAsync(p => p.Id == penaltyId && p.IsActive, ct).ConfigureAwait(false);
        if (penalty is null)
        {
            return Result<PenaltyRepaymentPlanDto>.Failure(
                ErrorCodes.NotFound, "LatePaymentPenalty not found.");
        }
        if (penalty.IsWaived)
        {
            return Result<PenaltyRepaymentPlanDto>.Failure(
                ErrorCodes.Conflict, PenaltyWaivedMessage);
        }

        var activeExists = await _db.PenaltyRepaymentPlans
            .AnyAsync(p => p.LatePaymentPenaltyId == penaltyId
                && p.Status == PenaltyRepaymentPlanStatus.Active
                && p.IsActive, ct).ConfigureAwait(false);
        if (activeExists)
        {
            return Result<PenaltyRepaymentPlanDto>.Failure(
                ErrorCodes.Conflict, ActivePlanExistsMessage);
        }

        // Compute the per-installment amount: floor-rounded to two decimals so
        // 1..(N-1) share the same nominal figure and the last row absorbs the
        // residual. e.g. 100 / 3 = 33.33 each plus the last installment getting
        // an extra 0.01 to total 100 exactly.
        var nominalAmount = decimal.Round(
            penalty.PenaltyAmount / input.InstallmentCount,
            decimals: 2,
            mode: MidpointRounding.ToZero);
        var residual = penalty.PenaltyAmount - (nominalAmount * (input.InstallmentCount - 1));

        var now = _clock.UtcNow;
        var plan = new PenaltyRepaymentPlan
        {
            LatePaymentPenaltyId = penaltyId,
            InstallmentCount = input.InstallmentCount,
            InstallmentAmount = nominalAmount,
            FirstInstallmentDueDate = input.FirstInstallmentDueDate,
            Status = PenaltyRepaymentPlanStatus.Active,
            PaidInstallmentCount = 0,
            RemainingAmount = penalty.PenaltyAmount,
            CreatedUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.PenaltyRepaymentPlans.Add(plan);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Generate installment rows in order; final installment absorbs the
        // rounding residual so the per-row sum reconciles exactly with the
        // penalty's PenaltyAmount snapshot.
        for (int n = 1; n <= input.InstallmentCount; n++)
        {
            var amount = n == input.InstallmentCount ? residual : nominalAmount;
            var dueDate = input.FirstInstallmentDueDate.AddMonths(n - 1);
            _db.PenaltyRepaymentInstallments.Add(new PenaltyRepaymentInstallment
            {
                PenaltyRepaymentPlanId = plan.Id,
                InstallmentNumber = n,
                DueDate = dueDate,
                Amount = amount,
                IsPaid = false,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            planSqid = _sqids.Encode(plan.Id),
            penaltySqid = _sqids.Encode(penaltyId),
            installmentCount = input.InstallmentCount,
            installmentAmount = nominalAmount,
            residual,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCreated,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(PenaltyRepaymentPlan),
            plan.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.PenaltyPlan.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "created"));

        return Result<PenaltyRepaymentPlanDto>.Success(ToDto(plan));
    }

    /// <inheritdoc />
    public async Task<Result<PenaltyRepaymentInstallmentDto>> RegisterInstallmentPaymentAsync(
        long installmentId,
        DateOnly paidDate,
        decimal paidAmount,
        CancellationToken ct = default)
    {
        var payInput = new PenaltyRepaymentRegisterPaymentInputDto(paidDate, paidAmount);
        var validation = await _payValidator.ValidateAsync(payInput, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<PenaltyRepaymentInstallmentDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var installment = await _db.PenaltyRepaymentInstallments
            .SingleOrDefaultAsync(i => i.Id == installmentId && i.IsActive, ct).ConfigureAwait(false);
        if (installment is null)
        {
            return Result<PenaltyRepaymentInstallmentDto>.Failure(
                ErrorCodes.NotFound, "PenaltyRepaymentInstallment not found.");
        }
        if (installment.IsPaid)
        {
            return Result<PenaltyRepaymentInstallmentDto>.Failure(
                ErrorCodes.Conflict, AlreadyPaidMessage);
        }

        var plan = await _db.PenaltyRepaymentPlans
            .SingleOrDefaultAsync(p => p.Id == installment.PenaltyRepaymentPlanId && p.IsActive, ct)
            .ConfigureAwait(false);
        if (plan is null)
        {
            return Result<PenaltyRepaymentInstallmentDto>.Failure(
                ErrorCodes.NotFound, "PenaltyRepaymentPlan not found.");
        }
        if (plan.Status != PenaltyRepaymentPlanStatus.Active)
        {
            return Result<PenaltyRepaymentInstallmentDto>.Failure(
                ErrorCodes.Conflict, InvalidStateMessage);
        }

        var now = _clock.UtcNow;
        installment.IsPaid = true;
        installment.PaidDate = paidDate;
        installment.PaidAmount = paidAmount;
        installment.UpdatedAtUtc = now;
        installment.UpdatedBy = _caller.UserSqid;

        plan.PaidInstallmentCount += 1;
        plan.RemainingAmount = Math.Max(0m, plan.RemainingAmount - installment.Amount);
        plan.UpdatedAtUtc = now;
        plan.UpdatedBy = _caller.UserSqid;

        bool wasCompleted = false;
        if (plan.PaidInstallmentCount >= plan.InstallmentCount)
        {
            plan.Status = PenaltyRepaymentPlanStatus.Completed;
            plan.CompletedUtc = now;
            wasCompleted = true;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            planSqid = _sqids.Encode(plan.Id),
            installmentSqid = _sqids.Encode(installment.Id),
            installmentNumber = installment.InstallmentNumber,
            paidDate = paidDate.ToString("O", CultureInfo.InvariantCulture),
            paidAmount,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditInstallmentPaid,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(PenaltyRepaymentInstallment),
            installment.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.PenaltyPlan.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "installment_paid"));

        if (wasCompleted)
        {
            var completedDetails = JsonSerializer.Serialize(new
            {
                planSqid = _sqids.Encode(plan.Id),
                completedUtc = now.ToString("O", CultureInfo.InvariantCulture),
            }, CachedJsonOptions);
            await _audit.RecordAsync(
                AuditCompleted,
                AuditSeverity.Critical,
                _caller.UserSqid ?? "?",
                nameof(PenaltyRepaymentPlan),
                plan.Id,
                completedDetails,
                _caller.SourceIp,
                _caller.CorrelationId,
                ct).ConfigureAwait(false);

            CnasMeter.PenaltyPlan.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "completed"));
        }

        return Result<PenaltyRepaymentInstallmentDto>.Success(ToDto(installment));
    }

    /// <inheritdoc />
    public async Task<Result<PenaltyRepaymentInstallmentDto>> RegisterInstallmentPaymentByNumberAsync(
        long planId,
        int installmentNumber,
        DateOnly paidDate,
        decimal paidAmount,
        CancellationToken ct = default)
    {
        var installmentId = await _db.PenaltyRepaymentInstallments
            .Where(i => i.IsActive
                && i.PenaltyRepaymentPlanId == planId
                && i.InstallmentNumber == installmentNumber)
            .Select(i => (long?)i.Id)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (!installmentId.HasValue)
        {
            return Result<PenaltyRepaymentInstallmentDto>.Failure(
                ErrorCodes.NotFound, "PenaltyRepaymentInstallment not found.");
        }
        return await RegisterInstallmentPaymentAsync(installmentId.Value, paidDate, paidAmount, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> CancelPlanAsync(long planId, string reason, CancellationToken ct = default)
    {
        var cancelInput = new PenaltyRepaymentCancelPlanInputDto(reason);
        var validation = await _cancelValidator.ValidateAsync(cancelInput, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var plan = await _db.PenaltyRepaymentPlans
            .SingleOrDefaultAsync(p => p.Id == planId && p.IsActive, ct).ConfigureAwait(false);
        if (plan is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "PenaltyRepaymentPlan not found.");
        }
        if (plan.Status != PenaltyRepaymentPlanStatus.Active)
        {
            return Result.Failure(ErrorCodes.Conflict, InvalidStateMessage);
        }

        var now = _clock.UtcNow;
        plan.Status = PenaltyRepaymentPlanStatus.Cancelled;
        plan.CancelReason = reason;
        plan.CancelledUtc = now;
        plan.UpdatedAtUtc = now;
        plan.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            planSqid = _sqids.Encode(plan.Id),
            cancelReason = reason,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCancelled,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(PenaltyRepaymentPlan),
            plan.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.PenaltyPlan.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "cancelled"));

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> MarkDefaultedAsync(long planId, CancellationToken ct = default)
    {
        var plan = await _db.PenaltyRepaymentPlans
            .SingleOrDefaultAsync(p => p.Id == planId && p.IsActive, ct).ConfigureAwait(false);
        if (plan is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "PenaltyRepaymentPlan not found.");
        }
        if (plan.Status != PenaltyRepaymentPlanStatus.Active)
        {
            return Result.Failure(ErrorCodes.Conflict, InvalidStateMessage);
        }

        var now = _clock.UtcNow;
        plan.Status = PenaltyRepaymentPlanStatus.Defaulted;
        plan.UpdatedAtUtc = now;
        plan.UpdatedBy = _caller.UserSqid ?? "system:penalty-repayment-default";
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            planSqid = _sqids.Encode(plan.Id),
            defaultedUtc = now.ToString("O", CultureInfo.InvariantCulture),
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditDefaulted,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "system:penalty-repayment-default",
            nameof(PenaltyRepaymentPlan),
            plan.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.PenaltyPlan.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "defaulted"));

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<PenaltyRepaymentPlanDto?> GetAsync(long planId, CancellationToken ct = default)
    {
        var plan = await _db.PenaltyRepaymentPlans
            .SingleOrDefaultAsync(p => p.Id == planId && p.IsActive, ct).ConfigureAwait(false);
        return plan is null ? null : ToDto(plan);
    }

    /// <summary>Projects a <see cref="PenaltyRepaymentPlan"/> entity into its outbound DTO.</summary>
    /// <param name="p">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private PenaltyRepaymentPlanDto ToDto(PenaltyRepaymentPlan p) => new(
        Id: _sqids.Encode(p.Id),
        LatePaymentPenaltySqid: _sqids.Encode(p.LatePaymentPenaltyId),
        InstallmentCount: p.InstallmentCount,
        InstallmentAmount: p.InstallmentAmount,
        FirstInstallmentDueDate: p.FirstInstallmentDueDate,
        Status: p.Status.ToString(),
        PaidInstallmentCount: p.PaidInstallmentCount,
        RemainingAmount: p.RemainingAmount,
        CreatedUtc: p.CreatedUtc,
        CompletedUtc: p.CompletedUtc,
        CancelledUtc: p.CancelledUtc,
        CancelReason: p.CancelReason);

    /// <summary>Projects a <see cref="PenaltyRepaymentInstallment"/> entity into its outbound DTO.</summary>
    /// <param name="i">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private PenaltyRepaymentInstallmentDto ToDto(PenaltyRepaymentInstallment i) => new(
        Id: _sqids.Encode(i.Id),
        PenaltyRepaymentPlanSqid: _sqids.Encode(i.PenaltyRepaymentPlanId),
        InstallmentNumber: i.InstallmentNumber,
        DueDate: i.DueDate,
        Amount: i.Amount,
        PaidDate: i.PaidDate,
        PaidAmount: i.PaidAmount,
        IsPaid: i.IsPaid);
}
