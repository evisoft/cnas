using System.Collections.Generic;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Cnas.Ps.Core.Tests.Domain;

/// <summary>
/// R0016 — exhaustive unit tests for <see cref="StatusTransitionTable{TStatus}"/>.
/// The table is the generic substrate that replaces hand-rolled <c>if</c>-ladders
/// in services where enum transition rules are documented in the entity / enum XML.
/// </summary>
/// <remarks>
/// Tests are written FIRST per CLAUDE.md RULE 1 — these should be RED until the
/// production type is added under <c>src/Cnas.Ps.Core/Domain/StatusTransitionTable.cs</c>.
/// The harness intentionally exercises both the generic <c>CanTransition</c> Boolean
/// predicate and the richer <c>Validate</c> path that returns a stable
/// <see cref="Result"/> with the <see cref="ErrorCodes"/>-style code
/// <c>STATUS.ILLEGAL_TRANSITION</c>.
/// </remarks>
public sealed class StatusTransitionTableTests
{
    /// <summary>
    /// A small bespoke enum used to test the generic table in isolation; deliberately
    /// kept inside the test file so production enums can evolve without breaking the
    /// table's contract tests.
    /// </summary>
    private enum SampleStatus
    {
        /// <summary>Initial state.</summary>
        Draft = 0,

        /// <summary>Mid-lifecycle.</summary>
        Submitted = 1,

        /// <summary>Terminal accepted.</summary>
        Approved = 2,

        /// <summary>Terminal rejected.</summary>
        Rejected = 3,
    }

    [Fact]
    public void EmptyTable_DeniesEveryTransition()
    {
        // Arrange — empty allowed map.
        var table = new StatusTransitionTable<SampleStatus>(
            new Dictionary<SampleStatus, IReadOnlySet<SampleStatus>>());

        // Act + Assert — every (from, to) pair is denied.
        table.CanTransition(SampleStatus.Draft, SampleStatus.Submitted).Should().BeFalse();
        table.CanTransition(SampleStatus.Submitted, SampleStatus.Approved).Should().BeFalse();
        table.CanTransition(SampleStatus.Draft, SampleStatus.Draft).Should().BeFalse();
    }

