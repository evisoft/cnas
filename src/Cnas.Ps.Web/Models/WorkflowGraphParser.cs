using System.Xml;
using System.Xml.Linq;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Web.Models;

/// <summary>
/// R0121 / CF 16.02 — read-only, hand-rolled parser that ingests the project's
/// minimal <c>&lt;workflow&gt;</c> XML envelope or BPMN-2.0 <c>&lt;bpmn:definitions&gt;</c>
/// root and normalises both into a <see cref="WorkflowGraphModel"/>. The parser
/// is intentionally tiny (≈ 150 LOC) and dependency-free: no XSD, no SAX
/// pipeline, just <see cref="XDocument"/> + element matching. It runs inside
/// the Blazor WASM client so its budget is measured in browser bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safety envelope.</b> The parser refuses any payload above 64 KiB
/// (<see cref="MaxPayloadBytes"/>), more than 200 nodes
/// (<see cref="MaxNodes"/>) or more than 400 edges (<see cref="MaxEdges"/>) so
/// a hostile administrator cannot lock the browser by pasting a huge document.
/// DTD / external-entity resolution is disabled at the
/// <see cref="XmlReader"/> level — billion-laughs and external entity probes
/// are impossible by construction.
/// </para>
/// <para>
/// <b>Schema variants.</b> Two shapes are accepted:
/// <list type="bullet">
///   <item>
///     The project's simple envelope —
///     <c>&lt;workflow&gt;&lt;nodes&gt;&lt;node code="…" kind="…" label="…" /&gt;…&lt;/nodes&gt;&lt;edges&gt;&lt;edge from="…" to="…" /&gt;…&lt;/edges&gt;&lt;/workflow&gt;</c>.
///     This is the iteration-91 default and what the renderer was designed
///     against.
///   </item>
///   <item>
///     A small BPMN 2.0 subset —
///     <c>&lt;bpmn:definitions&gt;</c> with <c>&lt;bpmn:startEvent&gt;</c> /
///     <c>&lt;bpmn:endEvent&gt;</c> / <c>&lt;bpmn:userTask&gt;</c> /
///     <c>&lt;bpmn:serviceTask&gt;</c> / <c>&lt;bpmn:sequenceFlow&gt;</c>
///     elements. Everything outside this subset is silently ignored.
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Failure semantics.</b> Every failure returns a stable error code under the
/// <c>WORKFLOW_XML.*</c> namespace via <see cref="Result{T}.Failure(string, string)"/>
/// so the UI can render targeted prompts ("payload too large", "orphan edge",
/// "unknown root") without parsing the human-readable message.
/// </para>
/// </remarks>
public static class WorkflowGraphParser
{
    /// <summary>Maximum payload size in bytes. Larger inputs are rejected before any XML parse.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>Maximum node count. Above this the parser short-circuits with a limit error.</summary>
    public const int MaxNodes = 200;

    /// <summary>Maximum edge count. Above this the parser short-circuits with a limit error.</summary>
    public const int MaxEdges = 400;

    /// <summary>BPMN 2.0 namespace URI used by the secondary schema variant.</summary>
    private const string BpmnNs = "http://www.omg.org/spec/BPMN/20100524/MODEL";

