using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Workflow;

/// <summary>
/// R0123 / TOR CF 16.05 — admin surface for the persisted workflow execution graph
/// (nodes + edges). The service is the only writer of <c>WorkflowGraphNodes</c> /
/// <c>WorkflowGraphEdges</c>; every replace produces a NEW <c>WorkflowDefinition</c>
/// version (R0129) so in-flight workflow runs keep following the version they were
/// pinned to at submission time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replace, not update.</b> The graph mutation surface is destructive on purpose:
/// edges and nodes are referenced by code, and a partial update would require
/// dependency-aware ordering across hundreds of rows. <see cref="ReplaceGraphAsync"/>
/// writes the whole new graph atomically as part of the same transaction that mints
/// the new version row.
/// </para>
/// <para>
/// <b>Audit obligation.</b> Every successful replace emits a Critical
/// <c>WORKFLOW.GRAPH.REPLACED</c> audit row capturing <c>{ workflowSqid, fromVersion,
/// toVersion, nodeCount, edgeCount }</c> so investigators can replay graph history.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> Workflow ids round-trip as Sqid strings per CLAUDE.md RULE 3.
/// Node codes and edge labels round-trip verbatim as their natural strings (they ARE
/// the public identifiers).
/// </para>
/// </remarks>
public interface IWorkflowGraphService
{
    /// <summary>
    /// Replaces the persisted execution graph for the workflow definition identified by
    /// <paramref name="workflowSqid"/>. The replace is destructive: every existing
    /// node and edge row tied to the current version is superseded as part of the same
    /// transaction that mints a fresh <c>WorkflowDefinition</c> version (R0129). The
    /// returned DTO carries the NEW version number + Sqid.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded id of the workflow definition (any version row of the chain).</param>
    /// <param name="graph">Replacement graph payload. Must pass validation (single Start, ≥ 1 End, no cycles, unique codes, valid edge references).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the persisted graph projection pinned to the new version. On failure
    /// a <see cref="Result{T}"/> carrying one of:
    /// <list type="bullet">
    ///   <item><see cref="ErrorCodes.InvalidSqid"/> — <paramref name="workflowSqid"/> failed to decode.</item>
    ///   <item><see cref="ErrorCodes.NotFound"/> — no workflow definition matches the supplied Sqid.</item>
    ///   <item><see cref="ErrorCodes.ValidationFailed"/> — the graph violated a structural rule (cycle, orphan edge, etc.).</item>
    /// </list>
    /// </returns>
    Task<Result<WorkflowGraphDto>> ReplaceGraphAsync(
        string workflowSqid,
        WorkflowGraphInputDto graph,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the persisted execution graph for the workflow definition identified by
    /// <paramref name="workflowSqid"/>. The lookup resolves the workflow definition via
    /// surrogate id (not <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition.Code"/>) so
    /// historical versions can be inspected — when the caller hands in the Sqid of a
    /// superseded row the response returns THAT version's graph, not the current one.
    /// </summary>
    /// <param name="workflowSqid">Sqid-encoded id of the workflow definition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted graph projection; <see cref="ErrorCodes.NotFound"/> when no rows match.</returns>
    Task<Result<WorkflowGraphDto>> GetForVersionAsync(
        string workflowSqid,
        CancellationToken ct = default);
}
