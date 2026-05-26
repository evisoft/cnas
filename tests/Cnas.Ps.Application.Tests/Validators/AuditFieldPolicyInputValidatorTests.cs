using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0183 / SEC 043 — FluentValidation rules for
/// <see cref="AuditFieldPolicyInputValidator"/> and its update sibling. Exercises
/// the PascalCase EntityType regex, the severity-string parse, and the
/// "RequireAnyChange ⇒ TrackedFields non-empty" safeguard.
/// </summary>
public class AuditFieldPolicyInputValidatorTests
{
    /// <summary>Reused tracked-fields seed to satisfy CA1861 (no new[] inside record ctor).</summary>
    private static readonly string[] DefaultTracked = { "DisplayName" };

    private static AuditFieldPolicyCreateInput Valid() => new(
        EntityType: "Solicitant",
        TrackedFields: DefaultTracked,
        SuppressedFields: Array.Empty<string>(),
        Severity: "Notice",
        RequireAnyChange: true,
        IsEnabled: true,
        Description: null);

    [Fact]
    public void Valid_Input_Passes()
    {
        var v = new AuditFieldPolicyInputValidator();
        var result = v.Validate(Valid());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void LowercaseFirstLetter_EntityType_Rejected()
    {
        var v = new AuditFieldPolicyInputValidator();
        var result = v.Validate(Valid() with { EntityType = "solicitant" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AuditFieldPolicyCreateInput.EntityType));
    }

    [Fact]
    public void RequireAnyChange_True_WithEmptyTracked_Rejected()
    {
        var v = new AuditFieldPolicyInputValidator();
        var result = v.Validate(Valid() with
        {
            RequireAnyChange = true,
            TrackedFields = Array.Empty<string>(),
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(AuditFieldPolicyCreateInput.TrackedFields));
    }

    [Fact]
    public void RequireAnyChange_False_AllowsEmptyTracked()
    {
        var v = new AuditFieldPolicyInputValidator();
        var result = v.Validate(Valid() with
        {
            RequireAnyChange = false,
            TrackedFields = Array.Empty<string>(),
        });
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Unknown_Severity_Rejected()
    {
        var v = new AuditFieldPolicyInputValidator();
        var result = v.Validate(Valid() with { Severity = "Loud" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(AuditFieldPolicyCreateInput.Severity));
    }

    [Theory]
    [InlineData("ab")]              // too short
    [InlineData("ABCdef-")]         // dash not allowed
    [InlineData("1Solicitant")]     // leading digit
    public void Malformed_EntityType_Rejected(string entityType)
    {
        var v = new AuditFieldPolicyInputValidator();
        var result = v.Validate(Valid() with { EntityType = entityType });
        result.IsValid.Should().BeFalse();
    }
}
