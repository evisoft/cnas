using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2505 / TOR PIR 030-033 — unit tests for the change-management input validators.
/// </summary>
public sealed class ChangeManagementInputValidatorTests
{
    /// <summary>Helper — builds a fully populated valid create input.</summary>
    private static ChangeRequestCreateInputDto NewValid()
        => new(
            Title: "Patch auth library",
            Description: "Upgrade the in-house authentication library to address a security advisory.",
            Kind: "Normal",
            Risk: "Medium",
            ImpactedSystems: "auth-api, web-portal",
            RollbackPlan: "Re-deploy previous container tag and restore the prior signing key from the secrets vault.",
            RelatedMaintenanceWindowSqid: null);

    [Fact]
    public void Create_AllValid_Passes()
    {
        var v = new ChangeRequestCreateInputValidator();

        var result = v.Validate(NewValid());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Create_RollbackPlanTooShort_Fails()
    {
        var v = new ChangeRequestCreateInputValidator();
        var dto = NewValid() with { RollbackPlan = "Revert." };

        var result = v.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Contain("50 characters or more");
    }

    [Fact]
    public void Create_InvalidKindEnum_Fails()
    {
        var v = new ChangeRequestCreateInputValidator();
        var dto = NewValid() with { Kind = "Routine" };

        var result = v.Validate(dto);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rollback_TooShortReason_Fails()
    {
        var v = new ChangeRequestRollbackInputValidator();
        var dto = new ChangeRequestRollbackInputDto(Reason: "x");

        var result = v.Validate(dto);

        result.IsValid.Should().BeFalse();
    }
}
