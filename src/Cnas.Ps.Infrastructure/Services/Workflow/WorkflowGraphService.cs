using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Workflow;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Workflow;

/// <summary>
/// R0123 / TOR CF 16.05 — default <see cref="IWorkflowGraphService"/> implementation.
/// Persists the workflow execution graph (nodes + edges) atomically against a NEW
/// <see cref="WorkflowDefinition"/> version (R0129) so in-flight workflow runs keep
/// following the version they were pinned to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Version-mint contract.</b> Each successful <see cref="ReplaceGraphAsync"/> mints
/// a new <see cref="WorkflowDefinition"/> row by copying the current row's columns
/// (Code, DefinitionJson, AllowedRoles, etc.), incrementing the <c>Version</c>, and
/// flipping the predecessor's <c>IsCurrent</c> to <c>false</c>. The new row's surrogate
/// id becomes the parent of the just-written nodes/edges. The doubly-linked chain
/// (<see cref="WorkflowDefinition.SupersedesDefinitionId"/> /
/// <see cref="WorkflowDefinition.SupersededByDefinitionId"/>) is stamped per
/// <see cref="WorkflowConfigurationService.SaveDefinitionAsync"/> conventions.
/// </para>
/// <para>
/// <b>Why not delegate to <see cref="IWorkflowConfigurationService"/>.</b> The
/// existing service requires a <c>DefinitionJson</c> payload — this service deals in
/// the structured graph and would have to serialise it just to satisfy the column
/// contract. Performing the version-mint directly here keeps the graph store and the
/// legacy JSON column independent (legacy JSON can stay null-friendly going forward).
/// </para>
/// <para>
/// <b>Audit.</b> Every replace emits Critical <c>WORKFLOW.GRAPH.REPLACED</c> carrying
/// <c>{ workflowSqid, fromVersion, toVersion, nodeCount, edgeCount }</c>.
/// </para>
/// </remarks>
public sealed class WorkflowGraphService : IWorkflowGraphService
{
    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly ISqidService _sqids;
    private readonly IAuditService _audit;
    private readonly IValidator<WorkflowGraphInputDto> _validator;

    /// <summary>Stable audit event code emitted on every graph replace.</summary>
    public const string GraphReplacedEvent = "WORKFLOW.GRAPH.REPLACED";

