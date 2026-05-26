namespace Cnas.Ps.Web.Models;

/// <summary>
/// R0121 / CF 16.02 — pure, allocation-light model carrying the parsed
/// node-and-edge projection of a workflow definition body. The model is the
/// hand-off contract between <see cref="WorkflowGraphParser"/> (input layer)
/// and <c>WorkflowDefinitionSvgRenderer</c> (presentation layer). It is
/// deliberately immutable so consumers can cache it across renders without
/// worrying about mutation cascades.
/// </summary>
/// <param name="Nodes">All nodes in the parsed graph, preserving source order.</param>
/// <param name="Edges">All edges in the parsed graph, preserving source order.</param>
public sealed record WorkflowGraphModel(
    IReadOnlyList<WorkflowNodeModel> Nodes,
    IReadOnlyList<WorkflowEdgeModel> Edges);

/// <summary>
/// R0121 / CF 16.02 — single node projection used by the visual designer.
/// </summary>
/// <param name="Code">
/// Stable code that the workflow engine uses to address this node (e.g.
/// <c>"START"</c>, <c>"REVIEW_LOCAL"</c>). The code is the natural identifier
/// the renderer prints as the node's caption.
/// </param>
/// <param name="Kind">
/// String enumeration of the node's kind. Recognised values: <c>Start</c>,
/// <c>End</c>, <c>UserTask</c>, <c>ServiceTask</c>, <c>AndSplit</c>, <c>AndJoin</c>,
/// <c>OrSplit</c>, <c>OrJoin</c>. Unknown values are accepted by the parser but
/// rendered as a generic rectangle.
/// </param>
/// <param name="Label">
/// Optional human-readable label that the renderer prefers over <see cref="Code"/>
/// when present and non-empty. Falls back to <see cref="Code"/> if null/blank.
/// </param>
public sealed record WorkflowNodeModel(
    string Code,
    string Kind,
    string? Label);

/// <summary>
/// R0121 / CF 16.02 — directed edge projection. Source and target use node
/// codes (the natural-key handle) rather than indices so the parser can resolve
/// without a numbered second pass.
/// </summary>
/// <param name="From">Source node code (must match a <see cref="WorkflowNodeModel.Code"/> in the same graph).</param>
/// <param name="To">Target node code (must match a <see cref="WorkflowNodeModel.Code"/> in the same graph).</param>
/// <param name="Label">Optional branch label rendered as the mid-line caption; null for unconditional edges.</param>
public sealed record WorkflowEdgeModel(
    string From,
    string To,
    string? Label);
