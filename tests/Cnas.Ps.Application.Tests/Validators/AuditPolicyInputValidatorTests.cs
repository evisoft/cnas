using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0182 / SEC 042 — FluentValidation rules for
/// <see cref="AuditPolicyInputValidator"/> and its update sibling. Exercises the
/// natural-key shape, the regex-compiles invariant, the severity-string parse, and
/// the suppression safeguard.
/// </summary>
public class AuditPolicyInputValidatorTests
{
    private static AuditPolicyCreateInput Valid() => new(
        Code: "solicitant.view.search",
        Module: "Solicitant",
        Screen: "Search",
        DataCategory: null,
        EventCodePattern: "^SOLICITANT\\.VIEW\\.SEARCH$",
        OverrideSeverity: null,
        SuppressAudit: false,
        ExtraRedactKeys: Array.Empty<string>(),
        Priority: 100,
        IsEnabled: true,
        Description: null);

    [Fact]
    public void Valid_Input_Passes()
    {
        var v = new AuditPolicyInputValidator();
        var result = v.Validate(Valid());
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Solicitant")]            // uppercase rejected
    [InlineData("solicitant_search")]     // underscore not allowed
    [InlineData("-leading-dash")]         // leading dash rejected
    [InlineData("a")]                     // too short (<3)
    [InlineData("ABC.def")]               // uppercase rejected
    public void Invalid_Code_Rejected(string badCode)
    {
        var v = new AuditPolicyInputValidator();
        var result = v.Validate(Valid() with { Code = badCode });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(AuditPolicyCreateInput.Code));
    }

    [Fact]
    public void Invalid_Regex_Rejected()
    {
        var v = new AuditPolicyInputValidator();
        // Unclosed character class — guaranteed to fail Regex compilation.
        var result = v.Validate(Valid() with { EventCodePattern = "[" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AuditPolicyCreateInput.EventCodePattern));
    }

    [Fact]
    public void Suppress_With_Notice_Severity_Rejected()
    {
        var v = new AuditPolicyInputValidator();
        var result = v.Validate(Valid() with
        {
            SuppressAudit = true,
            OverrideSeverity = "Notice",
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(AuditPolicyCreateInput.SuppressAudit));
    }

    [Fact]
    public void Suppress_With_Sensitive_Severity_Rejected()
    {
        var v = new AuditPolicyInputValidator();
        var result = v.Validate(Valid() with
        {
            SuppressAudit = true,
            OverrideSeverity = "Sensitive",
        });
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Suppress_With_Information_Severity_Allowed()
    {
        var v = new AuditPolicyInputValidator();
        var result = v.Validate(Valid() with
        {
            SuppressAudit = true,
            OverrideSeverity = "Information",
        });
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Suppress_With_Null_Severity_Allowed()
    {
        var v = new AuditPolicyInputValidator();
        var result = v.Validate(Valid() with
        {
            SuppressAudit = true,
            OverrideSeverity = null,
        });
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Negative_Priority_Rejected()
    {
        var v = new AuditPolicyInputValidator();
        var result = v.Validate(Valid() with { Priority = -1 });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AuditPolicyCreateInput.Priority));
    }

    [Fact]
    public void Unknown_Severity_String_Rejected()
    {
        var v = new AuditPolicyInputValidator();
        var result = v.Validate(Valid() with { OverrideSeverity = "Bogus" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(AuditPolicyCreateInput.OverrideSeverity));
    }
}