    /// <summary>Constructs the service with its DI dependencies.</summary>
    /// <param name="db">EF Core context abstraction.</param>
    /// <param name="clock">UTC clock — never <see cref="System.DateTime.UtcNow"/> directly.</param>
    /// <param name="caller">Authenticated caller — supplies the actor id for audit rows.</param>
    /// <param name="sqids">Sqid encoder/decoder for external id round-tripping (CLAUDE.md RULE 3).</param>
    /// <param name="audit">Audit journal façade — receives a Critical row per graph replace.</param>
    /// <param name="validator">Structural validator for the input graph.</param>
    public WorkflowGraphService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ICallerContext caller,
        ISqidService sqids,
        IAuditService audit,
        IValidator<WorkflowGraphInputDto> validator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(validator);
        _db = db;
        _clock = clock;
        _caller = caller;
        _sqids = sqids;
        _audit = audit;
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<Result<WorkflowGraphDto>> ReplaceGraphAsync(
        string workflowSqid,
        WorkflowGraphInputDto graph,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var decoded = _sqids.TryDecode(workflowSqid);
        if (decoded.IsFailure)
        {
            return Result<WorkflowGraphDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        // Validate the shape of the incoming graph BEFORE touching the DB so a malformed
        // payload never leaves transactional artifacts behind.
        var validation = await _validator.ValidateAsync(graph, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<WorkflowGraphDto>.Failure(
                ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        // Locate the supplied workflow definition row. The caller can hand us ANY version
        // row of the chain; we anchor the mint on the row's Code and find the CURRENT row.
        var anchor = await _db.WorkflowDefinitions
            .SingleOrDefaultAsync(w => w.Id == decoded.Value, ct)
            .ConfigureAwait(false);
        if (anchor is null)
        {
            return Result<WorkflowGraphDto>.Failure(
                ErrorCodes.NotFound, $"Workflow definition '{workflowSqid}' not found.");
        }

        var canonical = anchor.Code;
        var previousCurrent = await _db.WorkflowDefinitions
            .Where(w => w.Code == canonical && w.IsCurrent)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var nextVersion = await _db.WorkflowDefinitions
            .Where(w => w.Code == canonical)
            .Select(w => (int?)w.Version)
            .MaxAsync(ct)
            .ConfigureAwait(false) + 1 ?? 1;

        var now = _clock.UtcNow;
        if (previousCurrent is not null)
        {
            previousCurrent.IsCurrent = false;
            previousCurrent.SupersededAtUtc = now;
            previousCurrent.UpdatedAtUtc = now;
            previousCurrent.UpdatedBy = _caller.UserSqid;
        }

        // Mint a fresh WorkflowDefinition row carrying the same columns as the predecessor
        // (so ACL / rule pack / JSON settings are not silently lost) but with a bumped
        // version. The graph rows we write below are bound to THIS new id.
        var newDefinition = new WorkflowDefinition
        {
            Code = canonical,
            Version = nextVersion,
            DefinitionJson = previousCurrent?.DefinitionJson ?? anchor.DefinitionJson,
            IsCurrent = true,
            IsActive = true,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            SupersedesDefinitionId = previousCurrent?.Id,
            AllowedRoles = previousCurrent?.AllowedRoles is { Count: > 0 } pr
                ? new List<string>(pr)
                : new List<string>(),
            AllowedGroups = previousCurrent?.AllowedGroups is { Count: > 0 } pg
                ? new List<string>(pg)
                : new List<string>(),
            StartRulePackCode = previousCurrent?.StartRulePackCode,
            TransitionRulePackCode = previousCurrent?.TransitionRulePackCode,
            CompletionRulePackCode = previousCurrent?.CompletionRulePackCode,
        };
        _db.WorkflowDefinitions.Add(newDefinition);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (previousCurrent is not null)
        {
            previousCurrent.SupersededByDefinitionId = newDefinition.Id;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Materialise the nodes first so we have surrogate ids before adding edges.
        var nodes = new List<WorkflowGraphNode>(graph.Nodes.Count);
        foreach (var dto in graph.Nodes)
        {
            var kind = System.Enum.Parse<WorkflowNodeKind>(dto.Kind);
            var node = new WorkflowGraphNode
            {
                WorkflowDefinitionId = newDefinition.Id,
                NodeCode = dto.NodeCode,
                Kind = kind,
                AssigneeRole = dto.AssigneeRole,
                ConditionExpression = dto.ConditionExpression,
                OrderIndex = dto.OrderIndex,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.WorkflowGraphNodes.Add(node);
            nodes.Add(node);
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var idByCode = nodes.ToDictionary(n => n.NodeCode, n => n.Id, System.StringComparer.Ordinal);
        foreach (var dto in graph.Edges)
        {
            var edge = new WorkflowGraphEdge
            {
                WorkflowDefinitionId = newDefinition.Id,
                SourceNodeId = idByCode[dto.SourceNodeCode],
                TargetNodeId = idByCode[dto.TargetNodeCode],
                Label = dto.Label,
                OrderIndex = dto.OrderIndex,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.WorkflowGraphEdges.Add(edge);
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Critical audit row capturing the replace shape.
        var fromVersion = previousCurrent?.Version ?? 0;
        var details = JsonSerializer.Serialize(new
        {
            workflowSqid = _sqids.Encode(newDefinition.Id),
            code = canonical,
            fromVersion,
            toVersion = nextVersion,
            nodeCount = graph.Nodes.Count,
            edgeCount = graph.Edges.Count,
        });
        await _audit.RecordAsync(
            eventCode: GraphReplacedEvent,
            severity: AuditSeverity.Critical,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(WorkflowDefinition),
            targetEntityId: newDefinition.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);

        return Result<WorkflowGraphDto>.Success(ProjectGraph(newDefinition, nodes, GetEdges(newDefinition.Id)));
    }

    /// <inheritdoc />
    public async Task<Result<WorkflowGraphDto>> GetForVersionAsync(
        string workflowSqid,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(workflowSqid);
        if (decoded.IsFailure)
        {
            return Result<WorkflowGraphDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var def = await _db.WorkflowDefinitions
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == decoded.Value, ct)
            .ConfigureAwait(false);
        if (def is null)
        {
            return Result<WorkflowGraphDto>.Failure(
                ErrorCodes.NotFound, $"Workflow definition '{workflowSqid}' not found.");
        }

        var nodeRows = await _db.WorkflowGraphNodes
            .AsNoTracking()
            .Where(n => n.WorkflowDefinitionId == def.Id && n.IsActive)
            .OrderBy(n => n.OrderIndex)
            .ThenBy(n => n.NodeCode)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var edgeRows = await GetEdgesAsync(def.Id, ct).ConfigureAwait(false);

        return Result<WorkflowGraphDto>.Success(ProjectGraph(def, nodeRows, edgeRows));
    }

    /// <summary>
    /// Returns the edges bound to <paramref name="definitionId"/> ordered by source
    /// node id then order index. Centralised so the read and write paths emit the
    /// same projection ordering.
    /// </summary>
    /// <param name="definitionId">Workflow-definition row id.</param>
    /// <returns>The materialised, ordered edge list.</returns>
    private List<WorkflowGraphEdge> GetEdges(long definitionId) =>
        _db.WorkflowGraphEdges
            .Where(e => e.WorkflowDefinitionId == definitionId && e.IsActive)
            .OrderBy(e => e.SourceNodeId)
            .ThenBy(e => e.OrderIndex)
            .ToList();

    /// <summary>
    /// Async sibling of <see cref="GetEdges"/> used on the read-only path so the
    /// cancellation token is honoured.
    /// </summary>
    /// <param name="definitionId">Workflow-definition row id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The materialised, ordered edge list.</returns>
    private async Task<List<WorkflowGraphEdge>> GetEdgesAsync(long definitionId, CancellationToken ct) =>
        await _db.WorkflowGraphEdges
            .AsNoTracking()
            .Where(e => e.WorkflowDefinitionId == definitionId && e.IsActive)
            .OrderBy(e => e.SourceNodeId)
            .ThenBy(e => e.OrderIndex)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Projects a workflow definition + its loaded node / edge rows into the output DTO
    /// the API exposes. Centralised so every read and write path emits the same shape.
    /// </summary>
    /// <param name="def">Workflow definition the graph belongs to.</param>
    /// <param name="nodes">Loaded node rows.</param>
    /// <param name="edges">Loaded edge rows.</param>
    /// <returns>The DTO returned by the API.</returns>
    private WorkflowGraphDto ProjectGraph(
        WorkflowDefinition def,
        IReadOnlyList<WorkflowGraphNode> nodes,
        IReadOnlyList<WorkflowGraphEdge> edges)
    {
        var idToCode = nodes.ToDictionary(n => n.Id, n => n.NodeCode);
        var nodeDtos = nodes
            .OrderBy(n => n.OrderIndex)
            .ThenBy(n => n.NodeCode, System.StringComparer.Ordinal)
            .Select(n => new WorkflowGraphNodeDto(
                NodeCode: n.NodeCode,
                Kind: n.Kind.ToString(),
                AssigneeRole: n.AssigneeRole,
                ConditionExpression: n.ConditionExpression,
                OrderIndex: n.OrderIndex))
            .ToList();
        var edgeDtos = edges
            .Select(e => new WorkflowGraphEdgeDto(
                SourceNodeCode: idToCode.TryGetValue(e.SourceNodeId, out var src) ? src : "?",
                TargetNodeCode: idToCode.TryGetValue(e.TargetNodeId, out var tgt) ? tgt : "?",
                Label: e.Label,
                OrderIndex: e.OrderIndex))
            .ToList();
        return new WorkflowGraphDto(
            WorkflowDefinitionSqid: _sqids.Encode(def.Id),
            Version: def.Version,
            Nodes: nodeDtos,
            Edges: edgeDtos);
    }
}
