using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

public class ServicePassportInputValidatorTests
{
    private static ServicePassportInput Valid() => new(
        Id: null,
        Code: "SP-001-BIRTH",
        NameRo: "Indemnizație la nașterea copilului",
        NameEn: "Birth grant",
        NameRu: "Пособие при рождении",
        DescriptionRo: "Indemnizație unică la nașterea copilului.",
        FormSchemaJson: "{}",
        WorkflowCode: "WF-BIRTH-001",
        MaxProcessingDays: 30,
        IsEnabled: true,
        IsProactive: false,
        DecisionRulesJson: "{}");

    private readonly ServicePassportInputValidator _validator = new();

    [Fact]
    public void Valid_Input_PassesAllRules()
    {
        var result = _validator.TestValidate(Valid());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Code_Empty_Fails()
    {
        var input = Valid() with { Code = "" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Code_TooLong_Fails()
    {
        var input = Valid() with { Code = new string('A', 65) };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Code_Lowercase_Fails()
    {
        var input = Valid() with { Code = "sp-001-birth" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Code_WithSpace_Fails()
    {
        var input = Valid() with { Code = "SP 001 BIRTH" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Code_AllowedSymbols_Pass()
    {
        var input = Valid() with { Code = "SP-001_BIRTH-V2" };
        _validator.TestValidate(input).ShouldNotHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void NameRo_Empty_Fails()
    {
        var input = Valid() with { NameRo = "" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.NameRo);
    }

    [Fact]
    public void NameRo_TooLong_Fails()
    {
        var input = Valid() with { NameRo = new string('x', 257) };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.NameRo);
    }

    [Fact]
    public void DescriptionRo_Empty_Fails()
    {
        var input = Valid() with { DescriptionRo = "" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.DescriptionRo);
    }

    [Fact]
    public void WorkflowCode_Empty_Fails()
    {
        var input = Valid() with { WorkflowCode = "" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.WorkflowCode);
    }

    [Fact]
    public void WorkflowCode_TooLong_Fails()
    {
        var input = Valid() with { WorkflowCode = new string('w', 65) };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.WorkflowCode);
    }

    [Fact]
    public void MaxProcessingDays_Zero_Fails()
    {
        var input = Valid() with { MaxProcessingDays = 0 };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.MaxProcessingDays);
    }

    [Fact]
    public void MaxProcessingDays_TooLarge_Fails()
    {
        var input = Valid() with { MaxProcessingDays = 400 };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.MaxProcessingDays);
    }

    [Fact]
    public void MaxProcessingDays_BoundaryValues_Pass()
    {
        _validator.TestValidate(Valid() with { MaxProcessingDays = 1 })
            .ShouldNotHaveValidationErrorFor(x => x.MaxProcessingDays);
        _validator.TestValidate(Valid() with { MaxProcessingDays = 365 })
            .ShouldNotHaveValidationErrorFor(x => x.MaxProcessingDays);
    }

    [Fact]
    public void FormSchemaJson_Empty_Fails()
    {
        var input = Valid() with { FormSchemaJson = "" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.FormSchemaJson);
    }
}
