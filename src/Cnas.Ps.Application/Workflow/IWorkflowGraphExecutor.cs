using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Workflow;

/// <summary>
/// R0123 / TOR CF 16.05 — deterministic executor that drives a workflow run forward by
/// one step. Given the surrogate id of a JUST-COMPLETED <c>WorkflowTask</c>, the
/// executor reads the parent workflow's graph, follows the outgoing edges of the
/// matching <c>WorkflowGraphNode</c>, and materialises the next <c>WorkflowTask</c> rows
/// (or marks the run complete when an End node is reached).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-node semantics.</b>
/// <list type="bullet">
///   <item><c>Task</c> — creates a single new <c>WorkflowTask</c> for the next node and
///         returns its surrogate id.</item>
///   <item><c>AndSplit</c> — spawns one new <c>WorkflowTask</c> per outgoing edge; every
///         child's <c>ParentSplitTaskId</c> points back to the synthetic split-anchor
///         row created at the split.</item>
///   <item><c>AndJoin</c> — checks whether every sibling under the matching split anchor
///         has completed. When all are done the executor advances past the join;
///         otherwise it is a no-op (returns an empty id list).</item>
///   <item><c>OrSplit</c> — invokes <c>IWorkflowRulePackEvaluator</c> via the rule
///         engine and follows the edge whose label matches the verdict. On a
///         no-decision result the executor falls back to the first outgoing edge
///         (fail-open, R0123 spec).</item>
///   <item><c>OrJoin</c> — forwards immediately because the OR branches are mutually
///         exclusive and at most one ever reaches the join.</item>
///   <item><c>End</c> — marks the workflow run as complete and returns an empty list.</item>
/// </list>
/// </para>
/// <para>
/// <b>Idempotency contract.</b> Calling <see cref="AdvanceAsync"/> twice for the same
/// completed task is safe: the second call sees the next-step rows already exist and
/// returns an empty list. Production callers should not rely on this for crash
/// recovery — the executor's transaction is intended to be the only "advance" attempt
/// for a given task.
/// </para>
/// </remarks>
public interface IWorkflowGraphExecutor
{
    /// <summary>
    /// Advances the workflow run by reading the graph outgoing from the node anchored
    /// to <paramref name="completedWorkflowTaskId"/> and materialising the next set of
    /// <c>WorkflowTask</c> rows.
    /// </summary>
    /// <param name="completedWorkflowTaskId">Surrogate id of the task that just finished.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the list of newly-created task surrogate ids (empty when the run
    /// terminated, when an AND-join is still waiting, or when there is no next step).
    /// </returns>
    Task<Result<IReadOnlyList<long>>> AdvanceAsync(
        long completedWorkflowTaskId,
        CancellationToken ct = default);
}
