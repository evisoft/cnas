using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.SensitiveActions;

/// <summary>
/// R2273 / TOR SEC 027 — per-action handler invoked by the generic 4-eyes substrate at
/// approval time. The handler performs the actual mutation described by the request
/// payload and returns an optional PII-free JSON result that the substrate persists into
/// <c>SensitiveAdminAction.ExecutionResultJson</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract.</b> The handler MUST be idempotent — if invoked twice for the same
/// action row (e.g. retry after a transient failure) it MUST NOT double-apply the
/// mutation. The substrate's status guard prevents most double-fires, but a handler
/// that talks to an external system should still de-dup using a stable correlation key.
/// </para>
/// <para>
/// <b>Failure mode.</b> A handler that encounters an unrecoverable error returns a
/// <see cref="Result{T}"/> failure with a stable, sanitised message (no stack traces,
/// no PII). The substrate maps the failure to
/// <see cref="SensitiveAdminActionStatus.ExecutionFailed"/> and records the message in
/// <c>ExecutionFailureReason</c>.
/// </para>
/// <para>
/// <b>Missing handler.</b> When NO handler is registered for the action code at
/// approval time, the substrate records
/// <see cref="SensitiveAdminActionStatus.ExecutionFailed"/> with
/// <c>ExecutionFailureReason = "NO_HANDLER_REGISTERED"</c>. Approval still succeeds
/// (the audit + state transition fires) so the operator surface stays consistent.
/// </para>
/// </remarks>
public interface ISensitiveActionHandler
{
    /// <summary>Stable SCREAMING_SNAKE_CASE action code this handler handles.</summary>
    string ActionCode { get; }

    /// <summary>
    /// Executes the mutation described by <paramref name="action"/>'s payload.
    /// </summary>
    /// <param name="action">The approved action row carrying the payload + audit context.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// Success carrying an optional JSON-serialised result that the substrate writes to
    /// <see cref="SensitiveAdminAction.ExecutionResultJson"/>. Returning <c>null</c> is
    /// valid — the handler is not required to emit a result.
    /// </returns>
    Task<Result<string?>> ExecuteAsync(SensitiveAdminAction action, CancellationToken ct = default);
}
