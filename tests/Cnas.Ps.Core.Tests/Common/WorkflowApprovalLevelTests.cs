using Cnas.Ps.Core.Common;
using FluentAssertions;
using Xunit;

namespace Cnas.Ps.Core.Tests.Common;

/// <summary>
/// R0936 / TOR §10.1 — pinning test for the
/// <see cref="WorkflowApprovalLevel"/> enum. The enum encodes the 3-level
/// approval chain (Utilizator CNAS → Șeful direcției → Șeful CNAS); the test
/// pins the canonical vocabulary so a future refactor cannot silently drop the
/// intermediate director step or renumber the persisted values.
/// </summary>
public sealed class WorkflowApprovalLevelTests
{
    [Fact]
    public void Enum_DeclaresThreeLevels_InAscendingOrder()
    {
        // Pin the contract: exactly three levels exist, numbered 0/1/2, in the
        // canonical chain order. The chain itself (which passport opts into
        // which depth) is configured per service in the ServicePassport — the
        // enum is the closed vocabulary the routing service references.
        ((int)WorkflowApprovalLevel.UserCnas).Should().Be(0);
        ((int)WorkflowApprovalLevel.DirectorOfDirectorate).Should().Be(1);
        ((int)WorkflowApprovalLevel.ChiefCnas).Should().Be(2);

        var values = System.Enum.GetValues<WorkflowApprovalLevel>();
        values.Should().HaveCount(3,
            "the 3-level chain must remain Utilizator CNAS → Șeful direcției → Șeful CNAS");
        values.Should().BeEquivalentTo(new[]
        {
            WorkflowApprovalLevel.UserCnas,
            WorkflowApprovalLevel.DirectorOfDirectorate,
            WorkflowApprovalLevel.ChiefCnas,
        });
    }

    [Theory]
    [InlineData(WorkflowApprovalLevel.UserCnas, "UserCnas")]
    [InlineData(WorkflowApprovalLevel.DirectorOfDirectorate, "DirectorOfDirectorate")]
    [InlineData(WorkflowApprovalLevel.ChiefCnas, "ChiefCnas")]
    public void Enum_NameStrings_ArePersistenceContract(WorkflowApprovalLevel value, string expectedName)
    {
        // The stable name strings are part of the audit log payload + the
        // ServicePassport approval-depth configuration JSON. Renaming a
        // member is a breaking change that requires a data migration.
        value.ToString().Should().Be(expectedName);
    }
}
