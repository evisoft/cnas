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
/// R0814 / TOR BP 1.2-E — concrete implementation of
/// <see cref="IBassRefundService"/>. Owns the BASS-to-payer refund lifecycle
/// (Requested → Approved → IssuedToTreasury → Confirmed) plus the cancellation
/// path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Active-uniqueness defence.</b> <see cref="RequestAsync"/> pre-checks
/// the filtered unique index (one non-Cancelled refund per (payer, month))
/// before insert so the failure surfaces as a stable
/// <c>ACTIVE_REFUND_EXISTS</c> message rather than a raw
/// <c>DbUpdateException</c>.
/// </para>
/// <para>
/// <b>Overpayment gate.</b> The request path verifies the matching
/// <see cref="MonthlyContributionCalculation.OverpaymentAmount"/> is
/// strictly positive — refunds are only legitimate when the payer has
/// over-declared. Without a positive overpayment the service returns
/// <see cref="ErrorCodes.NotFound"/> with the
/// <c>OVERPAYMENT_NOT_FOUND</c> message.
/// </para>
/// </remarks>
public sealed class BassRefundService : IBassRefundService
{
    /// <summary>Stable audit event code emitted on refund request.</summary>
    public const string AuditRequested = "BASS_REFUND.REQUESTED";

    /// <summary>Stable audit event code emitted on refund approval.</summary>
    public const string AuditApproved = "BASS_REFUND.APPROVED";

    /// <summary>Stable audit event code emitted when the dispatch instruction is recorded.</summary>
    public const string AuditIssued = "BASS_REFUND.ISSUED";

    /// <summary>Stable audit event code emitted on Treasury confirmation.</summary>
    public const string AuditConfirmed = "BASS_REFUND.CONFIRMED";

    /// <summary>Stable audit event code emitted on cancellation.</summary>
    public const string AuditCancelled = "BASS_REFUND.CANCELLED";

    /// <summary>Stable failure message used when no positive overpayment exists.</summary>
    public const string OverpaymentNotFoundMessage = "OVERPAYMENT_NOT_FOUND";

    /// <summary>Stable failure message used when an active refund already exists.</summary>
    public const string ActiveRefundExistsMessage = "ACTIVE_REFUND_EXISTS";

