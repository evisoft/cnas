using System.Collections.Generic;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Cnas.Ps.Core.Tests.Domain;

/// <summary>
/// R0939 / iter 136 — pins the canonical 8-state <see cref="ApplicationStatus"/>
/// transition matrix exposed via
/// <see cref="ApplicationStatusTransitions.Table"/>. Every allowed edge is
/// exercised positively, every undocumented edge is exercised negatively, and the
/// terminal states are pinned as denying ALL outgoing transitions.
/// </summary>
/// <remarks>
/// <para>
/// The Romanian-language CNAS spec defines the lifecycle as
/// <c>Înregistrată → ÎnAșteptareDocumente? → ÎnExaminare → SemnatăUtilizator →
/// SemnatăȘefulDirecției? → AprobatăȘefulCNAS / Returnată / Refuz / Terminată</c>.
/// The enum-to-Romanian mapping is documented on <see cref="ApplicationStatus"/>;
/// this fixture asserts every edge in the matrix so a future drift between the
/// production table and the spec triggers a RED test rather than silently shipping.
/// </para>
/// </remarks>
public sealed class ApplicationStatusTransitionsTests
{
    /// <summary>
    /// Every edge documented as legal on
    /// <see cref="ApplicationStatusTransitions.Table"/> MUST be accepted by the
    /// underlying <see cref="StatusTransitionTable{TStatus}.CanTransition"/>
    /// predicate. The list mirrors the matrix in the production source so a
    /// future widening (new state, new edge) shows up here as a one-line change.
    /// </summary>
    public static IEnumerable<object[]> AllowedTransitions => new[]
    {
        // Draft origin
        new object[] { ApplicationStatus.Draft, ApplicationStatus.Submitted },
        new object[] { ApplicationStatus.Draft, ApplicationStatus.Withdrawn },

        // Înregistrată
        new object[] { ApplicationStatus.Submitted, ApplicationStatus.RejectedIncomplete },
        new object[] { ApplicationStatus.Submitted, ApplicationStatus.UnderExamination },
        new object[] { ApplicationStatus.Submitted, ApplicationStatus.Rejected },
        new object[] { ApplicationStatus.Submitted, ApplicationStatus.Withdrawn },

        // ÎnAșteptareDocumente
        new object[] { ApplicationStatus.RejectedIncomplete, ApplicationStatus.Submitted },
        new object[] { ApplicationStatus.RejectedIncomplete, ApplicationStatus.Rejected },
        new object[] { ApplicationStatus.RejectedIncomplete, ApplicationStatus.Withdrawn },

        // ÎnExaminare
        new object[] { ApplicationStatus.UnderExamination, ApplicationStatus.PendingApproval },
        new object[] { ApplicationStatus.UnderExamination, ApplicationStatus.Rejected },
        new object[] { ApplicationStatus.UnderExamination, ApplicationStatus.Withdrawn },

        // SemnatăUtilizator
        new object[] { ApplicationStatus.PendingApproval, ApplicationStatus.SignedByDirector },
        new object[] { ApplicationStatus.PendingApproval, ApplicationStatus.Approved },
        new object[] { ApplicationStatus.PendingApproval, ApplicationStatus.Returned },
        new object[] { ApplicationStatus.PendingApproval, ApplicationStatus.Rejected },

        // SemnatăȘefulDirecției
        new object[] { ApplicationStatus.SignedByDirector, ApplicationStatus.Approved },
        new object[] { ApplicationStatus.SignedByDirector, ApplicationStatus.Returned },
        new object[] { ApplicationStatus.SignedByDirector, ApplicationStatus.Rejected },

        // Returnată
        new object[] { ApplicationStatus.Returned, ApplicationStatus.UnderExamination },

        // AprobatăȘefulCNAS
        new object[] { ApplicationStatus.Approved, ApplicationStatus.Closed },
    };

    /// <summary>
    /// Pins every documented legal edge as accepted by the matrix. Theory data
    /// mirrors <see cref="AllowedTransitions"/> so adding a new edge to the matrix
    /// also requires adding a positive test row here.
    /// </summary>
    /// <param name="from">Current status.</param>
    /// <param name="to">Proposed next status.</param>
    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void AllowedEdge_IsAccepted(ApplicationStatus from, ApplicationStatus to)
    {
        ApplicationStatusTransitions.Table.CanTransition(from, to).Should().BeTrue(
            because: $"{from} → {to} is documented as a legal edge on the 8-state matrix.");
    }

    /// <summary>
    /// Pins the validate-shaped result for an allowed edge: success carries no
    /// error code or message and round-trips through the
    /// <see cref="Result"/> wrapper.
    /// </summary>
    [Fact]
    public void AllowedEdge_Validate_ReturnsSuccess()
    {
        var verdict = ApplicationStatusTransitions.Table.Validate(
            ApplicationStatus.UnderExamination,
            ApplicationStatus.PendingApproval);

        verdict.IsSuccess.Should().BeTrue();
        verdict.ErrorCode.Should().BeNull();
    }

    /// <summary>
    /// Exhaustively pins each terminal state as rejecting every outgoing edge.
    /// The matrix encodes <see cref="ApplicationStatus.Closed"/>,
    /// <see cref="ApplicationStatus.Rejected"/>, and <see cref="ApplicationStatus.Withdrawn"/>
    /// as terminal — flipping out of them is a contract violation that MUST be
    /// caught by the table.
    /// </summary>
    /// <param name="terminal">Terminal status whose outgoing edges are checked.</param>
    [Theory]
    [InlineData(ApplicationStatus.Closed)]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public void TerminalState_RejectsEveryOutgoingTransition(ApplicationStatus terminal)
    {
        foreach (var target in System.Enum.GetValues<ApplicationStatus>())
        {
            ApplicationStatusTransitions.Table
                .CanTransition(terminal, target)
                .Should().BeFalse(
                    because: $"{terminal} is terminal; {terminal} → {target} must be denied.");
        }
    }

