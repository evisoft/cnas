using System;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Cnas.Ps.Core.Tests.Domain;

/// <summary>
/// R0122 — unit tests for <see cref="WorkflowStepSla"/> — the strongly-typed SLA
/// descriptor that replaces ad-hoc JSON shapes on workflow step definitions.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written FIRST (RED) before the production
/// type lands.
/// </remarks>
public sealed class WorkflowStepSlaTests
{
    [Fact]
    public void Create_ValidWindow_ReturnsSuccess()
    {
        // Act
        var result = WorkflowStepSla.Create(
            dueWithin: TimeSpan.FromHours(8),
            escalateAfter: TimeSpan.FromHours(12),
            businessHoursOnly: true);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.DueWithin.Should().Be(TimeSpan.FromHours(8));
        result.Value.EscalateAfter.Should().Be(TimeSpan.FromHours(12));
        result.Value.BusinessHoursOnly.Should().BeTrue();
    }

    [Fact]
    public void Create_NonPositiveDue_ReturnsValidationFailure()
    {
        var result = WorkflowStepSla.Create(
            dueWithin: TimeSpan.Zero,
            escalateAfter: TimeSpan.FromHours(1),
            businessHoursOnly: false);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Create_EscalateBeforeDue_ReturnsValidationFailure()
    {
        // EscalateAfter must be >= DueWithin so the SLA monitor doesn't escalate before
        // the deadline has been missed.
        var result = WorkflowStepSla.Create(
            dueWithin: TimeSpan.FromHours(8),
            escalateAfter: TimeSpan.FromHours(4),
            businessHoursOnly: false);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("Escalate");
    }
}
