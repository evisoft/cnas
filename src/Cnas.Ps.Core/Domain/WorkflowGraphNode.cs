namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0123 / TOR CF 16.05 — single node in the persisted execution graph of a
/// <see cref="WorkflowDefinition"/>. Together with <see cref="WorkflowGraphEdge"/> the
/// row set forms the deterministic substrate consumed by the <c>WorkflowGraphExecutor</c>
/// until the real Camunda/Operaton engine adapter lands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pinning to a workflow version.</b> Each node is owned by a SPECIFIC
/// <see cref="WorkflowDefinition"/> row (R0129 versioning): when the graph is replaced
/// via <c>IWorkflowGraphService.ReplaceGraphAsync</c> the service mints a NEW
/// <see cref="WorkflowDefinition"/> version and provisions fresh node rows under that
/// id. In-flight workflow runs continue to follow the OLDER pinned graph because their
/// <see cref="WorkflowTask"/> rows reference workflow tasks already created against the
/// older nodes. This intentional non-cascade keeps the immutable-snapshot invariant
/// (CLAUDE.md cross-cutting "Immutable Snapshots") consistent across graph edits.
/// </para>
/// <para>
/// <b>Natural key.</b> UNIQUE on (<see cref="WorkflowDefinitionId"/>,
/// <see cref="NodeCode"/>) — the EF configuration declares the index. <see cref="NodeCode"/>
/// is the stable identifier used by the executor + by <see cref="WorkflowTask.NodeCode"/>
/// to anchor a running task to its graph node.
/// </para>
/// <para>
/// <b>External-id contract.</b> Implements <see cref="IExternalId"/> because the admin
/// graph endpoint exposes individual node ids in audit-detail payloads and (future)
/// per-node inspection endpoints. The surrogate id is Sqid-encoded per CLAUDE.md
/// RULE 3 at the API boundary; internally the executor uses the raw <see cref="long"/>.
/// </para>
/// </remarks>
public sealed class WorkflowGraphNode : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the <see cref="WorkflowDefinition"/> row that owns this node. The link is
    /// to a SPECIFIC version (not the canonical code) so a graph replace cleanly forks
    /// the running graph from the historical one.
    /// </summary>
    public long WorkflowDefinitionId { get; set; }

    /// <summary>
    /// Stable identifier of this node WITHIN the parent graph (e.g. <c>"intake"</c>,
    /// <c>"approve-decision"</c>). Used by the executor to anchor a running
    /// <see cref="WorkflowTask"/> to its node and by <see cref="WorkflowGraphEdge"/>
    /// lookups. Validator-enforced regex <c>^[a-z][a-z0-9-]{1,63}$</c>; UNIQUE per
    /// workflow.
    /// </summary>
    public required string NodeCode { get; set; }

    /// <summary>
    /// Execution semantics of this node — sequential <see cref="WorkflowNodeKind.Task"/>,
    /// parallel <see cref="WorkflowNodeKind.AndSplit"/> / <see cref="WorkflowNodeKind.AndJoin"/>,
    /// exclusive <see cref="WorkflowNodeKind.OrSplit"/> / <see cref="WorkflowNodeKind.OrJoin"/>,
    /// or the terminal markers <see cref="WorkflowNodeKind.Start"/> /
    /// <see cref="WorkflowNodeKind.End"/>. See the enum's XML doc for the per-kind contract.
    /// </summary>
    public WorkflowNodeKind Kind { get; set; }

    /// <summary>
    /// Role code assigned to the <see cref="WorkflowTask"/> the executor materialises when
    /// it reaches a <see cref="WorkflowNodeKind.Task"/> node. <c>null</c> on every non-task
    /// node and acceptable on a task node when ownership is later resolved by other rules
    /// (R0126 ACL, R0127 absence delegation).
    /// </summary>
    public string? AssigneeRole { get; set; }

    /// <summary>
    /// Stable expression string evaluated by the rule engine (R0124) when the executor
    /// reaches an <see cref="WorkflowNodeKind.OrSplit"/> node. The engine returns the
    /// matching edge label; the executor follows the outgoing edge whose
    /// <see cref="WorkflowGraphEdge.Label"/> equals that label. <c>null</c> on every
    /// non-OrSplit kind (validator-enforced). On OrSplits where the engine returns no
    /// decision the executor falls back to the first outgoing edge (fail-open).
    /// </summary>
    public string? ConditionExpression { get; set; }

    /// <summary>
    /// Display ordering for BPMN-style modelling and admin UI rendering. Lower values
    /// appear first when the admin endpoint lists nodes for a graph. Has no effect on
    /// the executor's traversal (which is driven by edges, not order).
    /// </summary>
    public int OrderIndex { get; set; }
}
