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
/// R0815 / TOR BP 1.2-F — concrete implementation of
/// <see cref="IPaymentCorrectionService"/>. Owns the
/// <c>Draft → Approved → Applied</c> lifecycle plus the cancellation path
/// and performs the actual receipt mutation on <see cref="ApplyAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mutation dispatch.</b> <see cref="ApplyAsync"/> dispatches on
/// <see cref="PaymentCorrection.Kind"/>:
/// <list type="bullet">
///   <item><c>Reverse</c> — receipt.DistributionStatus ← Failed; receipt.UndistributedRemainderAmount ← AmountReceived.</item>
///   <item><c>RedirectToPayer</c> — receipt.PayerContributorId ← RedirectedToContributorId.</item>
///   <item><c>RedirectToMonth</c> — receipt.ReportingMonth ← RedirectedToMonth.</item>
///   <item><c>AdjustAmount</c> — receipt.AmountReceived ← AdjustedAmount.</item>
/// </list>
/// </para>
/// <para>
/// <b>Adjusted-amount cap.</b> <see cref="CreateAsync"/> verifies that
/// <c>AdjustedAmount</c> &lt;= original receipt's <c>AmountReceived</c> when
/// <c>Kind=AdjustAmount</c>. Over-increasing a received amount is not a
/// "correction" — it would represent unauthorised free money.
/// </para>
/// </remarks>
public sealed class PaymentCorrectionService : IPaymentCorrectionService
{
    /// <summary>Stable audit event code emitted on correction creation.</summary>
    public const string AuditCreated = "PAYMENT_CORRECTION.CREATED";

    /// <summary>Stable audit event code emitted on correction approval.</summary>
    public const string AuditApproved = "PAYMENT_CORRECTION.APPROVED";

    /// <summary>Stable audit event code emitted when the correction is applied to the receipt.</summary>
    public const string AuditApplied = "PAYMENT_CORRECTION.APPLIED";

    /// <summary>Stable audit event code emitted on correction cancellation.</summary>
    public const string AuditCancelled = "PAYMENT_CORRECTION.CANCELLED";

    /// <summary>Stable failure message used when the lifecycle state forbids the transition.</summary>
    public const string InvalidStateMessage = "PAYMENT_CORRECTION_INVALID_STATE";

