using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0123 / TOR CF 16.05 — validator for <see cref="WorkflowGraphInputDto"/>. Enforces
/// the structural rules that make a graph executable: exactly one Start, at least one
/// End, unique node codes matching the canonical regex, every edge referencing an
/// existing node, no cycles, and the AND-split / OR-split fan-out invariants.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why server-side.</b> The admin UI (R0121, future) will surface the same rules
/// inside the BPMN modeler, but the service is the source of truth: an unvalidated
/// graph would let the executor wander into an unreachable End node or loop forever
/// on a back-edge.
/// </para>
/// <para>
/// <b>Cycle detection.</b> A depth-first traversal from every node detects back-edges;
/// the implementation uses an iterative stack to keep the stack depth bounded under
/// arbitrarily large graphs. Detected cycles surface as a single validation failure
/// (<c>WORKFLOW_GRAPH_CYCLE</c>) rather than a per-edge message.
/// </para>
/// <para>
/// <b>NodeCode regex.</b> <see cref="NodeCodePattern"/> enforces the kebab-case
/// convention used across the codebase (BPMN activity ids, step codes). Adminstrators
/// who paste an upper-case or whitespace-bearing identifier see a deterministic
/// rejection at validation time instead of a downstream "unknown node code" runtime
/// failure.
/// </para>
/// </remarks>
public sealed class WorkflowGraphInputDtoValidator : AbstractValidator<WorkflowGraphInputDto>
{
    /// <summary>Stable kebab-case regex for node codes (1..64 chars, lower-case-led).</summary>
    public const string NodeCodePattern = "^[a-z][a-z0-9-]{1,63}$";

    /// <summary>Stable error key for the cycle-detection failure.</summary>
    public const string CycleErrorKey = "WORKFLOW_GRAPH_CYCLE";

