using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Cnas.Ps.Core.Tests.ValueObjects;

/// <summary>
/// R0574 / R0592 — pinning tests for the <see cref="WorkflowRouting"/> helper +
/// <see cref="WorkflowRoutingDecision"/> value object. These tests anchor the
/// scaffolded multi-level forward / return-to-previous branches independently of
/// any BPM engine.
/// </summary>
public sealed class WorkflowRoutingTests
{
    [Fact]
    public void ComputeNextLevel_FromUserCnas_AdvancesToDirector()
    {
        var (decision, isTerminal) = WorkflowRouting.ComputeNextLevel(
            WorkflowApprovalLevel.UserCnas, reason: "fwd");

        decision.NextLevel.Should().Be(WorkflowApprovalLevel.DirectorOfDirectorate);
        decision.Reason.Should().Be("fwd");
        isTerminal.Should().BeFalse();
    }

    [Fact]
    public void ComputeNextLevel_FromDirector_AdvancesToChief()
    {
        var (decision, isTerminal) = WorkflowRouting.ComputeNextLevel(
            WorkflowApprovalLevel.DirectorOfDirectorate, reason: null);

        decision.NextLevel.Should().Be(WorkflowApprovalLevel.ChiefCnas);
        isTerminal.Should().BeFalse();
        decision.Reason.Should().NotBeNullOrWhiteSpace("a default reason is substituted when null is supplied");
    }

    [Fact]
    public void ComputeNextLevel_FromChief_ReportsTerminal()
    {
        var (decision, isTerminal) = WorkflowRouting.ComputeNextLevel(
            WorkflowApprovalLevel.ChiefCnas, reason: null);

        isTerminal.Should().BeTrue();
        decision.NextLevel.Should().Be(WorkflowApprovalLevel.ChiefCnas);
    }

    [Fact]
    public void ComputePreviousLevel_FromChief_ReturnsToDirector()
    {
        var (decision, isAtFloor) = WorkflowRouting.ComputePreviousLevel(
            WorkflowApprovalLevel.ChiefCnas, reason: "bounce-back");

        decision.NextLevel.Should().Be(WorkflowApprovalLevel.DirectorOfDirectorate);
        decision.Reason.Should().Be("bounce-back");
        isAtFloor.Should().BeFalse();
    }

    [Fact]
    public void ComputePreviousLevel_FromDirector_ReturnsToUserCnas()
    {
        var (decision, isAtFloor) = WorkflowRouting.ComputePreviousLevel(
            WorkflowApprovalLevel.DirectorOfDirectorate, reason: null);

        decision.NextLevel.Should().Be(WorkflowApprovalLevel.UserCnas);
        isAtFloor.Should().BeFalse();
    }

    [Fact]
    public void ComputePreviousLevel_FromUserCnas_ReportsAtFloor()
    {
        var (decision, isAtFloor) = WorkflowRouting.ComputePreviousLevel(
            WorkflowApprovalLevel.UserCnas, reason: null);

        isAtFloor.Should().BeTrue();
        decision.NextLevel.Should().Be(WorkflowApprovalLevel.UserCnas);
    }

    [Fact]
    public void StatusRoundTrip_PendingApproval_MapsToUserCnas()
    {
        WorkflowRouting.FromApplicationStatus(ApplicationStatus.PendingApproval)
            .Should().Be(WorkflowApprovalLevel.UserCnas);

        WorkflowRouting.ToApplicationStatus(WorkflowApprovalLevel.UserCnas)
            .Should().Be(ApplicationStatus.PendingApproval);
    }

    [Fact]
    public void StatusRoundTrip_SignedByDirector_MapsToDirector()
    {
        WorkflowRouting.FromApplicationStatus(ApplicationStatus.SignedByDirector)
            .Should().Be(WorkflowApprovalLevel.DirectorOfDirectorate);

        WorkflowRouting.ToApplicationStatus(WorkflowApprovalLevel.DirectorOfDirectorate)
            .Should().Be(ApplicationStatus.SignedByDirector);
    }

    [Fact]
    public void StatusRoundTrip_Approved_MapsToChiefCnas()
    {
        WorkflowRouting.FromApplicationStatus(ApplicationStatus.Approved)
            .Should().Be(WorkflowApprovalLevel.ChiefCnas);

        WorkflowRouting.ToApplicationStatus(WorkflowApprovalLevel.ChiefCnas)
            .Should().Be(ApplicationStatus.Approved);
    }

    [Fact]
    public void FromApplicationStatus_NonChainStatus_ReturnsNull()
    {
        WorkflowRouting.FromApplicationStatus(ApplicationStatus.Draft).Should().BeNull();
        WorkflowRouting.FromApplicationStatus(ApplicationStatus.UnderExamination).Should().BeNull();
        WorkflowRouting.FromApplicationStatus(ApplicationStatus.Returned).Should().BeNull();
    }

    [Fact]
    public void RoutingDecision_NullReason_NormalisedToEmpty()
    {
        var decision = WorkflowRoutingDecision.Create(WorkflowApprovalLevel.UserCnas, reason: null);
        decision.Reason.Should().Be(string.Empty);
    }

    [Fact]
    public void RoutingDecision_TooLongReason_Truncated()
    {
        var input = new string('x', WorkflowRoutingDecision.MaxReasonLength + 50);
        var decision = WorkflowRoutingDecision.Create(WorkflowApprovalLevel.UserCnas, reason: input);
        decision.Reason.Length.Should().Be(WorkflowRoutingDecision.MaxReasonLength);
    }
}