    /// <summary>
    /// Parses <paramref name="xml"/> into a <see cref="WorkflowGraphModel"/>.
    /// Returns a failure <see cref="Result{T}"/> with a stable
    /// <c>WORKFLOW_XML.*</c> error code when the payload fails any safety,
    /// shape, or structural check.
    /// </summary>
    /// <param name="xml">The workflow XML body, typically the textarea contents.</param>
    /// <returns>
    /// Success with the parsed model on a valid document; failure carrying one
    /// of <c>WORKFLOW_XML.TOO_LARGE</c>, <c>WORKFLOW_XML.PARSE_ERROR</c>,
    /// <c>WORKFLOW_XML.INVALID_ROOT</c>, <c>WORKFLOW_XML.TOO_MANY_NODES</c>,
    /// <c>WORKFLOW_XML.TOO_MANY_EDGES</c>, <c>WORKFLOW_XML.MISSING_NODE_CODE</c>,
    /// or <c>WORKFLOW_XML.ORPHAN_EDGE</c>.
    /// </returns>
    public static Result<WorkflowGraphModel> Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return Result<WorkflowGraphModel>.Failure(
                "WORKFLOW_XML.PARSE_ERROR",
                "Workflow XML is empty.");
        }

        // Byte-length check before XML parse so a hostile multi-megabyte string
        // never reaches the XmlReader (which would buffer the whole document).
        if (xml.Length > MaxPayloadBytes)
        {
            return Result<WorkflowGraphModel>.Failure(
                "WORKFLOW_XML.TOO_LARGE",
                $"Workflow XML exceeds the {MaxPayloadBytes}-byte safety cap.");
        }

        XDocument doc;
        try
        {
            // Disable DTD processing + external entity resolution so the parser
            // is resistant to billion-laughs and SSRF-via-external-entity probes.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                CloseInput = true,
            };
            using var sr = new System.IO.StringReader(xml);
            using var reader = XmlReader.Create(sr, settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            return Result<WorkflowGraphModel>.Failure(
                "WORKFLOW_XML.PARSE_ERROR",
                $"Workflow XML is not well-formed: {ex.Message}");
        }

        var root = doc.Root;
        if (root is null)
        {
            return Result<WorkflowGraphModel>.Failure(
                "WORKFLOW_XML.PARSE_ERROR",
                "Workflow XML has no root element.");
        }

        // Two accepted root shapes — dispatch by local name + namespace.
        if (string.Equals(root.Name.LocalName, "workflow", StringComparison.Ordinal)
            && string.IsNullOrEmpty(root.Name.NamespaceName))
        {
            return ParseSimpleWorkflow(root);
        }

        if (string.Equals(root.Name.LocalName, "definitions", StringComparison.Ordinal)
            && string.Equals(root.Name.NamespaceName, BpmnNs, StringComparison.Ordinal))
        {
            return ParseBpmnSubset(root);
        }

        return Result<WorkflowGraphModel>.Failure(
            "WORKFLOW_XML.INVALID_ROOT",
            $"Unsupported root element '{root.Name}'. Expected <workflow> or <bpmn:definitions>.");
    }

    /// <summary>
    /// Parses the project's simple <c>&lt;workflow&gt;</c> envelope. Node order
    /// is preserved from document order so the renderer can lay out the graph
    /// deterministically.
    /// </summary>
    /// <param name="root">The <c>&lt;workflow&gt;</c> element.</param>
    /// <returns>The parsed model or a structural failure.</returns>
    private static Result<WorkflowGraphModel> ParseSimpleWorkflow(XElement root)
    {
        var nodes = new List<WorkflowNodeModel>();
        var nodesContainer = root.Element("nodes");
        if (nodesContainer is not null)
        {
            foreach (var nodeEl in nodesContainer.Elements("node"))
            {
                var code = (string?)nodeEl.Attribute("code");
                if (string.IsNullOrWhiteSpace(code))
                {
                    return Result<WorkflowGraphModel>.Failure(
                        "WORKFLOW_XML.MISSING_NODE_CODE",
                        "Every <node> requires a non-empty 'code' attribute.");
                }
                var kind = (string?)nodeEl.Attribute("kind") ?? "UserTask";
                var label = (string?)nodeEl.Attribute("label");
                nodes.Add(new WorkflowNodeModel(code!, kind, label));

                if (nodes.Count > MaxNodes)
                {
                    return Result<WorkflowGraphModel>.Failure(
                        "WORKFLOW_XML.TOO_MANY_NODES",
                        $"Workflow XML exceeds the {MaxNodes}-node safety cap.");
                }
            }
        }

        var edges = new List<WorkflowEdgeModel>();
        var edgesContainer = root.Element("edges");
        if (edgesContainer is not null)
        {
            foreach (var edgeEl in edgesContainer.Elements("edge"))
            {
                var from = (string?)edgeEl.Attribute("from") ?? string.Empty;
                var to = (string?)edgeEl.Attribute("to") ?? string.Empty;
                var label = (string?)edgeEl.Attribute("label");
                edges.Add(new WorkflowEdgeModel(from, to, label));

                if (edges.Count > MaxEdges)
                {
                    return Result<WorkflowGraphModel>.Failure(
                        "WORKFLOW_XML.TOO_MANY_EDGES",
                        $"Workflow XML exceeds the {MaxEdges}-edge safety cap.");
                }
            }
        }

        return ValidateAndBuild(nodes, edges);
    }

    /// <summary>
    /// Parses a small BPMN 2.0 subset: <c>startEvent</c> / <c>endEvent</c> /
    /// <c>userTask</c> / <c>serviceTask</c> / <c>sequenceFlow</c>. Everything
    /// else under <c>&lt;bpmn:process&gt;</c> is ignored. <c>id</c> attributes
    /// become node/edge codes; <c>name</c> attributes become labels.
    /// </summary>
    /// <param name="root">The <c>&lt;bpmn:definitions&gt;</c> element.</param>
    /// <returns>The parsed model or a structural failure.</returns>
    private static Result<WorkflowGraphModel> ParseBpmnSubset(XElement root)
    {
        var nodes = new List<WorkflowNodeModel>();
        var edges = new List<WorkflowEdgeModel>();

        // BPMN scopes node/edge elements under <bpmn:process>; iterate every
        // process in document order so multi-pool diagrams still flatten.
        foreach (var proc in root.Elements(XName.Get("process", BpmnNs)))
        {
            foreach (var el in proc.Elements())
            {
                if (el.Name.NamespaceName != BpmnNs)
                {
                    continue;
                }

                var localName = el.Name.LocalName;
                var id = (string?)el.Attribute("id");
                var name = (string?)el.Attribute("name");

                switch (localName)
                {
                    case "startEvent":
                        if (string.IsNullOrWhiteSpace(id)) goto MissingCode;
                        nodes.Add(new WorkflowNodeModel(id!, "Start", name));
                        break;
                    case "endEvent":
                        if (string.IsNullOrWhiteSpace(id)) goto MissingCode;
                        nodes.Add(new WorkflowNodeModel(id!, "End", name));
                        break;
                    case "userTask":
                        if (string.IsNullOrWhiteSpace(id)) goto MissingCode;
                        nodes.Add(new WorkflowNodeModel(id!, "UserTask", name));
                        break;
                    case "serviceTask":
                        if (string.IsNullOrWhiteSpace(id)) goto MissingCode;
                        nodes.Add(new WorkflowNodeModel(id!, "ServiceTask", name));
                        break;
                    case "sequenceFlow":
                        {
                            var src = (string?)el.Attribute("sourceRef") ?? string.Empty;
                            var tgt = (string?)el.Attribute("targetRef") ?? string.Empty;
                            edges.Add(new WorkflowEdgeModel(src, tgt, name));
                            break;
                        }
                    default:
                        // Unrecognised BPMN element under <process>; skip silently.
                        break;
                }

                if (nodes.Count > MaxNodes)
                {
                    return Result<WorkflowGraphModel>.Failure(
                        "WORKFLOW_XML.TOO_MANY_NODES",
                        $"Workflow XML exceeds the {MaxNodes}-node safety cap.");
                }
                if (edges.Count > MaxEdges)
                {
                    return Result<WorkflowGraphModel>.Failure(
                        "WORKFLOW_XML.TOO_MANY_EDGES",
                        $"Workflow XML exceeds the {MaxEdges}-edge safety cap.");
                }
            }
        }

        return ValidateAndBuild(nodes, edges);

        MissingCode:
        return Result<WorkflowGraphModel>.Failure(
            "WORKFLOW_XML.MISSING_NODE_CODE",
            "Every BPMN node requires a non-empty 'id' attribute.");
    }

    /// <summary>
    /// Final structural validation: every edge must reference a known node
    /// code. Empties through to a successful empty graph (the renderer
    /// handles that as the "empty grid + parse-success" state).
    /// </summary>
    /// <param name="nodes">The accumulated nodes.</param>
    /// <param name="edges">The accumulated edges.</param>
    /// <returns>Success on a structurally valid graph; orphan-edge failure otherwise.</returns>
    private static Result<WorkflowGraphModel> ValidateAndBuild(
        List<WorkflowNodeModel> nodes,
        List<WorkflowEdgeModel> edges)
    {
        var knownCodes = new HashSet<string>(nodes.Select(n => n.Code), StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (!knownCodes.Contains(e.From) || !knownCodes.Contains(e.To))
            {
                return Result<WorkflowGraphModel>.Failure(
                    "WORKFLOW_XML.ORPHAN_EDGE",
                    $"Edge '{e.From} -> {e.To}' references a node that is not defined.");
            }
        }

        return Result<WorkflowGraphModel>.Success(new WorkflowGraphModel(nodes, edges));
    }
}
