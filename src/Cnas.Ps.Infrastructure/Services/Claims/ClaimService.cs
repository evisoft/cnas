using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Claims;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Claims;

/// <summary>
/// R0831 / R0832 / TOR BP 1.3-B + BP 1.3-C — concrete implementation of
/// <see cref="IClaimService"/>. Owns the claims-registry lifecycle (register,
/// modify, cancel, dispute) plus the per-claim payment-application path that
/// drives <see cref="ClaimStatus"/> transitions.
/// </summary>
/// <remarks>
/// <para>
/// <b>ClaimNumber generation.</b> <see cref="RegisterAsync"/> generates the
/// stable external identifier in the format <c>CRN-{year}-{seq:000000}</c>
/// where the sequence is per-year. The next sequence is computed by
/// <c>Claims.Count(c =&gt; c.ClaimNumber.StartsWith("CRN-{year}-")) + 1</c>.
/// A defensive retry loop tolerates the rare contention case where two
/// concurrent registrations would produce the same number — on uniqueness
/// failure the loop re-computes and reinserts.
/// </para>
/// <para>
/// <b>Atomic payment application.</b>
/// <see cref="RegisterPaymentAsync"/> inserts the <see cref="ClaimPayment"/>
/// row AND mutates the parent claim's running totals in a single
/// <c>SaveChangesAsync</c> call so the database can never observe a payment
/// without the corresponding parent-side update. Overpayments are rejected
/// before any row is written.
/// </para>
/// <para>
/// <b>Audit.</b> Lifecycle transitions emit canonical audit events; the
/// terminal <c>Settled</c> / <c>Cancelled</c> / <c>Disputed</c> events fire at
/// Critical severity per CLAUDE.md §5.6.
/// </para>
/// </remarks>
public sealed class ClaimService : IClaimService
{
    /// <summary>Stable audit event code emitted when a claim is registered.</summary>
    public const string AuditRegistered = "CLAIM.REGISTERED";

    /// <summary>Stable audit event code emitted when a claim is modified.</summary>
    public const string AuditModified = "CLAIM.MODIFIED";

    /// <summary>Stable audit event code emitted when a claim is cancelled.</summary>
    public const string AuditCancelled = "CLAIM.CANCELLED";

    /// <summary>Stable audit event code emitted when a payment is applied to a claim.</summary>
    public const string AuditPaymentRegistered = "CLAIM.PAYMENT_REGISTERED";

    /// <summary>Stable audit event code emitted when a claim is settled (running total reaches principal).</summary>
    public const string AuditSettled = "CLAIM.SETTLED";

    /// <summary>Stable audit event code emitted when a claim is flipped to <see cref="ClaimStatus.Disputed"/>.</summary>
    public const string AuditDisputed = "CLAIM.DISPUTED";

    /// <summary>Stable failure message used when a payment would drive the running total past the principal.</summary>
    public const string OverpaymentMessage = "OVERPAYMENT_NOT_ALLOWED";

    /// <summary>Stable failure message used when the parent claim is already in a terminal state.</summary>
    public const string TerminalStateMessage = "CLAIM_TERMINAL_STATE";

    /// <summary>Stable failure message used when the dispute path is invoked on an already-disputed / settled / cancelled claim.</summary>
    public const string DisputeForbiddenMessage = "CLAIM_DISPUTE_FORBIDDEN";

    /// <summary>Maximum re-attempts when the unique <c>ClaimNumber</c> index rejects a freshly-generated number under contention.</summary>
    private const int MaxClaimNumberRetries = 5;

