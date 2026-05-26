using System.Collections.Generic;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.WorkflowNotifications;

/// <summary>
/// R0128 / R0173 — synchronous, hot-path lookup that the workflow-notification
/// orchestrator consults before dispatching a notification batch. Returns either the
/// configured strategy projection or <c>null</c> when no strategy has been recorded for
/// the supplied (workflow, event) pair.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hot-path discipline.</b> Implementations MUST be non-blocking — the resolver is
/// invoked synchronously on every workflow-event dispatch. The reference implementation
/// (<c>WorkflowNotificationStrategyResolver</c>) backs this by an in-memory
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
/// snapshot rebuilt on a 60 s cadence by a hosted refresh job and on demand by the
/// CRUD service.
/// </para>
/// <para>
/// <b>No-match contract.</b> Returns <c>null</c> when no strategy exists for the
/// (workflow, event) pair. The orchestrator interprets a null result as "fall back to
/// the legacy default behaviour" — Email + InApp to the assignee — so existing dispatch
/// sites that have not yet had a strategy provisioned continue to work without change.
/// </para>
/// <para>
/// <b>Synchronous shape.</b> The interface is deliberately not <see cref="System.Threading.Tasks.Task"/>-returning;
/// the dictionary lookup is allocation-free and the orchestrator branches on the result
/// inside its own async method. Forcing an async API here would add a redundant state
/// machine on the hot path.
/// </para>
/// </remarks>
public interface IWorkflowNotificationStrategyResolver
{
    /// <summary>
    /// Returns the strategy view for the supplied (workflow, event) pair, or <c>null</c>
    /// when no strategy exists.
    /// </summary>
    /// <param name="workflowDefinitionId">Raw surrogate id of the <see cref="WorkflowDefinition"/>.</param>
    /// <param name="eventCode">Canonical event code from <see cref="WorkflowNotificationEvents"/>.</param>
    /// <returns>The strategy view, or <c>null</c> on no-match.</returns>
    WorkflowNotificationStrategyView? Resolve(long workflowDefinitionId, string eventCode);
}

/// <summary>
/// Read-only projection of a <see cref="WorkflowNotificationStrategy"/> shaped for the
/// orchestrator's hot path. Avoids exposing the EF entity (which carries audit metadata
/// the orchestrator does not need) and freezes the channel + recipient lists so the
/// orchestrator cannot accidentally mutate the cache.
/// </summary>
/// <param name="IsEnabled">
/// Explicit on/off flag. When <c>false</c> the orchestrator suppresses dispatch + bumps
/// the suppressed counter.
/// </param>
/// <param name="Channels">
/// Frozen list of channels the orchestrator iterates over. Order follows the persisted
/// list order so operators can rely on a deterministic dispatch sequence in tests.
/// </param>
/// <param name="RecipientRoles">Frozen list of recipient role codes; see entity remarks.</param>
/// <param name="TemplateCodeOverride">
/// Optional template code override; null defers to the orchestrator's default template.
/// </param>
/// <param name="QuietHours">
/// Optional (Start, End) tuple of local-time minute-of-day values; null when no quiet
/// hours apply.
/// </param>
public sealed record WorkflowNotificationStrategyView(
    bool IsEnabled,
    IReadOnlyList<NotificationChannel> Channels,
    IReadOnlyList<string> RecipientRoles,
    string? TemplateCodeOverride,
    (int Start, int End)? QuietHours);
