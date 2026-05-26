using System.Collections.Generic;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.WorkflowNotifications;

/// <summary>
/// R0128 / R0173 — central dispatch seam for workflow lifecycle notifications. Replaces
/// the per-site direct calls to <c>INotificationService.EnqueueAsync</c> with a single
/// orchestrator that consults the per-workflow strategy table, resolves recipients per
/// role, applies quiet-hours scheduling, and delegates the actual channel dispatch back
/// to the existing notification service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Backward compatibility.</b> When no strategy is configured for the supplied
/// (workflow, event) pair the orchestrator falls back to the legacy default — Email +
/// InApp dispatched to the task assignee — so existing dispatch sites that have not
/// been migrated to register a strategy continue to behave exactly as they did before
/// R0128 landed.
/// </para>
/// <para>
/// <b>Suppression contract.</b> When a strategy exists AND its <c>IsEnabled</c> is
/// <c>false</c>, the orchestrator increments the
/// <c>cnas.workflow.notify.suppressed</c> counter and returns
/// <see cref="Result.Success"/> without dispatching anything. The success result is
/// deliberate — suppression is the explicit operator decision, not a failure mode.
/// </para>
/// <para>
/// <b>Recipient resolution.</b> The orchestrator iterates the strategy's
/// <c>RecipientRoles</c> list and resolves each role to zero or more
/// <c>UserProfile</c> ids:
/// <list type="bullet">
///   <item><c>Assignee</c> → <c>WorkflowTask.AssignedUserId</c> when non-null.</item>
///   <item><c>AssigneeSupervisor</c> → best-effort supervisor lookup; logged + skipped when absent.</item>
///   <item><c>Applicant</c> → the parent application's <c>SolicitantId</c> (mapped to the citizen user).</item>
///   <item><c>ProcessOwner</c> / <c>ApprovingManager</c> → role-resolved; logged + skipped when not configured.</item>
///   <item><c>CustomGroup:&lt;code&gt;</c> → all active <c>UserProfile</c> rows whose <c>Groups</c> array contains the code.</item>
/// </list>
/// Resolution failures are logged at WARN and skipped — they do NOT fail the dispatch.
/// </para>
/// <para>
/// <b>Quiet hours.</b> When a strategy carries a (Start, End) window expressed in local
/// minutes-of-day, the orchestrator tests the current local time against the window. A
/// hit defers dispatch by computing the next end-of-window UTC instant and forwarding
/// it to the underlying notification service. A miss dispatches immediately.
/// </para>
/// </remarks>
public interface IWorkflowNotificationOrchestrator
{
    /// <summary>
    /// Dispatches a workflow lifecycle notification per the strategy registered for
    /// (<paramref name="workflowDefinitionId"/>, <paramref name="eventCode"/>), falling
    /// back to the legacy default behaviour when no strategy exists.
    /// </summary>
    /// <param name="workflowDefinitionId">Raw <c>WorkflowDefinition.Id</c>.</param>
    /// <param name="workflowTaskId">
    /// Raw <c>WorkflowTask.Id</c> the event pertains to. Used to resolve the
    /// <c>Assignee</c> + <c>Applicant</c> recipient roles.
    /// </param>
    /// <param name="eventCode">Canonical event code from <see cref="WorkflowNotificationEvents"/>.</param>
    /// <param name="templateContext">
    /// Optional template variables merged into the notification body; passed through to
    /// the underlying dispatch service. May be null.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on apply (including the suppression case). Failure
    /// only when an unexpected internal error occurs — strategy lookup errors degrade
    /// to the legacy default behaviour rather than fail the workflow.
    /// </returns>
    Task<Result> DispatchAsync(
        long workflowDefinitionId,
        long workflowTaskId,
        string eventCode,
        IDictionary<string, string>? templateContext,
        CancellationToken cancellationToken = default);
}
