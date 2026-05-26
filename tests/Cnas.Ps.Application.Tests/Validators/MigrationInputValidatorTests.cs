using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2430 / R2431 / R2433 / TOR M4 — unit tests for the migration input
/// validators. Exercises plan-create, plan-modify, the acknowledge-finding
/// note rules, and the findings/runs filter envelopes.
/// </summary>
public sealed class MigrationInputValidatorTests
{
    /// <summary>A canonical create envelope passes the validator.</summary>
    [Fact]
    public void PlanCreate_HappyPath_Passes()
    {
        var v = new MigrationPlanCreateInputValidator();
        var input = new MigrationPlanCreateInputDto(
            PlanCode: "LEGACY_PENSIONS_2026",
            Title: "Legacy pensions migration",
            Description: "Migrate pre-2024 pension awards from the legacy system.",
            SourceKind: "InMemoryTest",
            TargetEntityName: "Pension",
            MappingDescriptorJson: "{ \"version\": 1 }",
            BatchSize: 1000);

        var result = v.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>A PlanCode that violates the regex is refused.</summary>
    [Fact]
    public void PlanCreate_BadPlanCode_Rejected()
    {
        var v = new MigrationPlanCreateInputValidator();
        var input = new MigrationPlanCreateInputDto(
            PlanCode: "bad-code-lowercase",
            Title: "Plan",
            Description: null,
            SourceKind: "InMemoryTest",
            TargetEntityName: "Pension",
            MappingDescriptorJson: null,
            BatchSize: 1000);

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>An out-of-range BatchSize is refused.</summary>
    [Fact]
    public void PlanCreate_BatchSizeTooLow_Rejected()
    {
        var v = new MigrationPlanCreateInputValidator();
        var input = new MigrationPlanCreateInputDto(
            PlanCode: "PLAN_1",
            Title: "Plan",
            Description: null,
            SourceKind: "InMemoryTest",
            TargetEntityName: "Pension",
            MappingDescriptorJson: null,
            BatchSize: 5);

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>A malformed MappingDescriptorJson payload is refused.</summary>
    [Fact]
    public void PlanCreate_BadMappingJson_Rejected()
    {
        var v = new MigrationPlanCreateInputValidator();
        var input = new MigrationPlanCreateInputDto(
            PlanCode: "PLAN_1",
            Title: "Plan",
            Description: null,
            SourceKind: "InMemoryTest",
            TargetEntityName: "Pension",
            MappingDescriptorJson: "{ this is: not json",
            BatchSize: 100);

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>Acknowledge-finding note enforces the 3..1000 char bound.</summary>
    [Fact]
    public void FindingAck_ShortNote_Rejected()
    {
        var v = new MigrationFindingAcknowledgeInputValidator();
        var input = new MigrationFindingAcknowledgeInputDto("ab");

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>Findings filter envelope rejects out-of-range Take.</summary>
    [Fact]
    public void FindingFilter_TakeTooLarge_Rejected()
    {
        var v = new MigrationFindingFilterValidator();
        var f = new MigrationFindingFilterDto(Skip: 0, Take: 1000);

        var result = v.Validate(f);

        result.IsValid.Should().BeFalse();
    }
}