    /// <summary>Stable failure message used when the adjusted amount exceeds the original receipt amount.</summary>
    public const string AdjustedAmountExceedsOriginalMessage = "ADJUSTED_AMOUNT_EXCEEDS_ORIGINAL";

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
    private readonly IValidator<PaymentCorrectionCreateInputDto> _createValidator;
    private readonly IValidator<PaymentCorrectionCancelInputDto> _cancelValidator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (write surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated-caller context for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="createValidator">Validator for the create input shape.</param>
    /// <param name="cancelValidator">Validator for the cancel input shape.</param>
    public PaymentCorrectionService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<PaymentCorrectionCreateInputDto> createValidator,
        IValidator<PaymentCorrectionCancelInputDto> cancelValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(cancelValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _createValidator = createValidator;
        _cancelValidator = cancelValidator;
    }

    /// <inheritdoc />
    public async Task<Result<PaymentCorrectionDto>> CreateAsync(
        PaymentCorrectionCreateInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _createValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<PaymentCorrectionDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decodedReceipt = _sqids.TryDecode(input.OriginalReceiptSqid);
        if (decodedReceipt.IsFailure)
        {
            return Result<PaymentCorrectionDto>.Failure(decodedReceipt.ErrorCode!, decodedReceipt.ErrorMessage!);
        }
        var receiptId = decodedReceipt.Value;

        var receipt = await _db.TreasuryPaymentReceipts
            .SingleOrDefaultAsync(r => r.Id == receiptId && r.IsActive, ct).ConfigureAwait(false);
        if (receipt is null)
        {
            return Result<PaymentCorrectionDto>.Failure(
                ErrorCodes.NotFound, "TreasuryPaymentReceipt not found.");
        }

        // Validator guarantees Kind parses; defensive re-parse for the dispatch.
        if (!Enum.TryParse<PaymentCorrectionKind>(input.Kind, ignoreCase: false, out var kind))
        {
            return Result<PaymentCorrectionDto>.Failure(
                ErrorCodes.ValidationFailed, "Kind must be a known PaymentCorrectionKind enum name.");
        }

        long? redirectedToContributorId = null;
        if (kind == PaymentCorrectionKind.RedirectToPayer)
        {
            var decodedTarget = _sqids.TryDecode(input.RedirectedToContributorSqid!);
            if (decodedTarget.IsFailure)
            {
                return Result<PaymentCorrectionDto>.Failure(decodedTarget.ErrorCode!, decodedTarget.ErrorMessage!);
            }
            var targetId = decodedTarget.Value;

            var targetExists = await _db.Contributors
                .AnyAsync(c => c.Id == targetId && c.IsActive, ct).ConfigureAwait(false);
            if (!targetExists)
            {
                return Result<PaymentCorrectionDto>.Failure(
                    ErrorCodes.NotFound, "Redirect-target contributor not found.");
            }
            redirectedToContributorId = targetId;
        }

        if (kind == PaymentCorrectionKind.AdjustAmount
            && input.AdjustedAmount.HasValue
            && input.AdjustedAmount.Value > receipt.AmountReceived)
        {
            return Result<PaymentCorrectionDto>.Failure(
                ErrorCodes.ValidationFailed, AdjustedAmountExceedsOriginalMessage);
        }

        var now = _clock.UtcNow;
        var entity = new PaymentCorrection
        {
            OriginalTreasuryPaymentReceiptId = receiptId,
            RedirectedToContributorId = redirectedToContributorId,
            RedirectedToMonth = kind == PaymentCorrectionKind.RedirectToMonth ? input.RedirectedToMonth : null,
            Kind = kind,
            AdjustedAmount = kind == PaymentCorrectionKind.AdjustAmount ? input.AdjustedAmount : null,
            Status = PaymentCorrectionStatus.Draft,
            RequestedByUserId = _caller.UserId ?? 0L,
            Reason = input.Reason,
            CreatedUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.PaymentCorrections.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            correctionSqid = _sqids.Encode(entity.Id),
            originalReceiptSqid = input.OriginalReceiptSqid,
            kind = kind.ToString(),
            reason = input.Reason,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCreated,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(PaymentCorrection),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<PaymentCorrectionDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result> ApproveAsync(long correctionId, CancellationToken ct = default)
    {
        var correction = await _db.PaymentCorrections
            .SingleOrDefaultAsync(c => c.Id == correctionId && c.IsActive, ct).ConfigureAwait(false);
        if (correction is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "PaymentCorrection not found.");
        }
        if (correction.Status != PaymentCorrectionStatus.Draft)
        {
            return Result.Failure(ErrorCodes.Conflict, InvalidStateMessage);
        }

        var now = _clock.UtcNow;
        correction.Status = PaymentCorrectionStatus.Approved;
        correction.ApprovedByUserId = _caller.UserId;
        correction.UpdatedAtUtc = now;
        correction.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            correctionSqid = _sqids.Encode(correction.Id),
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditApproved,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(PaymentCorrection),
            correction.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> ApplyAsync(long correctionId, CancellationToken ct = default)
    {
        var correction = await _db.PaymentCorrections
            .SingleOrDefaultAsync(c => c.Id == correctionId && c.IsActive, ct).ConfigureAwait(false);
        if (correction is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "PaymentCorrection not found.");
        }
        if (correction.Status != PaymentCorrectionStatus.Approved)
        {
            return Result.Failure(ErrorCodes.Conflict, InvalidStateMessage);
        }

        var receipt = await _db.TreasuryPaymentReceipts
            .SingleOrDefaultAsync(r => r.Id == correction.OriginalTreasuryPaymentReceiptId && r.IsActive, ct)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "TreasuryPaymentReceipt not found.");
        }

        var now = _clock.UtcNow;

        // Dispatch the kind-specific receipt mutation.
        switch (correction.Kind)
        {
            case PaymentCorrectionKind.Reverse:
                receipt.DistributionStatus = TreasuryPaymentDistributionStatus.Failed;
                receipt.UndistributedRemainderAmount = receipt.AmountReceived;
                receipt.DistributionFailureReason = "PAYMENT_REVERSED";
                break;
            case PaymentCorrectionKind.RedirectToPayer:
                if (correction.RedirectedToContributorId.HasValue)
                {
                    receipt.PayerContributorId = correction.RedirectedToContributorId.Value;
                }
                break;
            case PaymentCorrectionKind.RedirectToMonth:
                if (correction.RedirectedToMonth.HasValue)
                {
                    receipt.ReportingMonth = correction.RedirectedToMonth.Value;
                }
                break;
            case PaymentCorrectionKind.AdjustAmount:
                if (correction.AdjustedAmount.HasValue)
                {
                    receipt.AmountReceived = correction.AdjustedAmount.Value;
                }
                break;
            default:
                return Result.Failure(ErrorCodes.ValidationFailed, "Unknown PaymentCorrectionKind.");
        }
        receipt.UpdatedAtUtc = now;
        receipt.UpdatedBy = _caller.UserSqid;

        correction.Status = PaymentCorrectionStatus.Applied;
        correction.AppliedUtc = now;
        correction.UpdatedAtUtc = now;
        correction.UpdatedBy = _caller.UserSqid;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            correctionSqid = _sqids.Encode(correction.Id),
            originalReceiptSqid = _sqids.Encode(receipt.Id),
            kind = correction.Kind.ToString(),
            appliedUtc = now.ToString("O", CultureInfo.InvariantCulture),
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditApplied,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(PaymentCorrection),
            correction.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.PaymentCorrected.Add(
            1,
            new KeyValuePair<string, object?>("kind", correction.Kind.ToString()));

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> CancelAsync(
        long correctionId,
        string reason,
        CancellationToken ct = default)
    {
        var cancelInput = new PaymentCorrectionCancelInputDto(reason);
        var validation = await _cancelValidator.ValidateAsync(cancelInput, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var correction = await _db.PaymentCorrections
            .SingleOrDefaultAsync(c => c.Id == correctionId && c.IsActive, ct).ConfigureAwait(false);
        if (correction is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "PaymentCorrection not found.");
        }
        if (correction.Status != PaymentCorrectionStatus.Draft)
        {
            return Result.Failure(ErrorCodes.Conflict, InvalidStateMessage);
        }

        var now = _clock.UtcNow;
        correction.Status = PaymentCorrectionStatus.Cancelled;
        correction.CancelReason = reason;
        correction.UpdatedAtUtc = now;
        correction.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            correctionSqid = _sqids.Encode(correction.Id),
            cancelReason = reason,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCancelled,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(PaymentCorrection),
            correction.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<PaymentCorrectionDto?> GetAsync(long correctionId, CancellationToken ct = default)
    {
        var correction = await _db.PaymentCorrections
            .SingleOrDefaultAsync(c => c.Id == correctionId && c.IsActive, ct).ConfigureAwait(false);
        return correction is null ? null : ToDto(correction);
    }

    /// <summary>Projects a <see cref="PaymentCorrection"/> entity into its outbound DTO with Sqid-encoded ids.</summary>
    /// <param name="c">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private PaymentCorrectionDto ToDto(PaymentCorrection c) => new(
        Id: _sqids.Encode(c.Id),
        OriginalReceiptSqid: _sqids.Encode(c.OriginalTreasuryPaymentReceiptId),
        Kind: c.Kind.ToString(),
        Status: c.Status.ToString(),
        RedirectedToContributorSqid: c.RedirectedToContributorId.HasValue
            ? _sqids.Encode(c.RedirectedToContributorId.Value)
            : null,
        RedirectedToMonth: c.RedirectedToMonth,
        AdjustedAmount: c.AdjustedAmount,
        RequestedByUserSqid: _sqids.Encode(c.RequestedByUserId),
        ApprovedByUserSqid: c.ApprovedByUserId.HasValue ? _sqids.Encode(c.ApprovedByUserId.Value) : null,
        Reason: c.Reason,
        CreatedUtc: c.CreatedUtc,
        AppliedUtc: c.AppliedUtc,
        CancelReason: c.CancelReason);
}