    /// <summary>Creates the validator with the static rule set.</summary>
    public WorkflowGraphInputDtoValidator()
    {
        // --- node list shape -------------------------------------------------------
        RuleFor(g => g.Nodes)
            .NotNull().WithMessage("Nodes is required.")
            .Must(n => n is { Count: > 0 }).WithMessage("Nodes cannot be empty.");

        // Validate each node's code shape.
        RuleForEach(g => g.Nodes).ChildRules(child =>
        {
            child.RuleFor(n => n.NodeCode)
                .NotEmpty().WithMessage("NodeCode is required.")
                .Matches(NodeCodePattern).WithMessage("NodeCode must match " + NodeCodePattern + ".");
            child.RuleFor(n => n.Kind)
                .NotEmpty().WithMessage("Kind is required.")
                .Must(BeKnownKind).WithMessage("Kind must be one of: Start, Task, AndSplit, AndJoin, OrSplit, OrJoin, End.");
        });

        // --- graph-level invariants ------------------------------------------------
        RuleFor(g => g).Custom((g, ctx) =>
        {
            if (g?.Nodes is null || g.Edges is null)
            {
                return;
            }

            // Codes must be unique within the graph.
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var n in g.Nodes)
            {
                if (string.IsNullOrEmpty(n.NodeCode)) continue;
                if (!seen.Add(n.NodeCode))
                {
                    ctx.AddFailure(nameof(WorkflowGraphInputDto.Nodes),
                        $"Duplicate NodeCode '{n.NodeCode}'.");
                }
            }

            // Exactly one Start node.
            var startCount = g.Nodes.Count(n => string.Equals(n.Kind, nameof(WorkflowNodeKind.Start), System.StringComparison.Ordinal));
            if (startCount != 1)
            {
                ctx.AddFailure(nameof(WorkflowGraphInputDto.Nodes),
                    $"Graph must contain exactly one Start node (found {startCount}).");
            }

            // At least one End node.
            var endCount = g.Nodes.Count(n => string.Equals(n.Kind, nameof(WorkflowNodeKind.End), System.StringComparison.Ordinal));
            if (endCount < 1)
            {
                ctx.AddFailure(nameof(WorkflowGraphInputDto.Nodes),
                    "Graph must contain at least one End node.");
            }

            // Resolve codes to nodes for edge / fan-out / cycle checks. Duplicates were
            // already reported above — here we silently keep the first occurrence so the
            // remaining structural checks still produce meaningful failures instead of
            // crashing the validator.
            var byCode = new Dictionary<string, WorkflowGraphNodeDto>(System.StringComparer.Ordinal);
            foreach (var n in g.Nodes)
            {
                if (string.IsNullOrEmpty(n.NodeCode)) continue;
                if (!byCode.ContainsKey(n.NodeCode))
                {
                    byCode[n.NodeCode] = n;
                }
            }

            // Edges must reference existing nodes on both ends.
            foreach (var e in g.Edges)
            {
                if (e is null) continue;
                if (string.IsNullOrEmpty(e.SourceNodeCode) || !byCode.ContainsKey(e.SourceNodeCode))
                {
                    ctx.AddFailure(nameof(WorkflowGraphInputDto.Edges),
                        $"Edge source '{e.SourceNodeCode}' is not a known node.");
                }
                if (string.IsNullOrEmpty(e.TargetNodeCode) || !byCode.ContainsKey(e.TargetNodeCode))
                {
                    ctx.AddFailure(nameof(WorkflowGraphInputDto.Edges),
                        $"Edge target '{e.TargetNodeCode}' is not a known node.");
                }
            }

            // AndSplit must have ≥ 2 outgoing edges. AndJoin must have ≥ 2 incoming edges.
            // OrSplit must have ≥ 2 outgoing edges AND non-empty ConditionExpression.
            foreach (var n in g.Nodes)
            {
                if (string.IsNullOrEmpty(n.NodeCode)) continue;
                var outgoing = g.Edges.Count(e =>
                    e is not null && string.Equals(e.SourceNodeCode, n.NodeCode, System.StringComparison.Ordinal));
                var incoming = g.Edges.Count(e =>
                    e is not null && string.Equals(e.TargetNodeCode, n.NodeCode, System.StringComparison.Ordinal));

                if (string.Equals(n.Kind, nameof(WorkflowNodeKind.AndSplit), System.StringComparison.Ordinal)
                    && outgoing < 2)
                {
                    ctx.AddFailure(nameof(WorkflowGraphInputDto.Nodes),
                        $"AndSplit node '{n.NodeCode}' must have at least 2 outgoing edges (found {outgoing}).");
                }
                if (string.Equals(n.Kind, nameof(WorkflowNodeKind.AndJoin), System.StringComparison.Ordinal)
                    && incoming < 2)
                {
                    ctx.AddFailure(nameof(WorkflowGraphInputDto.Nodes),
                        $"AndJoin node '{n.NodeCode}' must have at least 2 incoming edges (found {incoming}).");
                }
                if (string.Equals(n.Kind, nameof(WorkflowNodeKind.OrSplit), System.StringComparison.Ordinal))
                {
                    if (outgoing < 2)
                    {
                        ctx.AddFailure(nameof(WorkflowGraphInputDto.Nodes),
                            $"OrSplit node '{n.NodeCode}' must have at least 2 outgoing edges (found {outgoing}).");
                    }
                    if (string.IsNullOrWhiteSpace(n.ConditionExpression))
                    {
                        ctx.AddFailure(nameof(WorkflowGraphInputDto.Nodes),
                            $"OrSplit node '{n.NodeCode}' must carry a ConditionExpression.");
                    }
                }
            }

            // Cycle detection — only when references are sound enough to traverse.
            if (HasCycle(byCode, g.Edges))
            {
                ctx.AddFailure(nameof(WorkflowGraphInputDto.Edges), CycleErrorKey);
            }
        });
    }

    /// <summary>
    /// Reports whether the supplied node/edge graph contains a directed cycle. Uses an
    /// iterative DFS over the adjacency list — every node is the root of a sweep so a
    /// disconnected component cannot hide a cycle from the test.
    /// </summary>
    /// <param name="nodes">Map from node code to its DTO (codes assumed unique by the caller).</param>
    /// <param name="edges">Edge list; entries whose endpoints are not in <paramref name="nodes"/> are skipped.</param>
    /// <returns><c>true</c> when at least one back-edge exists.</returns>
    internal static bool HasCycle(
        IReadOnlyDictionary<string, WorkflowGraphNodeDto> nodes,
        IReadOnlyList<WorkflowGraphEdgeDto> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        // Build adjacency list — case-sensitive, code-keyed.
        var adj = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
        foreach (var code in nodes.Keys)
        {
            adj[code] = new List<string>();
        }
        foreach (var e in edges)
        {
            if (e is null) continue;
            if (!nodes.ContainsKey(e.SourceNodeCode) || !nodes.ContainsKey(e.TargetNodeCode)) continue;
            adj[e.SourceNodeCode].Add(e.TargetNodeCode);
        }

        // Three-state DFS: 0 = unvisited, 1 = in-progress, 2 = finished.
        var state = new Dictionary<string, int>(System.StringComparer.Ordinal);
        foreach (var code in nodes.Keys)
        {
            state[code] = 0;
        }

        foreach (var start in nodes.Keys)
        {
            if (state[start] != 0) continue;
            // Iterative DFS using a stack of (node, childIndex) frames.
            var stack = new Stack<(string Node, int Index)>();
            stack.Push((start, 0));
            state[start] = 1;
            while (stack.Count > 0)
            {
                var (node, idx) = stack.Peek();
                var children = adj[node];
                if (idx >= children.Count)
                {
                    state[node] = 2;
                    stack.Pop();
                    continue;
                }
                // Advance the parent's child pointer.
                stack.Pop();
                stack.Push((node, idx + 1));

                var child = children[idx];
                if (!state.TryGetValue(child, out var s)) continue;
                if (s == 1)
                {
                    return true; // back-edge ⇒ cycle.
                }
                if (s == 0)
                {
                    state[child] = 1;
                    stack.Push((child, 0));
                }
            }
        }

        return false;
    }

    /// <summary>True when the supplied kind string maps to a known <see cref="WorkflowNodeKind"/> name.</summary>
    /// <param name="kind">Kind string from the input DTO.</param>
    /// <returns><c>true</c> when the kind is parseable; <c>false</c> otherwise.</returns>
    private static bool BeKnownKind(string? kind) =>
        !string.IsNullOrWhiteSpace(kind)
        && System.Enum.TryParse<WorkflowNodeKind>(kind, ignoreCase: false, out _);

    /// <summary>
    /// Test-friendly helper: returns <c>true</c> when the supplied node code matches
    /// <see cref="NodeCodePattern"/>. Centralised so production and tests share the same
    /// definition.
    /// </summary>
    /// <param name="nodeCode">Code to check.</param>
    /// <returns><c>true</c> on a valid code.</returns>
    public static bool NodeCodeShapeIsValid(string? nodeCode)
    {
        if (string.IsNullOrWhiteSpace(nodeCode)) return false;
        return Regex.IsMatch(nodeCode, NodeCodePattern, RegexOptions.CultureInvariant);
    }
}