    /// <summary>
    /// R0016 — declarative <see cref="ClaimStatus"/> transition table. Replaces the
    /// hand-rolled <c>if</c>-ladders in <see cref="CancelAsync"/> /
    /// <see cref="DisputeAsync"/> with a single shared declaration of the lifecycle
    /// edges documented on the enum's XML. The legacy error codes
    /// (<see cref="ErrorCodes.Conflict"/>) + stable messages
    /// (<see cref="TerminalStateMessage"/> / <see cref="DisputeForbiddenMessage"/>)
    /// remain the wire shape — the table is consulted internally, and on denial the
    /// service maps the result back to its legacy code/message pair so callers see no
    /// change.
    /// </summary>
    /// <remarks>
    /// Documented edges:
    /// <list type="bullet">
    ///   <item><c>Open / PartiallyPaid</c> → <c>Cancelled / Disputed / Settled / PartiallyPaid</c></item>
    ///   <item><c>Disputed</c> → <c>Cancelled</c> (only — Modify is allowed but does not change Status).</item>
    ///   <item><c>Settled / Cancelled</c> — terminal (no outbound transitions).</item>
    /// </list>
    /// </remarks>
    internal static readonly Cnas.Ps.Core.ValueObjects.StatusTransitionTable<ClaimStatus> ClaimStatusTransitions =
        new(new Dictionary<ClaimStatus, IReadOnlySet<ClaimStatus>>
        {
            [ClaimStatus.Open] = new HashSet<ClaimStatus>
            {
                ClaimStatus.PartiallyPaid,
                ClaimStatus.Settled,
                ClaimStatus.Cancelled,
                ClaimStatus.Disputed,
            },
            [ClaimStatus.PartiallyPaid] = new HashSet<ClaimStatus>
            {
                ClaimStatus.PartiallyPaid,
                ClaimStatus.Settled,
                ClaimStatus.Cancelled,
                ClaimStatus.Disputed,
            },
            [ClaimStatus.Disputed] = new HashSet<ClaimStatus>
            {
                ClaimStatus.Cancelled,
            },
        });

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
    private readonly IValidator<ClaimRegisterInputDto> _registerValidator;
    private readonly IValidator<ClaimModifyInputDto> _modifyValidator;
    private readonly IValidator<ClaimPaymentInputDto> _paymentValidator;
    private readonly IValidator<ClaimReasonInputDto> _reasonValidator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (write surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly from service code.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated-caller information for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="registerValidator">Validator for register input.</param>
    /// <param name="modifyValidator">Validator for modify input.</param>
    /// <param name="paymentValidator">Validator for payment input.</param>
    /// <param name="reasonValidator">Validator for cancel / dispute input.</param>
    public ClaimService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<ClaimRegisterInputDto> registerValidator,
        IValidator<ClaimModifyInputDto> modifyValidator,
        IValidator<ClaimPaymentInputDto> paymentValidator,
        IValidator<ClaimReasonInputDto> reasonValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(registerValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(paymentValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _registerValidator = registerValidator;
        _modifyValidator = modifyValidator;
        _paymentValidator = paymentValidator;
        _reasonValidator = reasonValidator;
    }

    /// <inheritdoc />
    public async Task<Result<ClaimDto>> RegisterAsync(
        ClaimRegisterInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _registerValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ClaimDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(input.ContributorSqid);
        if (decoded.IsFailure)
        {
            return Result<ClaimDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var contributorId = decoded.Value;

        // Defensive payer-existence check — the row carries no navigation
        // property so a bogus ContributorId would persist dangling rows.
        var payerExists = await _db.Contributors
            .AnyAsync(c => c.Id == contributorId && c.IsActive, ct).ConfigureAwait(false);
        if (!payerExists)
        {
            return Result<ClaimDto>.Failure(
                ErrorCodes.NotFound, "Contributor not found.");
        }

        if (!Enum.TryParse<ClaimKind>(input.Kind, ignoreCase: false, out var kind))
        {
            // Validator guarantees this branch is unreachable; defence in depth.
            return Result<ClaimDto>.Failure(
                ErrorCodes.ValidationFailed, "Kind must be a known ClaimKind enum name.");
        }

        var now = _clock.UtcNow;
        var openedDate = input.OpenedDate == default
            ? DateOnly.FromDateTime(now)
            : input.OpenedDate;

        // Retry loop: under contention two concurrent registrations could
        // observe the same Count() result and produce the same ClaimNumber.
        // We re-compute the next number on uniqueness failure and re-insert.
        Claim? created = null;
        DbUpdateException? lastUniquenessFailure = null;
        for (var attempt = 0; attempt < MaxClaimNumberRetries; attempt++)
        {
            var year = openedDate.Year;
            var prefix = $"CRN-{year}-";
            var existingCount = await _db.Claims
                .CountAsync(c => c.ClaimNumber.StartsWith(prefix), ct).ConfigureAwait(false);
            var claimNumber = $"{prefix}{(existingCount + 1):D6}";

            var entity = new Claim
            {
                ContributorId = contributorId,
                ClaimNumber = claimNumber,
                Kind = kind,
                RelatedMonth = input.RelatedMonth,
                PrincipalAmount = input.PrincipalAmount,
                PaidAmount = 0m,
                RemainingAmount = input.PrincipalAmount,
                Status = ClaimStatus.Open,
                OpenedDate = openedDate,
                DueDate = input.DueDate,
                RelatedDocumentReference = input.RelatedDocumentReference,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.Claims.Add(entity);

            try
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                created = entity;
                break;
            }
            catch (DbUpdateException ex)
            {
                // Likely a uniqueness collision on ClaimNumber. Detach and
                // recompute the sequence — the next attempt will see the new
                // row counts.
                lastUniquenessFailure = ex;
                _db.Claims.Remove(entity);
            }
        }
        if (created is null)
        {
            return Result<ClaimDto>.Failure(
                ErrorCodes.Conflict,
                lastUniquenessFailure?.Message ?? "ClaimNumber generation contention exceeded retry budget.");
        }

        var details = JsonSerializer.Serialize(new
        {
            claimSqid = _sqids.Encode(created.Id),
            contributorSqid = input.ContributorSqid,
            claimNumber = created.ClaimNumber,
            kind = created.Kind.ToString(),
            principalAmount = created.PrincipalAmount,
            relatedMonth = created.RelatedMonth.ToString("O", CultureInfo.InvariantCulture),
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditRegistered,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Claim),
            created.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.ClaimRegistered.Add(
            1,
            new KeyValuePair<string, object?>("kind", created.Kind.ToString()));

        return Result<ClaimDto>.Success(ToDto(created));
    }

    /// <inheritdoc />
    public async Task<Result<ClaimDto>> ModifyAsync(
        long claimId,
        ClaimModifyInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _modifyValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ClaimDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var claim = await _db.Claims
            .SingleOrDefaultAsync(c => c.Id == claimId && c.IsActive, ct).ConfigureAwait(false);
        if (claim is null)
        {
            return Result<ClaimDto>.Failure(ErrorCodes.NotFound, "Claim not found.");
        }
        if (claim.Status is ClaimStatus.Settled or ClaimStatus.Cancelled)
        {
            return Result<ClaimDto>.Failure(ErrorCodes.Conflict, TerminalStateMessage);
        }

        var beforePrincipal = claim.PrincipalAmount;
        var beforeDueDate = claim.DueDate;
        var beforeReference = claim.RelatedDocumentReference;

        if (input.PrincipalAmount.HasValue)
        {
            claim.PrincipalAmount = input.PrincipalAmount.Value;
            claim.RemainingAmount = claim.PrincipalAmount - claim.PaidAmount;
        }
        if (input.DueDate.HasValue)
        {
            claim.DueDate = input.DueDate.Value;
        }
        if (input.RelatedDocumentReference is not null)
        {
            claim.RelatedDocumentReference = input.RelatedDocumentReference;
        }

        var now = _clock.UtcNow;
        claim.UpdatedAtUtc = now;
        claim.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            claimSqid = _sqids.Encode(claim.Id),
            claimNumber = claim.ClaimNumber,
            beforePrincipal,
            afterPrincipal = claim.PrincipalAmount,
            beforeDueDate,
            afterDueDate = claim.DueDate,
            beforeReference,
            afterReference = claim.RelatedDocumentReference,
            changeReason = input.ChangeReason,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditModified,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Claim),
            claim.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<ClaimDto>.Success(ToDto(claim));
    }

    /// <inheritdoc />
    public async Task<Result> CancelAsync(
        long claimId,
        string reason,
        CancellationToken ct = default)
    {
        var reasonInput = new ClaimReasonInputDto(reason);
        var validation = await _reasonValidator.ValidateAsync(reasonInput, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var claim = await _db.Claims
            .SingleOrDefaultAsync(c => c.Id == claimId && c.IsActive, ct).ConfigureAwait(false);
        if (claim is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Claim not found.");
        }

        // R0016 — delegate the legal-transition check to the declarative table. The
        // wire shape stays the same (legacy Conflict + TerminalStateMessage) — on
        // denial we translate the generic STATUS.ILLEGAL_TRANSITION code into the
        // historical contract callers have been written against.
        var transitionCheck = ClaimStatusTransitions.Validate(claim.Status, ClaimStatus.Cancelled);
        if (transitionCheck.IsFailure)
        {
            return Result.Failure(ErrorCodes.Conflict, TerminalStateMessage);
        }

        var now = _clock.UtcNow;
        claim.Status = ClaimStatus.Cancelled;
        claim.CancelReason = reason;
        claim.CancelledDate = DateOnly.FromDateTime(now);
        claim.UpdatedAtUtc = now;
        claim.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            claimSqid = _sqids.Encode(claim.Id),
            claimNumber = claim.ClaimNumber,
            cancelReason = reason,
            remainingAmount = claim.RemainingAmount,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCancelled,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Claim),
            claim.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<ClaimPaymentDto>> RegisterPaymentAsync(
        long claimId,
        ClaimPaymentInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _paymentValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ClaimPaymentDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var claim = await _db.Claims
            .SingleOrDefaultAsync(c => c.Id == claimId && c.IsActive, ct).ConfigureAwait(false);
        if (claim is null)
        {
            return Result<ClaimPaymentDto>.Failure(ErrorCodes.NotFound, "Claim not found.");
        }

        // iter-149 — Disputed claims are NOT modelled in the transition table (the
        // table only validates legal Status edges; the disputed-claim refusal is a
        // semantic policy on top of the lifecycle). Keep the dedicated guard
        // BEFORE the table lookup so the canonical "CLAIM_DISPUTED" message stays
        // the wire shape; the table would otherwise reject Disputed → PartiallyPaid
        // with a generic conflict that loses the dispute attribution.
        if (claim.Status == ClaimStatus.Disputed)
        {
            return Result<ClaimPaymentDto>.Failure(ErrorCodes.Conflict, "CLAIM_DISPUTED");
        }

        // iter-149 — delegate the terminal-state guard to the declarative
        // ClaimStatusTransitions table so RegisterPaymentAsync, CancelAsync, and
        // DisputeAsync share a single source of truth for the lifecycle edges.
        // We probe BOTH possible target statuses (Settled / PartiallyPaid). If
        // neither edge is legal from the current Status the claim is terminal
        // (Settled / Cancelled) — surface the legacy Conflict + TerminalStateMessage.
        // Probing both targets must happen BEFORE the overpayment check so a
        // payment against a terminal claim returns the canonical terminal-state
        // code rather than a misleading overpayment error.
        var settledProbe = ClaimStatusTransitions.Validate(claim.Status, ClaimStatus.Settled);
        var partialProbe = ClaimStatusTransitions.Validate(claim.Status, ClaimStatus.PartiallyPaid);
        if (settledProbe.IsFailure && partialProbe.IsFailure)
        {
            return Result<ClaimPaymentDto>.Failure(ErrorCodes.Conflict, TerminalStateMessage);
        }

        // Reject overpayment BEFORE any row is written. (At this point the claim
        // is in a non-terminal status so the math determines the legal target.)
        var projectedPaid = claim.PaidAmount + input.Amount;
        if (projectedPaid > claim.PrincipalAmount)
        {
            return Result<ClaimPaymentDto>.Failure(
                ErrorCodes.ValidationFailed, OverpaymentMessage);
        }

        long? treasuryReceiptId = null;
        if (!string.IsNullOrEmpty(input.TreasuryReceiptSqid))
        {
            var decodedReceipt = _sqids.TryDecode(input.TreasuryReceiptSqid);
            if (decodedReceipt.IsFailure)
            {
                return Result<ClaimPaymentDto>.Failure(
                    decodedReceipt.ErrorCode!, decodedReceipt.ErrorMessage!);
            }
            treasuryReceiptId = decodedReceipt.Value;
        }

        var now = _clock.UtcNow;
        var payment = new ClaimPayment
        {
            ClaimId = claim.Id,
            PaidDate = input.PaidDate,
            Amount = input.Amount,
            PaymentReference = input.PaymentReference,
            TreasuryPaymentReceiptId = treasuryReceiptId,
            Notes = input.Notes,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ClaimPayments.Add(payment);

        claim.PaidAmount = projectedPaid;
        claim.RemainingAmount = claim.PrincipalAmount - claim.PaidAmount;
        var settledNow = claim.RemainingAmount == 0m;
        if (settledNow)
        {
            claim.Status = ClaimStatus.Settled;
            claim.SettledDate = DateOnly.FromDateTime(now);
        }
        else
        {
            claim.Status = ClaimStatus.PartiallyPaid;
        }
        claim.UpdatedAtUtc = now;
        claim.UpdatedBy = _caller.UserSqid;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Notice-severity audit on every payment row.
        var paymentDetails = JsonSerializer.Serialize(new
        {
            claimSqid = _sqids.Encode(claim.Id),
            paymentSqid = _sqids.Encode(payment.Id),
            paidDate = payment.PaidDate.ToString("O", CultureInfo.InvariantCulture),
            amount = payment.Amount,
            runningTotal = claim.PaidAmount,
            remaining = claim.RemainingAmount,
            paymentReference = payment.PaymentReference,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditPaymentRegistered,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(ClaimPayment),
            payment.Id,
            paymentDetails,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        // Critical-severity audit when the final payment settles the claim.
        if (settledNow)
        {
            var settleDetails = JsonSerializer.Serialize(new
            {
                claimSqid = _sqids.Encode(claim.Id),
                claimNumber = claim.ClaimNumber,
                settledDate = claim.SettledDate?.ToString("O", CultureInfo.InvariantCulture),
                principalAmount = claim.PrincipalAmount,
            }, CachedJsonOptions);
            await _audit.RecordAsync(
                AuditSettled,
                AuditSeverity.Critical,
                _caller.UserSqid ?? "?",
                nameof(Claim),
                claim.Id,
                settleDetails,
                _caller.SourceIp,
                _caller.CorrelationId,
                ct).ConfigureAwait(false);
        }

        CnasMeter.ClaimPaymentApplied.Add(
            1,
            new KeyValuePair<string, object?>("outcome", settledNow ? "settled" : "partial"));

        return Result<ClaimPaymentDto>.Success(ToDto(payment));
    }

    /// <inheritdoc />
    public async Task<Result> DisputeAsync(
        long claimId,
        string reason,
        CancellationToken ct = default)
    {
        var reasonInput = new ClaimReasonInputDto(reason);
        var validation = await _reasonValidator.ValidateAsync(reasonInput, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var claim = await _db.Claims
            .SingleOrDefaultAsync(c => c.Id == claimId && c.IsActive, ct).ConfigureAwait(false);
        if (claim is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Claim not found.");
        }

        // R0016 — delegate the legal-transition check to the declarative table.
        // The table does NOT permit Settled → Disputed, Cancelled → Disputed, or
        // Disputed → Disputed, so this single call replaces the old three-way `if`.
        // The translation back to the legacy Conflict + DisputeForbiddenMessage
        // shape preserves the wire contract.
        var transitionCheck = ClaimStatusTransitions.Validate(claim.Status, ClaimStatus.Disputed);
        if (transitionCheck.IsFailure)
        {
            return Result.Failure(ErrorCodes.Conflict, DisputeForbiddenMessage);
        }

        var now = _clock.UtcNow;
        claim.Status = ClaimStatus.Disputed;
        claim.UpdatedAtUtc = now;
        claim.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            claimSqid = _sqids.Encode(claim.Id),
            claimNumber = claim.ClaimNumber,
            disputeReason = reason,
            remainingAmount = claim.RemainingAmount,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditDisputed,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Claim),
            claim.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ClaimDto?> GetAsync(long claimId, CancellationToken ct = default)
    {
        var claim = await _db.Claims
            .SingleOrDefaultAsync(c => c.Id == claimId && c.IsActive, ct).ConfigureAwait(false);
        return claim is null ? null : ToDto(claim);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClaimDto>> ListForContributorAsync(
        long contributorId,
        CancellationToken ct = default)
    {
        var rows = await _db.Claims
            .Where(c => c.ContributorId == contributorId && c.IsActive)
            .OrderByDescending(c => c.OpenedDate)
            .ThenBy(c => c.ClaimNumber)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ConvertAll(ToDto);
    }

    /// <summary>Projects a <see cref="Claim"/> entity into its outbound DTO with Sqid-encoded ids.</summary>
    /// <param name="c">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private ClaimDto ToDto(Claim c) => new(
        Id: _sqids.Encode(c.Id),
        ContributorSqid: _sqids.Encode(c.ContributorId),
        ClaimNumber: c.ClaimNumber,
        Kind: c.Kind.ToString(),
        Status: c.Status.ToString(),
        PrincipalAmount: c.PrincipalAmount,
        PaidAmount: c.PaidAmount,
        RemainingAmount: c.RemainingAmount,
        RelatedMonth: c.RelatedMonth,
        OpenedDate: c.OpenedDate,
        DueDate: c.DueDate,
        SettledDate: c.SettledDate,
        CancelledDate: c.CancelledDate,
        CancelReason: c.CancelReason,
        RelatedDocumentReference: c.RelatedDocumentReference);

    /// <summary>Projects a <see cref="ClaimPayment"/> entity into its outbound DTO with Sqid-encoded ids.</summary>
    /// <param name="p">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private ClaimPaymentDto ToDto(ClaimPayment p) => new(
        Id: _sqids.Encode(p.Id),
        ClaimSqid: _sqids.Encode(p.ClaimId),
        PaidDate: p.PaidDate,
        Amount: p.Amount,
        PaymentReference: p.PaymentReference,
        TreasuryReceiptSqid: p.TreasuryPaymentReceiptId.HasValue
            ? _sqids.Encode(p.TreasuryPaymentReceiptId.Value)
            : null,
        Notes: p.Notes);
}
