namespace Cnas.Ps.Contracts;

/// <summary>
/// R0123 / TOR CF 16.05 — projection of a single <c>WorkflowGraphNode</c> for the admin
/// graph endpoints. The DTO is intentionally serialisation-friendly: the kind is a
/// stable string (not the int enum) so a future enum-value addition does not break
/// existing JSON consumers, and the node code is the natural-key handle the rest of
/// the graph DTO references.
/// </summary>
/// <param name="NodeCode">Stable node identifier within the graph (e.g. <c>"intake"</c>).</param>
/// <param name="Kind">
/// Stable string form of the node's kind (<c>"Start"</c> / <c>"Task"</c> / <c>"AndSplit"</c>
/// / <c>"AndJoin"</c> / <c>"OrSplit"</c> / <c>"OrJoin"</c> / <c>"End"</c>). Mirrors the
/// names of the <c>WorkflowNodeKind</c> enum so the round-trip parses by name.
/// </param>
/// <param name="AssigneeRole">Role code assigned to the task when this node materialises a <c>WorkflowTask</c>; null for non-task nodes.</param>
/// <param name="ConditionExpression">Rule-engine expression evaluated on OR-split nodes; null elsewhere.</param>
/// <param name="OrderIndex">Display ordering for admin UI / BPMN rendering.</param>
public sealed record WorkflowGraphNodeDto(
    string NodeCode,
    string Kind,
    string? AssigneeRole,
    string? ConditionExpression,
    int OrderIndex);

/// <summary>
/// R0123 / TOR CF 16.05 — projection of a single <c>WorkflowGraphEdge</c> for the admin
/// graph endpoints. Edge source/target are emitted as NODE CODES (not surrogate ids)
/// because the natural key is the round-trip handle.
/// </summary>
/// <param name="SourceNodeCode">Stable code of the source node within the graph.</param>
/// <param name="TargetNodeCode">Stable code of the target node within the graph.</param>
/// <param name="Label">Branch label (matched by the rule engine on OR-splits); null when the edge is unconditional.</param>
/// <param name="OrderIndex">Tie-breaker when multiple edges share a source node; lowest wins on OR-split fail-open fallback.</param>
public sealed record WorkflowGraphEdgeDto(
    string SourceNodeCode,
    string TargetNodeCode,
    string? Label,
    int OrderIndex);

/// <summary>
/// R0123 / TOR CF 16.05 — full output projection returned by
/// <c>GET /api/workflow-definitions/{workflowSqid}/graph</c> and by the success body of
/// the <c>PUT</c> endpoint. Carries the round-trip handle (<see cref="WorkflowDefinitionSqid"/>
/// + <see cref="Version"/>) so a client can verify it received the version it expected
/// after a replace.
/// </summary>
/// <param name="WorkflowDefinitionSqid">Sqid-encoded id of the workflow-definition version owning the graph.</param>
/// <param name="Version">Numeric workflow-definition version (R0129) that hosts this graph.</param>
/// <param name="Nodes">All nodes in the graph, ordered by <see cref="WorkflowGraphNodeDto.OrderIndex"/>.</param>
/// <param name="Edges">All edges in the graph, ordered by source node code then <see cref="WorkflowGraphEdgeDto.OrderIndex"/>.</param>
public sealed record WorkflowGraphDto(
    string WorkflowDefinitionSqid,
    int Version,
    IReadOnlyList<WorkflowGraphNodeDto> Nodes,
    IReadOnlyList<WorkflowGraphEdgeDto> Edges);

/// <summary>
/// R0123 / TOR CF 16.05 — request body for
/// <c>PUT /api/workflow-definitions/{workflowSqid}/graph</c>. The body is the whole new
/// graph; the service writes it atomically as a NEW workflow-definition version (R0129)
/// so in-flight runs keep their pinned graph. Mass-assignment protection (CLAUDE.md §2.4)
/// is enforced by the absence of any id / audit / system fields on this input.
/// </summary>
/// <param name="Nodes">Replacement node list. At least one Start node and one End node required.</param>
/// <param name="Edges">Replacement edge list. Each entry references existing source/target node codes.</param>
public sealed record WorkflowGraphInputDto(
    IReadOnlyList<WorkflowGraphNodeDto> Nodes,
    IReadOnlyList<WorkflowGraphEdgeDto> Edges);
