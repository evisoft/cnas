using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2506 / TOR PIR 037-040 — unit tests for the quality-risk input validators.
/// </summary>
public sealed class QualityRiskInputValidatorTests
{
    /// <summary>Helper — builds a fully populated valid create input.</summary>
    private static QualityRiskCreateInputDto NewValid()
        => new(
            RiskCode: "DATA_LOSS",
            Title: "Risk of payroll data loss during migration",
            Description: "Potential corruption of payroll data during the legacy-to-PostgreSQL migration window.",
            Category: "Technical",
            Likelihood: "Possible",
            Impact: "Major",
            OwnerSqid: "SQID-1");

    [Fact]
    public void Create_AllValid_Passes()
    {
        var v = new QualityRiskCreateInputValidator();

        var result = v.Validate(NewValid());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Create_LowerCaseRiskCode_Fails()
    {
        var v = new QualityRiskCreateInputValidator();
        var dto = NewValid() with { RiskCode = "data_loss" };

        var result = v.Validate(dto);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_InvalidLikelihood_Fails()
    {
        var v = new QualityRiskCreateInputValidator();
        var dto = NewValid() with { Likelihood = "VeryLikely" };

        var result = v.Validate(dto);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Action_TooLongDescription_Fails()
    {
        var v = new QualityRiskActionCreateInputValidator();
        var dto = new QualityRiskActionCreateInputDto(
            Description: new string('x', 2001),
            DueDate: new DateOnly(2026, 12, 31),
            AssignedToSqid: "SQID-7");

        var result = v.Validate(dto);

        result.IsValid.Should().BeFalse();
    }
}
