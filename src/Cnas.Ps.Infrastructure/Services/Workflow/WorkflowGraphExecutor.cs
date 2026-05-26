using System.Collections.Generic;
using System.Linq;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Workflow;
using Cnas.Ps.Application.WorkflowRules;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Workflow;

/// <summary>
/// R0123 / TOR CF 16.05 — default <see cref="IWorkflowGraphExecutor"/> implementation.
/// Drives a workflow run forward by one step against the persisted node/edge graph.
/// The executor is deterministic and stateless beyond what it reads from the DB; it
/// becomes the engine of record until the Camunda/Operaton adapter takes over.
/// </summary>
/// <remarks>
/// <para>
/// <b>Workflow-definition resolution.</b> The completed task carries
/// <see cref="WorkflowTask.NodeCode"/> but not a direct FK to its workflow definition.
/// The executor resolves the parent workflow via
/// <c>WorkflowTask → Dossier → ServiceApplication → ServicePassport.WorkflowCode →
/// WorkflowDefinition.IsCurrent=true</c> — the same chain used by R0124's rule engine.
/// </para>
/// <para>
/// <b>OrSplit fail-open.</b> When the rule engine returns no decision (no transition
/// rule pack configured OR the verdict is "allow" with no matching label) the
/// executor falls back to the first outgoing edge by ascending
/// <see cref="WorkflowGraphEdge.OrderIndex"/>. The contract is documented on
/// <see cref="IWorkflowGraphExecutor"/>.
/// </para>
/// <para>
/// <b>AND-join readiness.</b> When the next node is an AndJoin the executor counts
/// completed siblings of the just-completed task using
/// <see cref="WorkflowTask.ParentSplitTaskId"/>. If every sibling is in
/// <see cref="WorkflowTaskStatus.Completed"/> the executor advances past the join;
/// otherwise the call is a no-op (returns an empty list).
/// </para>
/// </remarks>
public sealed class WorkflowGraphExecutor : IWorkflowGraphExecutor
{
    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IWorkflowRulePackEvaluator _ruleEvaluator;
    private readonly ILogger<WorkflowGraphExecutor> _logger;

