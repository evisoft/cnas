using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Cnas.Ps.Core.Tests.Domain;

/// <summary>
/// R0122 — unit tests for <see cref="WorkflowPerformerAssignment"/> — the strongly-typed
/// performer descriptor that replaces ad-hoc JSON shapes on workflow step definitions.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written FIRST (RED) before the production
/// type lands. The value object is immutable, validates its own invariants at
/// construction time, and surfaces failures via <see cref="Result{T}"/>.
/// </remarks>
public sealed class WorkflowPerformerAssignmentTests
{
    [Fact]
    public void Create_RoleKind_WithCode_ReturnsSuccess()
    {
        // Act
        var result = WorkflowPerformerAssignment.Create(
            kind: WorkflowPerformerKind.Role,
            code: "cnas-decider");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Kind.Should().Be(WorkflowPerformerKind.Role);
        result.Value.Code.Should().Be("cnas-decider");
        result.Value.FallbackKind.Should().BeNull();
        result.Value.FallbackCode.Should().BeNull();
    }

    [Fact]
    public void Create_OriginatorKind_AllowsEmptyCode()
    {
        // Originator / Supervisor are reflexive descriptors — they refer to "the case
        // originator" / "the originator's supervisor" so they carry no code.
        var result = WorkflowPerformerAssignment.Create(
            kind: WorkflowPerformerKind.Originator,
            code: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().BeNull();
    }

    [Fact]
    public void Create_RoleKind_MissingCode_ReturnsValidationFailure()
    {
        // Role / Group / NamedUser MUST carry a non-empty code.
        var result = WorkflowPerformerAssignment.Create(
            kind: WorkflowPerformerKind.Role,
            code: "");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("Role");
    }

    [Fact]
    public void Create_WithFallback_CarriesFallbackTuple()
    {
        // The fallback path lets a workflow say "try the primary role; if no one is
        // available, route to this group instead". Both halves are validated.
        var result = WorkflowPerformerAssignment.Create(
            kind: WorkflowPerformerKind.Role,
            code: "cnas-decider",
            fallbackKind: WorkflowPerformerKind.Group,
            fallbackCode: "pension-group-a");

        result.IsSuccess.Should().BeTrue();
        result.Value.FallbackKind.Should().Be(WorkflowPerformerKind.Group);
        result.Value.FallbackCode.Should().Be("pension-group-a");
    }

    [Fact]
    public void Create_FallbackKindWithoutFallbackCode_ReturnsValidationFailure()
    {
        // Partial fallback specifications are rejected so a misconfiguration cannot
        // silently lose the fallback edge.
        var result = WorkflowPerformerAssignment.Create(
            kind: WorkflowPerformerKind.Role,
            code: "cnas-decider",
            fallbackKind: WorkflowPerformerKind.Group,
            fallbackCode: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Create_CodeExceedsCap_ReturnsValidationFailure()
    {
        // Codes are capped at 64 chars to match the rest of the codebase's role / group
        // code conventions.
        var result = WorkflowPerformerAssignment.Create(
            kind: WorkflowPerformerKind.Role,
            code: new string('a', 65));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }
}
