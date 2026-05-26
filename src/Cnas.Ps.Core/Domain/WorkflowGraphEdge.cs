namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0123 / TOR CF 16.05 — directed edge in the persisted execution graph of a
/// <see cref="WorkflowDefinition"/>. Connects two <see cref="WorkflowGraphNode"/> rows
/// owned by the SAME workflow definition (R0129 version-pinned — see the node's XML
/// doc); together node + edge rows form the substrate consumed by the
/// <c>WorkflowGraphExecutor</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Label semantics.</b> <see cref="Label"/> is the contract between the rule engine
/// (R0124) and the executor at <see cref="WorkflowNodeKind.OrSplit"/> nodes: the engine
/// returns a string label and the executor follows the edge whose <see cref="Label"/>
/// matches. On sequential and AND-split traversals the label is informational only
/// (used by audit / UI rendering); on OR-splits a missing/unmatched label causes the
/// executor to fall back to the first outgoing edge (fail-open per R0123 spec).
/// </para>
/// <para>
/// <b>OrderIndex as tie-breaker.</b> When multiple outgoing edges share a source node
/// (AND-split fan-out, OR-split branches, audit-time enumeration) the executor + admin
/// UI iterate them in ascending <see cref="OrderIndex"/>. For OR-splits this also
/// defines the fail-open fallback: the edge with the lowest <see cref="OrderIndex"/>
/// wins when the rule engine returns no decision.
/// </para>
/// <para>
/// <b>Internal id only.</b> Unlike <see cref="WorkflowGraphNode"/>, edges do NOT
/// implement <see cref="IExternalId"/>. Edges are referenced indirectly via their
/// source/target node codes in DTOs, never by surrogate id, so there is no external-
/// id contract to satisfy.
/// </para>
/// </remarks>
public sealed class WorkflowGraphEdge : AuditableEntity
{
    /// <summary>
    /// FK to the parent <see cref="WorkflowDefinition"/> row. Denormalised onto the edge
    /// so the executor's "give me every edge whose source is node X within workflow W"
    /// query is a single sargable index lookup on
    /// <c>(WorkflowDefinitionId, SourceNodeId)</c>.
    /// </summary>
    public long WorkflowDefinitionId { get; set; }

    /// <summary>
    /// FK to the source <see cref="WorkflowGraphNode"/>. The executor reads outgoing
    /// edges for the just-completed task by filtering on this column. The validator
    /// rejects edges whose source does not exist in the same workflow definition.
    /// </summary>
    public long SourceNodeId { get; set; }

    /// <summary>
    /// FK to the target <see cref="WorkflowGraphNode"/>. The executor materialises a new
    /// <see cref="WorkflowTask"/> for this node (or terminates the run when the target
    /// kind is <see cref="WorkflowNodeKind.End"/>). Validator rejects orphan targets.
    /// </summary>
    public long TargetNodeId { get; set; }

    /// <summary>
    /// Branch label — populated on <see cref="WorkflowNodeKind.OrSplit"/> outgoing edges
    /// (matched against the rule engine's verdict) and optionally on every other edge
    /// for audit / UI purposes. <c>null</c> when the edge is unconditional. See the
    /// class-level remarks for the OR-split matching contract.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Ordering tie-breaker used when multiple edges share a source node. Lower values
    /// are traversed first; on OR-splits the lowest-order edge is also the fail-open
    /// fallback when the rule engine returns no decision. See the class-level remarks
    /// for the full traversal contract.
    /// </summary>
    public int OrderIndex { get; set; }
}