    [Fact]
    public void AllowedTransition_ReturnsSuccess()
    {
        // Arrange — allow Draft → Submitted only.
        var table = new StatusTransitionTable<SampleStatus>(
            new Dictionary<SampleStatus, IReadOnlySet<SampleStatus>>
            {
                [SampleStatus.Draft] = new HashSet<SampleStatus> { SampleStatus.Submitted },
            });

        // Act
        var validate = table.Validate(SampleStatus.Draft, SampleStatus.Submitted);

        // Assert
        table.CanTransition(SampleStatus.Draft, SampleStatus.Submitted).Should().BeTrue();
        validate.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void DisallowedTransition_ReturnsFailureWithStableCode()
    {
        // Arrange — allow Draft → Submitted only.
        var table = new StatusTransitionTable<SampleStatus>(
            new Dictionary<SampleStatus, IReadOnlySet<SampleStatus>>
            {
                [SampleStatus.Draft] = new HashSet<SampleStatus> { SampleStatus.Submitted },
            });

        // Act — Submitted → Approved is not in the table.
        var validate = table.Validate(SampleStatus.Submitted, SampleStatus.Approved);

        // Assert
        table.CanTransition(SampleStatus.Submitted, SampleStatus.Approved).Should().BeFalse();
        validate.IsSuccess.Should().BeFalse();
        validate.ErrorCode.Should().Be(StatusTransitionTable<SampleStatus>.IllegalTransitionCode);
        validate.ErrorMessage.Should().Contain("Submitted");
        validate.ErrorMessage.Should().Contain("Approved");
    }

    [Fact]
    public void SameStateTransition_AllowedOrDenied_PerTable()
    {
        // Arrange — explicitly allow Draft → Draft (self-loop) but NOT Approved → Approved.
        var table = new StatusTransitionTable<SampleStatus>(
            new Dictionary<SampleStatus, IReadOnlySet<SampleStatus>>
            {
                [SampleStatus.Draft] = new HashSet<SampleStatus> { SampleStatus.Draft },
            });

        // Act + Assert
        table.CanTransition(SampleStatus.Draft, SampleStatus.Draft).Should().BeTrue();
        table.CanTransition(SampleStatus.Approved, SampleStatus.Approved).Should().BeFalse();
    }

    [Fact]
    public void ClaimStatusMatrix_CoversDocumentedRules()
    {
        // Arrange — build a table that mirrors the rules documented in ClaimService:
        //   * From Open / PartiallyPaid / Disputed → may go to Cancelled (admin cancel).
        //   * From Open / PartiallyPaid → may register a payment which transitions to
        //     PartiallyPaid (still owing) or Settled (paid in full).
        //   * From Open / PartiallyPaid → may flip to Disputed.
        //   * Settled and Cancelled are TERMINAL — no outbound transitions.
        var table = new StatusTransitionTable<ClaimStatus>(
            new Dictionary<ClaimStatus, IReadOnlySet<ClaimStatus>>
            {
                [ClaimStatus.Open] = new HashSet<ClaimStatus>
                {
                    ClaimStatus.PartiallyPaid,
                    ClaimStatus.Settled,
                    ClaimStatus.Cancelled,
                    ClaimStatus.Disputed,
                },
                [ClaimStatus.PartiallyPaid] = new HashSet<ClaimStatus>
                {
                    ClaimStatus.PartiallyPaid,
                    ClaimStatus.Settled,
                    ClaimStatus.Cancelled,
                    ClaimStatus.Disputed,
                },
                [ClaimStatus.Disputed] = new HashSet<ClaimStatus>
                {
                    ClaimStatus.Cancelled,
                },
            });

        // Assert — happy paths.
        table.CanTransition(ClaimStatus.Open, ClaimStatus.Settled).Should().BeTrue();
        table.CanTransition(ClaimStatus.Open, ClaimStatus.Cancelled).Should().BeTrue();
        table.CanTransition(ClaimStatus.PartiallyPaid, ClaimStatus.Settled).Should().BeTrue();
        table.CanTransition(ClaimStatus.Disputed, ClaimStatus.Cancelled).Should().BeTrue();

        // Assert — terminal-state denials.
        table.CanTransition(ClaimStatus.Settled, ClaimStatus.Disputed).Should().BeFalse();
        table.CanTransition(ClaimStatus.Cancelled, ClaimStatus.Open).Should().BeFalse();
        table.CanTransition(ClaimStatus.Disputed, ClaimStatus.Settled).Should().BeFalse();
    }

    [Fact]
    public void WorkflowTaskStatusMatrix_CoversDocumentedRules()
    {
        // Arrange — mirror the documented WorkflowTaskStatus lifecycle:
        //   Pending → InProgress (claim) | Cancelled (admin cancel) | Overdue (SLA breach)
        //   InProgress → Completed | Cancelled | Overdue
        //   Overdue   → InProgress (recovered) | Completed | Cancelled
        //   Completed / Cancelled — terminal.
        var table = new StatusTransitionTable<WorkflowTaskStatus>(
            new Dictionary<WorkflowTaskStatus, IReadOnlySet<WorkflowTaskStatus>>
            {
                [WorkflowTaskStatus.Pending] = new HashSet<WorkflowTaskStatus>
                {
                    WorkflowTaskStatus.InProgress,
                    WorkflowTaskStatus.Cancelled,
                    WorkflowTaskStatus.Overdue,
                },
                [WorkflowTaskStatus.InProgress] = new HashSet<WorkflowTaskStatus>
                {
                    WorkflowTaskStatus.Completed,
                    WorkflowTaskStatus.Cancelled,
                    WorkflowTaskStatus.Overdue,
                },
                [WorkflowTaskStatus.Overdue] = new HashSet<WorkflowTaskStatus>
                {
                    WorkflowTaskStatus.InProgress,
                    WorkflowTaskStatus.Completed,
                    WorkflowTaskStatus.Cancelled,
                },
            });

        // Assert
        table.CanTransition(WorkflowTaskStatus.Pending, WorkflowTaskStatus.InProgress).Should().BeTrue();
        table.CanTransition(WorkflowTaskStatus.InProgress, WorkflowTaskStatus.Completed).Should().BeTrue();
        table.CanTransition(WorkflowTaskStatus.Overdue, WorkflowTaskStatus.Completed).Should().BeTrue();

        // Terminal-state denials.
        table.CanTransition(WorkflowTaskStatus.Completed, WorkflowTaskStatus.InProgress).Should().BeFalse();
        table.CanTransition(WorkflowTaskStatus.Cancelled, WorkflowTaskStatus.InProgress).Should().BeFalse();

        // A direct Pending → Completed is illegal (must Claim first).
        table.CanTransition(WorkflowTaskStatus.Pending, WorkflowTaskStatus.Completed).Should().BeFalse();
        var validate = table.Validate(
            WorkflowTaskStatus.Pending, WorkflowTaskStatus.Completed);
        validate.IsSuccess.Should().BeFalse();
        validate.ErrorCode.Should().Be(StatusTransitionTable<WorkflowTaskStatus>.IllegalTransitionCode);
    }

    [Fact]
    public void Constructor_NullAllowedMap_Throws()
    {
        // Arrange + Act
        var act = () => new StatusTransitionTable<SampleStatus>(allowed: null!);

        // Assert
        act.Should().Throw<System.ArgumentNullException>();
    }
}
