using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0524 / TOR CF 03.06 — FluentValidation rules for
/// <see cref="SavedSearchShareInputValidator"/>. Exercises the SharingScope parse,
/// the group-code regex, and the cross-field invariant between scope and group code.
/// </summary>
public class SavedSearchShareInputValidatorTests
{
    private static SavedSearchShareInputValidator NewValidator() => new();

    [Fact]
    public void Valid_PrivateScope_NullGroupCode_Passes()
    {
        var v = NewValidator();
        var r = v.Validate(new SavedSearchShareInput(nameof(SavedSearchSharingScope.Private), null));
        r.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Valid_SharedScope_NullGroupCode_Passes()
    {
        var v = NewValidator();
        var r = v.Validate(new SavedSearchShareInput(nameof(SavedSearchSharingScope.Shared), null));
        r.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Valid_GroupScope_WithGroupCode_Passes()
    {
        var v = NewValidator();
        var r = v.Validate(new SavedSearchShareInput(nameof(SavedSearchSharingScope.Group), "pensions.examiners"));
        r.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Group_Scope_With_Null_GroupCode_Rejected()
    {
        // R0524: SharingScope=Group MUST carry a non-empty SharedWithGroupCode.
        var v = NewValidator();
        var r = v.Validate(new SavedSearchShareInput(nameof(SavedSearchSharingScope.Group), null));
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName == nameof(SavedSearchShareInput.SharedWithGroupCode));
    }

    [Fact]
    public void Private_Scope_With_NonNull_GroupCode_Rejected()
    {
        // R0524: Private MUST have null SharedWithGroupCode (the column is meaningless).
        var v = NewValidator();
        var r = v.Validate(new SavedSearchShareInput(nameof(SavedSearchSharingScope.Private), "pensions.examiners"));
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName == nameof(SavedSearchShareInput.SharedWithGroupCode));
    }

    [Fact]
    public void Shared_Scope_With_NonNull_GroupCode_Rejected()
    {
        var v = NewValidator();
        var r = v.Validate(new SavedSearchShareInput(nameof(SavedSearchSharingScope.Shared), "pensions.examiners"));
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName == nameof(SavedSearchShareInput.SharedWithGroupCode));
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("private")]   // case-sensitive enum parse
    [InlineData("")]
    [InlineData(" ")]
    public void Unknown_SharingScope_String_Rejected(string scope)
    {
        var v = NewValidator();
        var r = v.Validate(new SavedSearchShareInput(scope, null));
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName == nameof(SavedSearchShareInput.SharingScope));
    }

    [Theory]
    [InlineData("Pensions.Examiners")] // uppercase rejected
    [InlineData("1leading-digit")]     // must start with a letter
    [InlineData("-leading-dash")]
    [InlineData("with space")]
    [InlineData("with/slash")]
    [InlineData("a")]                  // too short (< 2 chars)
    public void Invalid_GroupCode_Format_Rejected(string code)
    {
        var v = NewValidator();
        var r = v.Validate(new SavedSearchShareInput(nameof(SavedSearchSharingScope.Group), code));
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName == nameof(SavedSearchShareInput.SharedWithGroupCode));
    }
}