    /// <summary>Constructs the executor with its DI dependencies.</summary>
    /// <param name="db">EF Core context abstraction.</param>
    /// <param name="clock">UTC clock — never <see cref="System.DateTime.UtcNow"/> directly.</param>
    /// <param name="caller">Authenticated caller; supplies the actor id for new task rows.</param>
    /// <param name="ruleEvaluator">Rule-pack evaluator used to pick an OR-split branch.</param>
    /// <param name="logger">Structured logger for executor diagnostics.</param>
    public WorkflowGraphExecutor(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IWorkflowRulePackEvaluator ruleEvaluator,
        ILogger<WorkflowGraphExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(ruleEvaluator);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _clock = clock;
        _caller = caller;
        _ruleEvaluator = ruleEvaluator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<long>>> AdvanceAsync(
        long completedWorkflowTaskId,
        CancellationToken ct = default)
    {
        var task = await _db.WorkflowTasks
            .SingleOrDefaultAsync(t => t.Id == completedWorkflowTaskId, ct)
            .ConfigureAwait(false);
        if (task is null)
        {
            return Result<IReadOnlyList<long>>.Failure(
                ErrorCodes.NotFound,
                $"WorkflowTask {completedWorkflowTaskId} not found.");
        }

        if (string.IsNullOrEmpty(task.NodeCode))
        {
            // Legacy task with no graph anchor — return empty, not failure. A legacy
            // task simply has no executable next-step.
            return Result<IReadOnlyList<long>>.Success(System.Array.Empty<long>());
        }

        var workflowId = await ResolveWorkflowDefinitionIdAsync(task, ct).ConfigureAwait(false);
        if (workflowId is null)
        {
            return Result<IReadOnlyList<long>>.Failure(
                ErrorCodes.NotFound,
                $"Could not resolve workflow definition for task {completedWorkflowTaskId}.");
        }

        // Resolve the current node by code under the pinned workflow definition.
        var currentNode = await _db.WorkflowGraphNodes
            .SingleOrDefaultAsync(
                n => n.WorkflowDefinitionId == workflowId.Value
                  && n.NodeCode == task.NodeCode
                  && n.IsActive,
                ct).ConfigureAwait(false);
        if (currentNode is null)
        {
            return Result<IReadOnlyList<long>>.Success(System.Array.Empty<long>());
        }

        var outgoingEdges = await _db.WorkflowGraphEdges
            .Where(e => e.WorkflowDefinitionId == workflowId.Value
                     && e.SourceNodeId == currentNode.Id
                     && e.IsActive)
            .OrderBy(e => e.OrderIndex)
            .ToListAsync(ct).ConfigureAwait(false);

        var createdIds = new List<long>();
        foreach (var edge in await PickNextEdgesAsync(currentNode, outgoingEdges, task, ct).ConfigureAwait(false))
        {
            var target = await _db.WorkflowGraphNodes
                .SingleOrDefaultAsync(n => n.Id == edge.TargetNodeId, ct)
                .ConfigureAwait(false);
            if (target is null) continue;

            var advanced = await AdvanceToNodeAsync(workflowId.Value, target, task, ct).ConfigureAwait(false);
            createdIds.AddRange(advanced);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<long>>.Success(createdIds);
    }

    /// <summary>
    /// Resolves the workflow-definition surrogate id that owns <paramref name="task"/>'s
    /// graph by walking the task → dossier → application → passport → workflow chain.
    /// Mirrors the resolution used by R0124's rule engine so the two stay in lockstep.
    /// </summary>
    /// <param name="task">The completed workflow task.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The workflow-definition id when found; <c>null</c> when any link is missing.</returns>
    private async Task<long?> ResolveWorkflowDefinitionIdAsync(WorkflowTask task, CancellationToken ct)
    {
        var query = from t in _db.WorkflowTasks
                    where t.Id == task.Id
                    join d in _db.Dossiers on t.DossierId equals d.Id into ds
                    from d in ds.DefaultIfEmpty()
                    join a in _db.Applications on d.ApplicationId equals a.Id into apps
                    from a in apps.DefaultIfEmpty()
                    join p in _db.ServicePassports on a.ServicePassportId equals p.Id into passports
                    from p in passports.DefaultIfEmpty()
                    join w in _db.WorkflowDefinitions on p.WorkflowCode equals w.Code into wfs
                    from w in wfs.DefaultIfEmpty()
                    where w == null || w.IsCurrent
                    select w == null ? (long?)null : (long?)w.Id;
        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Decides which outgoing edge(s) the executor should follow given the current node
    /// kind. Sequential tasks return the single edge (or empty when none); AND-splits
    /// return every edge; OR-splits invoke the rule engine and select the matching one
    /// (or the lowest-order edge on a no-decision fall-back).
    /// </summary>
    /// <param name="currentNode">The just-completed node.</param>
    /// <param name="outgoing">Outgoing edges from the node, pre-sorted by order index.</param>
    /// <param name="task">The completed task (used to anchor rule-engine context).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The edges to traverse. Empty list ⇒ no advance.</returns>
    private async Task<IReadOnlyList<WorkflowGraphEdge>> PickNextEdgesAsync(
        WorkflowGraphNode currentNode,
        IReadOnlyList<WorkflowGraphEdge> outgoing,
        WorkflowTask task,
        CancellationToken ct)
    {
        if (outgoing.Count == 0) return System.Array.Empty<WorkflowGraphEdge>();

        switch (currentNode.Kind)
        {
            case WorkflowNodeKind.AndSplit:
                return outgoing;

            case WorkflowNodeKind.OrSplit:
                {
                    // Invoke the rule engine. The engine's "allow with annotations" path
                    // is how an OR-split conveys the chosen label — annotation key
                    // "branch" carries the label string. The fail-open contract on
                    // R0123 says: when the engine returns no decision, take the first
                    // outgoing edge.
                    var verdict = await SafeEvaluateAsync(currentNode, task, ct).ConfigureAwait(false);
                    if (verdict?.Annotations is { } anns
                        && anns.TryGetValue("branch", out var label)
                        && !string.IsNullOrEmpty(label))
                    {
                        var match = outgoing.FirstOrDefault(e =>
                            string.Equals(e.Label, label, System.StringComparison.Ordinal));
                        if (match is not null)
                        {
                            return new[] { match };
                        }
                    }
                    // No decision OR no matching label — fall open to the first edge.
                    return new[] { outgoing[0] };
                }

            default:
                // Sequential / Task / End / Start / OrJoin / AndJoin — single edge.
                return new[] { outgoing[0] };
        }
    }

    /// <summary>
    /// Invokes the rule evaluator for an OR-split node and returns its verdict, or
    /// <c>null</c> when no <c>ConditionExpression</c> is configured or the evaluator
    /// throws. Wrapping the call here keeps the executor's contract simple — every
    /// failure mode maps to "fall open to the first edge".
    /// </summary>
    /// <param name="node">The OR-split node carrying the condition expression.</param>
    /// <param name="task">The completed task (forwarded to the evaluator as context).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The evaluator's verdict; <c>null</c> on missing config or evaluator error.</returns>
    private async Task<WorkflowRulePackEvaluatorResult?> SafeEvaluateAsync(
        WorkflowGraphNode node, WorkflowTask task, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(node.ConditionExpression))
        {
            return null;
        }
        try
        {
            var context = new Dictionary<string, object>(System.StringComparer.Ordinal)
            {
                ["workflowTaskId"] = task.Id,
                ["nodeCode"] = node.NodeCode,
                ["expression"] = node.ConditionExpression,
            };
            return await _ruleEvaluator
                .EvaluateAsync(node.ConditionExpression, WorkflowRuleStages.Transition, context, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Exception ex)
        {
            _logger.LogError(
                ex,
                "Rule evaluator failed for OR-split node {NodeCode}; falling open to first edge.",
                node.NodeCode);
            return null;
        }
    }

    /// <summary>
    /// Materialises the next step in the workflow run. Dispatches on the target node's
    /// kind: Task / OrSplit / OrJoin / AndJoin → create the matching <see cref="WorkflowTask"/>
    /// row (for OrSplit / OrJoin we materialise an anchor task that completes immediately
    /// so the next call to the executor can continue from there); AndSplit → create the
    /// split-anchor + every sibling; End → mark the workflow run complete (no rows created).
    /// </summary>
    /// <param name="workflowId">Workflow-definition id the graph is pinned to.</param>
    /// <param name="target">Target node to materialise.</param>
    /// <param name="predecessor">The just-completed task (used for inheriting Dossier and timestamps).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of new <see cref="WorkflowTask"/> ids created by the advance.</returns>
    private async Task<IReadOnlyList<long>> AdvanceToNodeAsync(
        long workflowId, WorkflowGraphNode target, WorkflowTask predecessor, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        switch (target.Kind)
        {
            case WorkflowNodeKind.End:
                // Terminal — no row to create.
                return System.Array.Empty<long>();

            case WorkflowNodeKind.AndSplit:
                return await SpawnAndSplitAsync(workflowId, target, predecessor, now, ct).ConfigureAwait(false);

            case WorkflowNodeKind.AndJoin:
                return await AdvancePastAndJoinAsync(workflowId, target, predecessor, now, ct).ConfigureAwait(false);

            case WorkflowNodeKind.OrJoin:
            case WorkflowNodeKind.OrSplit:
            case WorkflowNodeKind.Task:
            case WorkflowNodeKind.Start:
            default:
                // Default: materialise a task anchored to the target node code. Start
                // is unreachable in production traversal (only outgoing edges exist) but
                // we treat it as a task for defence-in-depth.
                var taskId = await CreateTaskAsync(target, predecessor, parentSplitTaskId: null, now, ct)
                    .ConfigureAwait(false);
                return new[] { taskId };
        }
    }

    /// <summary>
    /// Implements the AND-split fan-out: creates one synthetic anchor task at the split
    /// (already <see cref="WorkflowTaskStatus.Completed"/> so the join can count it),
    /// then materialises one child task per outgoing edge with <see cref="WorkflowTask.ParentSplitTaskId"/>
    /// pointing at the anchor.
    /// </summary>
    /// <param name="workflowId">Workflow-definition id.</param>
    /// <param name="splitNode">The AND-split node.</param>
    /// <param name="predecessor">The just-completed predecessor task.</param>
    /// <param name="now">Stamp used for created/completed timestamps.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of child task ids that were created (anchor excluded).</returns>
    private async Task<IReadOnlyList<long>> SpawnAndSplitAsync(
        long workflowId,
        WorkflowGraphNode splitNode,
        WorkflowTask predecessor,
        System.DateTime now,
        CancellationToken ct)
    {
        // Anchor first — completed immediately so AndJoin's sibling-completion check
        // doesn't get tripped up by the anchor itself.
        var anchor = new WorkflowTask
        {
            DossierId = predecessor.DossierId,
            Title = $"[split] {splitNode.NodeCode}",
            NodeCode = splitNode.NodeCode,
            Status = WorkflowTaskStatus.Completed,
            CompletedAtUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.WorkflowTasks.Add(anchor);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var outgoing = await _db.WorkflowGraphEdges
            .Where(e => e.WorkflowDefinitionId == workflowId
                     && e.SourceNodeId == splitNode.Id
                     && e.IsActive)
            .OrderBy(e => e.OrderIndex)
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = new List<long>(outgoing.Count);
        foreach (var edge in outgoing)
        {
            var child = await _db.WorkflowGraphNodes
                .SingleOrDefaultAsync(n => n.Id == edge.TargetNodeId, ct)
                .ConfigureAwait(false);
            if (child is null) continue;
            var taskId = await CreateTaskAsync(child, predecessor, parentSplitTaskId: anchor.Id, now, ct)
                .ConfigureAwait(false);
            ids.Add(taskId);
        }
        return ids;
    }

    /// <summary>
    /// Implements the AND-join readiness check. Counts the completed siblings under the
    /// same <see cref="WorkflowTask.ParentSplitTaskId"/> as <paramref name="predecessor"/>;
    /// if every sibling is completed the executor follows the join's outgoing edges,
    /// otherwise the call is a no-op (returns an empty list).
    /// </summary>
    /// <param name="workflowId">Workflow-definition id.</param>
    /// <param name="joinNode">The AND-join node.</param>
    /// <param name="predecessor">Just-completed task whose split anchor identifies the sibling group.</param>
    /// <param name="now">Stamp used when downstream nodes are created.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ids of downstream tasks (empty when the join is not yet ready).</returns>
    private async Task<IReadOnlyList<long>> AdvancePastAndJoinAsync(
        long workflowId,
        WorkflowGraphNode joinNode,
        WorkflowTask predecessor,
        System.DateTime now,
        CancellationToken ct)
    {
        if (predecessor.ParentSplitTaskId is null)
        {
            // No anchor ⇒ we cannot tell sibling group; conservatively skip the advance.
            return System.Array.Empty<long>();
        }

        var siblings = await _db.WorkflowTasks
            .Where(t => t.ParentSplitTaskId == predecessor.ParentSplitTaskId.Value && t.Id != predecessor.Id)
            .Select(t => new { t.Id, t.Status })
            .ToListAsync(ct).ConfigureAwait(false);

        if (siblings.Any(s => s.Status != WorkflowTaskStatus.Completed))
        {
            // Still waiting for at least one sibling.
            return System.Array.Empty<long>();
        }

        var outgoing = await _db.WorkflowGraphEdges
            .Where(e => e.WorkflowDefinitionId == workflowId
                     && e.SourceNodeId == joinNode.Id
                     && e.IsActive)
            .OrderBy(e => e.OrderIndex)
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = new List<long>();
        foreach (var edge in outgoing)
        {
            var target = await _db.WorkflowGraphNodes
                .SingleOrDefaultAsync(n => n.Id == edge.TargetNodeId, ct)
                .ConfigureAwait(false);
            if (target is null) continue;
            // Once past the join the children are no longer part of the parallel group,
            // so we clear parentSplitTaskId on the new row.
            var newId = await CreateTaskAsync(target, predecessor, parentSplitTaskId: null, now, ct)
                .ConfigureAwait(false);
            ids.Add(newId);
        }
        return ids;
    }

    /// <summary>
    /// Inserts a new <see cref="WorkflowTask"/> row anchored to <paramref name="node"/>
    /// inheriting Dossier from the predecessor. The new row is Pending; when the node
    /// kind is OR-split / OR-join the title is prefixed with the kind so audit dashboards
    /// can distinguish anchors from real human tasks at a glance.
    /// </summary>
    /// <param name="node">Target node.</param>
    /// <param name="predecessor">Predecessor task (Dossier inheritance only).</param>
    /// <param name="parentSplitTaskId">Anchor id for AND-split children; null otherwise.</param>
    /// <param name="now">Created stamp.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The surrogate id of the new task.</returns>
    private async Task<long> CreateTaskAsync(
        WorkflowGraphNode node,
        WorkflowTask predecessor,
        long? parentSplitTaskId,
        System.DateTime now,
        CancellationToken ct)
    {
        var titlePrefix = node.Kind switch
        {
            WorkflowNodeKind.OrSplit => "[or-split] ",
            WorkflowNodeKind.OrJoin => "[or-join] ",
            WorkflowNodeKind.AndJoin => "[and-join] ",
            _ => string.Empty,
        };
        var task = new WorkflowTask
        {
            DossierId = predecessor.DossierId,
            Title = titlePrefix + node.NodeCode,
            NodeCode = node.NodeCode,
            Status = WorkflowTaskStatus.Pending,
            GroupCode = node.AssigneeRole,
            UnclaimedSinceUtc = node.AssigneeRole is null ? null : now,
            ParentSplitTaskId = parentSplitTaskId,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.WorkflowTasks.Add(task);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return task.Id;
    }
}
