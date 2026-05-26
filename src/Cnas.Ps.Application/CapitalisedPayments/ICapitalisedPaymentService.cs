using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.CapitalisedPayments;

/// <summary>
/// R1202 / TOR §3.4-C — service façade for the capitalised-payment registry.
/// Owns the request lifecycle (create / modify / submit / compute / approve /
/// reject / mark-settled / cancel) plus the lookup / listing surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b> Every successful mutation emits a stable audit
/// event at <see cref="AuditSeverity.Critical"/> severity (PII / financial
/// data — see CLAUDE.md §5.6):
/// <list type="bullet">
///   <item><see cref="CreateAsync"/> → <c>CAP_PAY.CREATED</c>.</item>
///   <item><see cref="ModifyAsync"/> → <c>CAP_PAY.MODIFIED</c>.</item>
///   <item><see cref="SubmitAsync"/> → <c>CAP_PAY.SUBMITTED</c>.</item>
///   <item><see cref="ComputeAsync"/> → <c>CAP_PAY.COMPUTED</c>.</item>
///   <item><see cref="ApproveAsync"/> → <c>CAP_PAY.APPROVED</c>.</item>
///   <item><see cref="RejectAsync"/> → <c>CAP_PAY.REJECTED</c>.</item>
///   <item><see cref="MarkSettledAsync"/> → <c>CAP_PAY.SETTLED</c>.</item>
///   <item><see cref="CancelAsync"/> → <c>CAP_PAY.CANCELLED</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>State transitions.</b> Strict:
/// <c>Draft → Submitted | Cancelled</c>;
/// <c>Submitted → Computing | Cancelled</c>;
/// <c>Computing → ComputedAwaitingApproval | Cancelled</c>;
/// <c>ComputedAwaitingApproval → Approved | Rejected | Cancelled</c>;
/// <c>Approved → Settled | Cancelled</c>;
/// <c>Settled / Rejected / Cancelled</c> are terminal.
/// Invalid transitions return <see cref="ErrorCodes.Conflict"/> with the
/// stable message <c>CAP_PAY.INVALID_TRANSITION</c>.
/// </para>
/// <para>
/// <b>Sqids everywhere.</b> Identifiers crossing the boundary are Sqid-encoded
/// per CLAUDE.md RULE 3; the service decodes them internally before touching
/// the DbContext. IDNP / IDNO are NOT Sqids — they are stable external
/// identifiers that the application encrypts at rest and never returns
/// plaintext on reads.
/// </para>
/// </remarks>
public interface ICapitalisedPaymentService
{
    /// <summary>R1202 — opens a new request in <c>Draft</c>. Auto-generates the request number.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the persisted <see cref="CapitalisedPaymentRequestDto"/>; otherwise a typed failure.</returns>
    Task<Result<CapitalisedPaymentRequestDto>> CreateAsync(
        CapitalisedPaymentRequestCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1202 — modifies an outstanding <c>Draft</c> request. Refused for any non-Draft row.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Modify payload (one or more fields + ChangeReason).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the updated DTO; otherwise a typed failure.</returns>
    Task<Result<CapitalisedPaymentRequestDto>> ModifyAsync(
        string sqid,
        CapitalisedPaymentRequestModifyInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1202 — transitions <c>Draft</c> → <c>Submitted</c>.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the updated DTO; otherwise a typed failure.</returns>
    Task<Result<CapitalisedPaymentRequestDto>> SubmitAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// R1202 — runs the present-value computation: transitions <c>Submitted</c>
    /// → <c>Computing</c> → <c>ComputedAwaitingApproval</c>. Persists a
    /// <c>CapitalisedPaymentDecision</c> row carrying the computed amount and
    /// the per-period breakdown.
    /// </summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the persisted decision DTO; otherwise a typed failure.</returns>
    Task<Result<CapitalisedPaymentDecisionDto>> ComputeAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>R1202 — transitions <c>ComputedAwaitingApproval</c> → <c>Approved</c>.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Approval envelope (mandatory note).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the updated decision DTO; otherwise a typed failure.</returns>
    Task<Result<CapitalisedPaymentDecisionDto>> ApproveAsync(
        string sqid,
        CapitalisedPaymentApprovalInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1202 — transitions <c>ComputedAwaitingApproval</c> → <c>Rejected</c>.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the updated decision DTO; otherwise a typed failure.</returns>
    Task<Result<CapitalisedPaymentDecisionDto>> RejectAsync(
        string sqid,
        CapitalisedPaymentReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// R1202 — transitions <c>Approved</c> → <c>Settled</c> when the liquidator
    /// pays the capitalised amount. The treasury-receipt Sqid is recorded on
    /// the audit row.
    /// </summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Settlement envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the updated DTO; otherwise a typed failure.</returns>
    Task<Result<CapitalisedPaymentRequestDto>> MarkSettledAsync(
        string sqid,
        CapitalisedPaymentSettlementInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1202 — transitions any non-terminal status → <c>Cancelled</c> with a reason.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the updated DTO; otherwise a typed failure.</returns>
    Task<Result<CapitalisedPaymentRequestDto>> CancelAsync(
        string sqid,
        CapitalisedPaymentReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1202 — fetches a single request by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded request id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the DTO; on missing row <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result<CapitalisedPaymentRequestDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>R1202 — paged list filtered by status / obligation kind / beneficiary hash.</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the page DTO; on validation failure <see cref="ErrorCodes.ValidationFailed"/>.</returns>
    Task<Result<CapitalisedPaymentRequestPageDto>> ListAsync(
        CapitalisedPaymentRequestFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>R1202 — fetches the most-recent decision row for a request.</summary>
    /// <param name="requestSqid">Sqid-encoded request id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the decision DTO; on missing row <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result<CapitalisedPaymentDecisionDto>> GetLatestDecisionAsync(
        string requestSqid,
        CancellationToken cancellationToken = default);
}