    /// <summary>Stable failure message used when the lifecycle state forbids the transition.</summary>
    public const string InvalidStateMessage = "BASS_REFUND_INVALID_STATE";

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
    private readonly IValidator<BassRefundRequestInputDto> _requestValidator;
    private readonly IValidator<BassRefundIssueInputDto> _issueValidator;
    private readonly IValidator<BassRefundConfirmInputDto> _confirmValidator;
    private readonly IValidator<BassRefundCancelInputDto> _cancelValidator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (write surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated-caller context for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="requestValidator">Validator for the request input shape.</param>
    /// <param name="issueValidator">Validator for the issue-to-Treasury input shape.</param>
    /// <param name="confirmValidator">Validator for the confirm input shape.</param>
    /// <param name="cancelValidator">Validator for the cancel input shape.</param>
    public BassRefundService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<BassRefundRequestInputDto> requestValidator,
        IValidator<BassRefundIssueInputDto> issueValidator,
        IValidator<BassRefundConfirmInputDto> confirmValidator,
        IValidator<BassRefundCancelInputDto> cancelValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(requestValidator);
        ArgumentNullException.ThrowIfNull(issueValidator);
        ArgumentNullException.ThrowIfNull(confirmValidator);
        ArgumentNullException.ThrowIfNull(cancelValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _requestValidator = requestValidator;
        _issueValidator = issueValidator;
        _confirmValidator = confirmValidator;
        _cancelValidator = cancelValidator;
    }

    /// <inheritdoc />
    public async Task<Result<BassRefundDto>> RequestAsync(
        BassRefundRequestInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _requestValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<BassRefundDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(input.ContributorSqid);
        if (decoded.IsFailure)
        {
            return Result<BassRefundDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var contributorId = decoded.Value;

        // Verify the (contributor, month) tuple has a positive overpayment
        // calculation — refunds are only legitimate when the payer has
        // over-declared.
        var overpaymentExists = await _db.MonthlyContributionCalculations
            .AnyAsync(m => m.ContributorId == contributorId
                && m.Month == input.RelatedMonth
                && m.IsActive
                && m.OverpaymentAmount.HasValue
                && m.OverpaymentAmount.Value > 0m, ct).ConfigureAwait(false);
        if (!overpaymentExists)
        {
            return Result<BassRefundDto>.Failure(
                ErrorCodes.NotFound, OverpaymentNotFoundMessage);
        }

        // Active-refund uniqueness pre-check.
        var activeExists = await _db.BassRefunds
            .AnyAsync(r => r.ContributorId == contributorId
                && r.RelatedMonth == input.RelatedMonth
                && r.Status != BassRefundStatus.Cancelled
                && r.IsActive, ct).ConfigureAwait(false);
        if (activeExists)
        {
            return Result<BassRefundDto>.Failure(
                ErrorCodes.Conflict, ActiveRefundExistsMessage);
        }

        var now = _clock.UtcNow;
        var entity = new BassRefund
        {
            ContributorId = contributorId,
            RelatedMonth = input.RelatedMonth,
            RefundAmount = input.RefundAmount,
            Status = BassRefundStatus.Requested,
            AuthorisationDocumentReference = input.AuthorisationDocumentReference,
            RequestedByUserId = _caller.UserId ?? 0L,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.BassRefunds.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            refundSqid = _sqids.Encode(entity.Id),
            contributorSqid = input.ContributorSqid,
            relatedMonth = entity.RelatedMonth.ToString("O", CultureInfo.InvariantCulture),
            refundAmount = entity.RefundAmount,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditRequested,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(BassRefund),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.BassRefund.Add(
            1,
            new KeyValuePair<string, object?>("status", entity.Status.ToString()));

        return Result<BassRefundDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result> ApproveAsync(long refundId, CancellationToken ct = default)
    {
        var refund = await _db.BassRefunds
            .SingleOrDefaultAsync(r => r.Id == refundId && r.IsActive, ct).ConfigureAwait(false);
        if (refund is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "BassRefund not found.");
        }
        if (refund.Status != BassRefundStatus.Requested)
        {
            return Result.Failure(ErrorCodes.Conflict, InvalidStateMessage);
        }

        var now = _clock.UtcNow;
        refund.Status = BassRefundStatus.Approved;
        refund.ApprovedByUserId = _caller.UserId;
        refund.ApprovedDate = DateOnly.FromDateTime(now);
        refund.UpdatedAtUtc = now;
        refund.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            refundSqid = _sqids.Encode(refund.Id),
            approvedDate = refund.ApprovedDate?.ToString("O", CultureInfo.InvariantCulture),
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditApproved,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(BassRefund),
            refund.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.BassRefund.Add(
            1,
            new KeyValuePair<string, object?>("status", refund.Status.ToString()));

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> IssueToTreasuryAsync(
        long refundId,
        string treasuryDispatchReference,
        CancellationToken ct = default)
    {
        var issueInput = new BassRefundIssueInputDto(treasuryDispatchReference);
        var validation = await _issueValidator.ValidateAsync(issueInput, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var refund = await _db.BassRefunds
            .SingleOrDefaultAsync(r => r.Id == refundId && r.IsActive, ct).ConfigureAwait(false);
        if (refund is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "BassRefund not found.");
        }
        if (refund.Status != BassRefundStatus.Approved)
        {
            return Result.Failure(ErrorCodes.Conflict, InvalidStateMessage);
        }

        var now = _clock.UtcNow;
        refund.Status = BassRefundStatus.IssuedToTreasury;
        refund.TreasuryDispatchReference = treasuryDispatchReference;
        refund.IssuedDate = DateOnly.FromDateTime(now);
        refund.UpdatedAtUtc = now;
        refund.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            refundSqid = _sqids.Encode(refund.Id),
            treasuryDispatchReference,
            issuedDate = refund.IssuedDate?.ToString("O", CultureInfo.InvariantCulture),
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditIssued,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(BassRefund),
            refund.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.BassRefund.Add(
            1,
            new KeyValuePair<string, object?>("status", refund.Status.ToString()));

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> ConfirmAsync(
        long refundId,
        DateOnly confirmedDate,
        CancellationToken ct = default)
    {
        var confirmInput = new BassRefundConfirmInputDto(confirmedDate);
        var validation = await _confirmValidator.ValidateAsync(confirmInput, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var refund = await _db.BassRefunds
            .SingleOrDefaultAsync(r => r.Id == refundId && r.IsActive, ct).ConfigureAwait(false);
        if (refund is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "BassRefund not found.");
        }
        if (refund.Status != BassRefundStatus.IssuedToTreasury)
        {
            return Result.Failure(ErrorCodes.Conflict, InvalidStateMessage);
        }

        var now = _clock.UtcNow;
        refund.Status = BassRefundStatus.Confirmed;
        refund.ConfirmedDate = confirmedDate;
        refund.UpdatedAtUtc = now;
        refund.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            refundSqid = _sqids.Encode(refund.Id),
            confirmedDate = confirmedDate.ToString("O", CultureInfo.InvariantCulture),
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditConfirmed,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(BassRefund),
            refund.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.BassRefund.Add(
            1,
            new KeyValuePair<string, object?>("status", refund.Status.ToString()));

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> CancelAsync(
        long refundId,
        string reason,
        CancellationToken ct = default)
    {
        var cancelInput = new BassRefundCancelInputDto(reason);
        var validation = await _cancelValidator.ValidateAsync(cancelInput, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var refund = await _db.BassRefunds
            .SingleOrDefaultAsync(r => r.Id == refundId && r.IsActive, ct).ConfigureAwait(false);
        if (refund is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "BassRefund not found.");
        }
        if (refund.Status is not (BassRefundStatus.Requested or BassRefundStatus.Approved))
        {
            return Result.Failure(ErrorCodes.Conflict, InvalidStateMessage);
        }

        var now = _clock.UtcNow;
        refund.Status = BassRefundStatus.Cancelled;
        refund.CancelReason = reason;
        refund.CancelledDate = DateOnly.FromDateTime(now);
        refund.UpdatedAtUtc = now;
        refund.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            refundSqid = _sqids.Encode(refund.Id),
            cancelReason = reason,
            cancelledDate = refund.CancelledDate?.ToString("O", CultureInfo.InvariantCulture),
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCancelled,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(BassRefund),
            refund.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.BassRefund.Add(
            1,
            new KeyValuePair<string, object?>("status", refund.Status.ToString()));

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<BassRefundDto?> GetAsync(long refundId, CancellationToken ct = default)
    {
        var refund = await _db.BassRefunds
            .SingleOrDefaultAsync(r => r.Id == refundId && r.IsActive, ct).ConfigureAwait(false);
        return refund is null ? null : ToDto(refund);
    }

    /// <summary>Projects a <see cref="BassRefund"/> entity into its outbound DTO with Sqid-encoded ids.</summary>
    /// <param name="r">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private BassRefundDto ToDto(BassRefund r) => new(
        Id: _sqids.Encode(r.Id),
        ContributorSqid: _sqids.Encode(r.ContributorId),
        RelatedMonth: r.RelatedMonth,
        RefundAmount: r.RefundAmount,
        Status: r.Status.ToString(),
        AuthorisationDocumentReference: r.AuthorisationDocumentReference,
        RequestedByUserSqid: _sqids.Encode(r.RequestedByUserId),
        ApprovedByUserSqid: r.ApprovedByUserId.HasValue ? _sqids.Encode(r.ApprovedByUserId.Value) : null,
        ApprovedDate: r.ApprovedDate,
        TreasuryDispatchReference: r.TreasuryDispatchReference,
        IssuedDate: r.IssuedDate,
        ConfirmedDate: r.ConfirmedDate,
        CancelReason: r.CancelReason,
        CancelledDate: r.CancelledDate);
}
