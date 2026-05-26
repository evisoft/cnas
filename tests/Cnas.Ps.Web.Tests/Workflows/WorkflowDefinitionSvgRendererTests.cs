using Bunit;
using Cnas.Ps.Web.Components.Admin;
using Cnas.Ps.Web.Models;

namespace Cnas.Ps.Web.Tests.Workflows;

/// <summary>
/// R0121 / CF 16.02 — RED→GREEN tests for the read-only SVG renderer of a
/// parsed <see cref="WorkflowGraphModel"/>. The renderer is the "visual" half
/// of the iteration-91 MVP visual designer.
/// </summary>
public sealed class WorkflowDefinitionSvgRendererTests : TestContext
{
    public WorkflowDefinitionSvgRendererTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static WorkflowGraphModel Build(params (string code, string kind)[] nodes)
    {
        var ns = nodes.Select(n => new WorkflowNodeModel(n.code, n.kind, Label: n.code)).ToList();
        return new WorkflowGraphModel(ns, Array.Empty<WorkflowEdgeModel>());
    }

    [Fact]
    public void Renderer_WithThreeTaskNodes_RendersExactlyThreeNodeShapes()
    {
        var model = new WorkflowGraphModel(
            new[]
            {
                new WorkflowNodeModel("A", "UserTask", "A"),
                new WorkflowNodeModel("B", "UserTask", "B"),
                new WorkflowNodeModel("C", "UserTask", "C"),
            },
            Array.Empty<WorkflowEdgeModel>());

        var cut = RenderComponent<WorkflowDefinitionSvgRenderer>(p => p.Add(x => x.Model, model));

        // Task nodes are rendered as <rect data-testid="node-shape" />.
        cut.FindAll("[data-testid='node-shape']").Count.Should().Be(3);
    }

    [Fact]
    public void Renderer_StartNode_RendersAsCircleNotRect()
    {
        var model = Build(("S", "Start"), ("T", "UserTask"));

        var cut = RenderComponent<WorkflowDefinitionSvgRenderer>(p => p.Add(x => x.Model, model));

        // The start node uses <circle data-testid="node-shape-start" />.
        cut.FindAll("[data-testid='node-shape-start']").Count.Should().Be(1);
    }

    [Fact]
    public void Renderer_WithConnectedNodes_RendersEdgeLines()
    {
        var model = new WorkflowGraphModel(
            new[]
            {
                new WorkflowNodeModel("A", "Start", "A"),
                new WorkflowNodeModel("B", "End", "B"),
            },
            new[] { new WorkflowEdgeModel("A", "B", Label: null) });

        var cut = RenderComponent<WorkflowDefinitionSvgRenderer>(p => p.Add(x => x.Model, model));

        cut.FindAll("[data-testid='edge-line']").Count.Should().Be(1);
    }

    [Fact]
    public void Renderer_NullModel_RendersEmptyGridWithPlaceholder()
    {
        var cut = RenderComponent<WorkflowDefinitionSvgRenderer>(p => p.Add(x => x.Model, (WorkflowGraphModel?)null));

        cut.FindAll("[data-testid='empty-placeholder']").Count.Should().Be(1);
        cut.FindAll("[data-testid='node-shape']").Count.Should().Be(0);
    }
}
