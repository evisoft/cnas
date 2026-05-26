using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2163 / INT 004 — TDD coverage for <see cref="NewServiceProvisionInputValidator"/>.
/// The validator gates the schema-driven new-service provisioning surface
/// (<c>POST /api/admin/service-catalog/provision</c>) so a malformed JSON-schema or a
/// missing workflow code is rejected at the boundary before the service ever spawns a
/// <c>ServicePassport</c> row.
/// </summary>
public sealed class NewServiceProvisionInputValidatorTests
{
    /// <summary>Shared static schema collection — avoids CA1861 inline-array allocations.</summary>
    private static readonly IReadOnlyList<string> SampleSchemes = ["CAEM", "CUATM"];

    /// <summary>Builds a fully-valid sample input.</summary>
    /// <returns>A schema-driven provisioning request that should pass every rule.</returns>
    private static NewServiceProvisionInputDto Valid() => new(
        Code: "SP-NEW-INT004",
        NameRo: "Serviciu nou INT 004",
        NameEn: "New INT 004 service",
        NameRu: "Новый сервис INT 004",
        DescriptionRo: "Descriere serviciu provisioned via INT 004.",
        WorkflowCode: "WF-INT004",
        MaxProcessingDays: 30,
        FormSchemaJson: "{\"type\":\"object\",\"properties\":{\"idnp\":{\"type\":\"string\"}}}",
        DecisionRulesJson: "{}",
        ClassifierSchemes: SampleSchemes,
        IsEnabled: true,
        IsProactive: false);

    private readonly NewServiceProvisionInputValidator _validator = new();

    [Fact]
    public void Valid_Input_PassesAllRules()
    {
        var result = _validator.TestValidate(Valid());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Code_TooShort_Fails()
    {
        var input = Valid() with { Code = "AB" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Code_TooLong_Fails()
    {
        var input = Valid() with { Code = new string('A', 33) };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Code_Lowercase_Fails()
    {
        var input = Valid() with { Code = "sp-new-int004" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void NameRo_TooShort_Fails()
    {
        var input = Valid() with { NameRo = "ab" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.NameRo);
    }

    [Fact]
    public void NameRo_TooLong_Fails()
    {
        var input = Valid() with { NameRo = new string('x', 257) };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.NameRo);
    }

    [Fact]
    public void WorkflowCode_Empty_Fails()
    {
        var input = Valid() with { WorkflowCode = "" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.WorkflowCode);
    }

    [Fact]
    public void FormSchemaJson_Empty_Fails()
    {
        var input = Valid() with { FormSchemaJson = "" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.FormSchemaJson);
    }

    [Fact]
    public void FormSchemaJson_NoProperties_Fails()
    {
        // Schema-driven INT 004 contract — the new service must declare at least one
        // form field. A JSON object with no "properties" key (or an empty one) fails.
        var input = Valid() with { FormSchemaJson = "{\"type\":\"object\"}" };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.FormSchemaJson);
    }

    [Fact]
    public void DecisionRulesJson_Null_Fails()
    {
        var input = Valid() with { DecisionRulesJson = null! };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.DecisionRulesJson);
    }

    [Fact]
    public void MaxProcessingDays_Zero_Fails()
    {
        var input = Valid() with { MaxProcessingDays = 0 };
        _validator.TestValidate(input).ShouldHaveValidationErrorFor(x => x.MaxProcessingDays);
    }
}
