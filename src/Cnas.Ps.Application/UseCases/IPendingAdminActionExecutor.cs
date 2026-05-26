using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// Per-operation executor invoked by <see cref="IPendingAdminActionService"/> after a
/// pending admin action has cleared the maker-checker (4-eyes) guard
/// (R0058 / SEC 027). Each executor handles one or more stable operation codes
/// (e.g. <c>USER.SUSPEND</c>, <c>ROLE.REVOKE</c>) and runs the actual side-effect when
/// the action is approved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration model.</b> Executors are registered as multiple-instance services in
/// the DI container; the service injects <see cref="System.Collections.Generic.IEnumerable{T}"/>
/// and dispatches to the first executor whose <see cref="Handles"/> returns
/// <c>true</c>. Add a new gated admin action by writing a new executor and registering
/// it alongside the rest — no edits to the maker-checker service are required.
/// </para>
/// <para>
/// <b>PII discipline.</b> The payload supplied to <see cref="ExecuteAsync"/> is the
/// verbatim text the maker submitted. Executors MUST NOT log raw payload bytes (the
/// payload may carry user-supplied free text) and MUST validate the payload shape
/// before applying any side effect.
/// </para>
/// </remarks>
public interface IPendingAdminActionExecutor
{
    /// <summary>
    /// Returns <c>true</c> when this executor recognises the supplied stable
    /// operation code. Called by the service at both submit time (fail-fast unknown
    /// operations) and approve time (dispatch to the right executor). Case-sensitive
    /// — operation codes are SCREAMING_SNAKE_CASE constants.
    /// </summary>
    /// <param name="operation">Stable operation code, e.g. <c>USER.SUSPEND</c>.</param>
    /// <returns><c>true</c> when this executor handles the code.</returns>
    bool Handles(string operation);

    /// <summary>
    /// Applies the side effect represented by the pending admin action. Invoked once
    /// per approval — never before maker-checker validation. The service guarantees
    /// that <see cref="Handles"/> returned <c>true</c> for <paramref name="operation"/>
    /// before this method is called.
    /// </summary>
    /// <param name="operation">Stable operation code; the same string passed to <see cref="Handles"/>.</param>
    /// <param name="payloadJson">Verbatim payload supplied by the maker at submit time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="Result"/> describing whether the side effect succeeded. Failures
    /// surface to the caller of <c>ApproveAsync</c>; the row remains
    /// <c>Approved</c> regardless because the human decision has already been
    /// recorded — a follow-up operational fix is needed for executor failures.
    /// </returns>
    Task<Result> ExecuteAsync(string operation, string payloadJson, CancellationToken ct = default);
}
