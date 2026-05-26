using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// Maker-checker / 4-eyes workflow service for sensitive administrative actions
/// (R0058 — SEC 027). Persists a <c>PendingAdminAction</c> row at submit time, blocks
/// self-approval, enforces a TTL on undecided actions, and dispatches to a registered
/// <see cref="IPendingAdminActionExecutor"/> once a second administrator approves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Roles.</b> Every entry point assumes the caller satisfies the
/// <c>CnasAdmin</c> policy at the controller; the service performs a defense-in-depth
/// role check at the service boundary as well (CLAUDE.md §5.4 — deny by default).
/// </para>
/// <para>
/// <b>Sqid contract.</b> All identifiers crossing the API boundary are Sqid-encoded
/// per CLAUDE.md RULE 3. <see cref="SubmitAsync"/> returns the Sqid of the new row;
/// <see cref="ApproveAsync"/> / <see cref="RejectAsync"/> accept that Sqid back.
/// </para>
/// </remarks>
public interface IPendingAdminActionService
{
    /// <summary>
    /// Submits a new pending admin action on behalf of the calling administrator
    /// (the <i>maker</i>). The action is persisted with
    /// <c>Status</c> = <c>Pending</c> and an expiry of
    /// <c>now + ttl</c> (default 24 h); a second administrator must later approve
    /// before the executor runs.
    /// </summary>
    /// <param name="operation">Stable operation code recognised by exactly one registered
    /// <see cref="IPendingAdminActionExecutor"/>. Unknown codes are rejected fail-fast
    /// with <see cref="ErrorCodes.MakerCheckerUnknownOperation"/>.</param>
    /// <param name="payloadJson">Verbatim payload that the executor will consume when the
    /// action is approved. MUST NOT contain PII — see the entity's class-level remarks.</param>
    /// <param name="ttl">Override for the default 24-hour TTL window. Pass <c>null</c> to
    /// keep the default. Negative or zero spans are clamped to a 1-minute minimum.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The Sqid-encoded id of the new pending row on success.</returns>
    Task<Result<string>> SubmitAsync(string operation, string payloadJson, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>
    /// Approves a pending admin action — the calling administrator becomes the
    /// <i>checker</i>. Validations enforced in order: row exists + active, status is
    /// <c>Pending</c>, current time &lt; <c>ExpiresAtUtc</c>, maker ≠ checker. On
    /// success the status flips to <c>Approved</c>, the checker user-id and timestamp
    /// are recorded, and the matching executor is invoked exactly once.
    /// </summary>
    /// <param name="actionSqid">Sqid-encoded id of the pending row, as returned by
    /// <see cref="SubmitAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success; otherwise a failure carrying one of
    /// <see cref="ErrorCodes.InvalidSqid"/>, <see cref="ErrorCodes.NotFound"/>,
    /// <see cref="ErrorCodes.Forbidden"/>,
    /// <see cref="ErrorCodes.MakerCheckerSelfApprovalForbidden"/>,
    /// <see cref="ErrorCodes.MakerCheckerAlreadyDecided"/>, or
    /// <see cref="ErrorCodes.MakerCheckerExpired"/>.
    /// </returns>
    Task<Result> ApproveAsync(string actionSqid, CancellationToken ct = default);

    /// <summary>
    /// Rejects a pending admin action with a free-form reason. Mirrors
    /// <see cref="ApproveAsync"/> for guards (maker ≠ checker, TTL, already-decided)
    /// but never invokes the executor — a rejection simply records the decision and
    /// closes the row.
    /// </summary>
    /// <param name="actionSqid">Sqid-encoded id of the pending row.</param>
    /// <param name="reason">Free-form rejection reason (capped at 512 chars by the EF mapping).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The same failure set as <see cref="ApproveAsync"/>, plus
    /// <see cref="ErrorCodes.ValidationFailed"/> when the reason is empty.</returns>
    Task<Result> RejectAsync(string actionSqid, string reason, CancellationToken ct = default);

    /// <summary>
    /// Pages through the still-pending admin actions for the administrator audience.
    /// Returns only rows whose <c>Status == Pending</c> AND <c>ExpiresAtUtc &gt; now</c>;
    /// expired rows are intentionally hidden so checkers don't waste a click on
    /// something the sweeper will soon close.
    /// </summary>
    /// <param name="page">1-based pagination input; <c>PageSize</c> is clamped to
    /// <c>[1, 200]</c> by the service.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A paged list of pending actions; identifiers are Sqid-encoded per
    /// CLAUDE.md RULE 3.</returns>
    Task<Result<PagedResult<PendingAdminActionItem>>> ListPendingAsync(PageRequest page, CancellationToken ct = default);
}
