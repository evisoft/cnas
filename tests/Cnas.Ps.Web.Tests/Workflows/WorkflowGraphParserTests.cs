using System.Text;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Web.Models;

namespace Cnas.Ps.Web.Tests.Workflows;

/// <summary>
/// R0121 / CF 16.02 — RED→GREEN tests for the read-only
/// <see cref="WorkflowGraphParser"/>. The parser accepts the project's minimal
/// <c>&lt;workflow&gt;</c> XML envelope, normalises it to a
/// <see cref="WorkflowGraphModel"/>, and enforces hard input limits so a hostile
/// XML payload cannot exhaust browser memory.
/// </summary>
public sealed class WorkflowGraphParserTests
{
    [Fact]
    public void Parse_MinimalSingleNode_Succeeds()
    {
        const string xml = """
            <workflow>
              <nodes>
                <node code="START" kind="Start" label="Begin" />
              </nodes>
              <edges>
              </edges>
            </workflow>
            """;

        var result = WorkflowGraphParser.Parse(xml);

        result.IsSuccess.Should().BeTrue();
        result.Value.Nodes.Should().HaveCount(1);
        result.Value.Nodes[0].Code.Should().Be("START");
        result.Value.Nodes[0].Kind.Should().Be("Start");
        result.Value.Nodes[0].Label.Should().Be("Begin");
        result.Value.Edges.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ThreeNodeLinearFlow_Succeeds()
    {
        const string xml = """
            <workflow>
              <nodes>
                <node code="START" kind="Start" />
                <node code="REVIEW" kind="UserTask" label="Review" />
                <node code="END" kind="End" />
              </nodes>
              <edges>
                <edge from="START" to="REVIEW" />
                <edge from="REVIEW" to="END" />
              </edges>
            </workflow>
            """;

        var result = WorkflowGraphParser.Parse(xml);

        result.IsSuccess.Should().BeTrue();
        result.Value.Nodes.Should().HaveCount(3);
        result.Value.Edges.Should().HaveCount(2);
        result.Value.Edges[0].From.Should().Be("START");
        result.Value.Edges[0].To.Should().Be("REVIEW");
        result.Value.Edges[1].From.Should().Be("REVIEW");
        result.Value.Edges[1].To.Should().Be("END");
    }

    [Fact]
    public void Parse_PayloadOver64Kb_RejectedWithSizeError()
    {
        // Build a > 64 KB payload by padding a comment with junk; the parser must
        // reject before any structural validation runs.
        var sb = new StringBuilder("<workflow><nodes><node code=\"A\" kind=\"Start\" /></nodes><edges /></workflow>");
        sb.Append("<!-- ");
        sb.Append('x', 70 * 1024);
        sb.Append(" -->");

        var result = WorkflowGraphParser.Parse(sb.ToString());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("WORKFLOW_XML.TOO_LARGE");
    }

    [Fact]
    public void Parse_MoreThan200Nodes_RejectedWithLimitError()
    {
        var sb = new StringBuilder("<workflow><nodes>");
        for (int i = 0; i < 250; i++)
        {
            sb.Append("<node code=\"N").Append(i).Append("\" kind=\"UserTask\" />");
        }
        sb.Append("</nodes><edges /></workflow>");

        var result = WorkflowGraphParser.Parse(sb.ToString());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("WORKFLOW_XML.TOO_MANY_NODES");
    }

    [Fact]
    public void Parse_UnknownRootElement_RejectedAsInvalidShape()
    {
        const string xml = "<process><nodes/></process>";

        var result = WorkflowGraphParser.Parse(xml);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("WORKFLOW_XML.INVALID_ROOT");
    }

    [Fact]
    public void Parse_MalformedXml_RejectedAsParseError()
    {
        const string xml = "<workflow><nodes><node></broken>";

        var result = WorkflowGraphParser.Parse(xml);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("WORKFLOW_XML.PARSE_ERROR");
    }

    [Fact]
    public void Parse_EdgeReferencingMissingNode_RejectedAsOrphanEdge()
    {
        const string xml = """
            <workflow>
              <nodes>
                <node code="A" kind="Start" />
              </nodes>
              <edges>
                <edge from="A" to="GHOST" />
              </edges>
            </workflow>
            """;

        var result = WorkflowGraphParser.Parse(xml);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("WORKFLOW_XML.ORPHAN_EDGE");
    }
}