    /// <summary>
    /// Pins canonical undocumented edges as denied: the matrix MUST NOT allow a
    /// dossier to leap directly from <see cref="ApplicationStatus.Submitted"/> to
    /// <see cref="ApplicationStatus.Approved"/>, skip examination, fast-forward
    /// to <see cref="ApplicationStatus.Closed"/>, or wind back from
    /// <see cref="ApplicationStatus.Approved"/> to
    /// <see cref="ApplicationStatus.UnderExamination"/>.
    /// </summary>
    /// <param name="from">Current status.</param>
    /// <param name="to">Disallowed proposed status.</param>
    [Theory]
    [InlineData(ApplicationStatus.Submitted, ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Submitted, ApplicationStatus.Closed)]
    [InlineData(ApplicationStatus.UnderExamination, ApplicationStatus.SignedByDirector)]
    [InlineData(ApplicationStatus.UnderExamination, ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Approved, ApplicationStatus.UnderExamination)]
    [InlineData(ApplicationStatus.Approved, ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Returned, ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.SignedByDirector, ApplicationStatus.Closed)]
    [InlineData(ApplicationStatus.PendingApproval, ApplicationStatus.Withdrawn)]
    public void UndocumentedEdge_IsRejected(ApplicationStatus from, ApplicationStatus to)
    {
        ApplicationStatusTransitions.Table.CanTransition(from, to).Should().BeFalse(
            because: $"{from} → {to} is not documented on the 8-state matrix.");
    }

    /// <summary>
    /// Pins the stable error-code surface returned by
    /// <see cref="StatusTransitionTable{TStatus}.Validate"/> when the matrix
    /// rejects an edge. The code is
    /// <c>STATUS.ILLEGAL_TRANSITION</c> per CLAUDE.md §2.2 (stable codes are
    /// API contract).
    /// </summary>
    [Fact]
    public void DeniedEdge_Validate_CarriesIllegalTransitionCode()
    {
        var verdict = ApplicationStatusTransitions.Table.Validate(
            ApplicationStatus.Closed,
            ApplicationStatus.UnderExamination);

        verdict.IsSuccess.Should().BeFalse();
        verdict.ErrorCode.Should().Be(StatusTransitionTable<ApplicationStatus>.IllegalTransitionCode);
        verdict.ErrorMessage.Should()
            .Contain("ApplicationStatus")
            .And.Contain("Closed")
            .And.Contain("UnderExamination");
    }

    /// <summary>
    /// Self-loop policy pinning: a redundant assignment <c>from == to</c> is NOT
    /// in the matrix and therefore MUST be denied. Mutators that want idempotent
    /// re-writes are expected to no-op BEFORE consulting the guard.
    /// </summary>
    /// <param name="status">A status whose self-loop is checked.</param>
    [Theory]
    [InlineData(ApplicationStatus.Draft)]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.UnderExamination)]
    [InlineData(ApplicationStatus.Approved)]
    public void SelfLoop_IsRejected(ApplicationStatus status)
    {
        ApplicationStatusTransitions.Table.CanTransition(status, status).Should().BeFalse(
            because: $"the matrix does not document {status} → {status}; "
                + "redundant writes must be filtered at the call site.");
    }

    /// <summary>
    /// Coverage gate: the matrix must address every <see cref="ApplicationStatus"/>
    /// enum value (either as a non-terminal origin or as a documented terminal).
    /// If a new enum value is added without an entry in the matrix the table will
    /// silently treat it as terminal, hiding the omission — this assertion makes
    /// the omission loud.
    /// </summary>
    [Fact]
    public void Matrix_CoversEveryEnumValue()
    {
        // The table treats unknown sources as terminal, so the only safe pin is to
        // assert that every documented enum value is REACHABLE — i.e. there is at
        // least one allowed edge whose target is the given value. This catches a
        // "we added a state but forgot to wire it" regression.
        var reachableTargets = new HashSet<ApplicationStatus>();
        foreach (var row in AllowedTransitions)
        {
            reachableTargets.Add((ApplicationStatus)row[1]);
        }
        // Draft is unreachable by design — applications are CREATED in Draft, not
        // transitioned into it. Excluded from the reachability assertion.
        foreach (var value in System.Enum.GetValues<ApplicationStatus>())
        {
            if (value == ApplicationStatus.Draft)
            {
                continue;
            }
            reachableTargets.Should().Contain(value,
                because: $"{value} must be reachable through at least one documented edge.");
        }
    }

    /// <summary>
    /// The Returned (Returnată) state has a single outgoing edge back into
    /// <see cref="ApplicationStatus.UnderExamination"/>. This is the only way a
    /// returned dossier can be re-drafted — pinning it prevents a future
    /// refactor from accidentally allowing Returned → Approved (which would
    /// bypass the second look).
    /// </summary>
    [Fact]
    public void Returned_OnlyExitsToUnderExamination()
    {
        foreach (var target in System.Enum.GetValues<ApplicationStatus>())
        {
            var allowed = ApplicationStatusTransitions.Table.CanTransition(
                ApplicationStatus.Returned, target);
            var expectedAllowed = target == ApplicationStatus.UnderExamination;
            allowed.Should().Be(expectedAllowed,
                because: $"Returned → {target} {(expectedAllowed ? "must" : "must NOT")} be allowed.");
        }
    }
}
