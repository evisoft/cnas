using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2270 / TOR SEC 023-024 — unit tests for the user-group input validators.
/// Covers code regex, display-name bounds, description bounds, role-list
/// bounds, role regex, and modify/reason envelopes.
/// </summary>
public sealed class UserGroupInputValidatorTests
{
    /// <summary>Canonical create-input that satisfies every rule.</summary>
    private static UserGroupCreateInputDto Valid() => new(
        Code: "OFFICE_CHISINAU_CENTRU",
        DisplayName: "Oficiul Chișinău Centru",
        Description: "Branch office that serves citizens in central Chișinău.",
        Kind: nameof(UserGroupKind.OrganizationalUnit),
        Roles: ["CNAS_USER", "BENEFITS_EXAMINER"]);

    // ───────── Create validator ─────────

    /// <summary>R2270 — happy-path create input passes validation.</summary>
    [Fact]
    public void Create_GoodInput_Accepted()
    {
        var v = new UserGroupCreateInputValidator();

        var result = v.Validate(Valid());

        result.IsValid.Should().BeTrue();
    }

    /// <summary>R2270 — lowercase code → rejected.</summary>
    [Fact]
    public void Create_LowercaseCode_Rejected()
    {
        var v = new UserGroupCreateInputValidator();
        var input = Valid() with { Code = "office_chisinau" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.Code));
    }

    /// <summary>R2270 — code starting with a digit → rejected.</summary>
    [Fact]
    public void Create_CodeStartingWithDigit_Rejected()
    {
        var v = new UserGroupCreateInputValidator();
        var input = Valid() with { Code = "1OFFICE_CHISINAU" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.Code));
    }

    /// <summary>R2270 — code exceeding 64 chars → rejected.</summary>
    [Fact]
    public void Create_CodeTooLong_Rejected()
    {
        var v = new UserGroupCreateInputValidator();
        var input = Valid() with { Code = "A" + new string('B', 64) };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.Code));
    }

    /// <summary>R2270 — display name shorter than 3 chars → rejected.</summary>
    [Fact]
    public void Create_DisplayNameTooShort_Rejected()
    {
        var v = new UserGroupCreateInputValidator();
        var input = Valid() with { DisplayName = "Hi" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.DisplayName));
    }

    /// <summary>R2270 — description exceeding 1000 chars → rejected.</summary>
    [Fact]
    public void Create_DescriptionTooLong_Rejected()
    {
        var v = new UserGroupCreateInputValidator();
        var input = Valid() with { Description = new string('x', 1001) };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.Description));
    }

    /// <summary>R2270 — too many roles (>50) → rejected.</summary>
    [Fact]
    public void Create_TooManyRoles_Rejected()
    {
        var v = new UserGroupCreateInputValidator();
        var roles = Enumerable.Range(1, 51).Select(i => $"ROLE_{i:D2}").ToArray();
        var input = Valid() with { Roles = roles };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.Roles));
    }

    /// <summary>R2270 — role code with a lowercase prefix → rejected.</summary>
    [Fact]
    public void Create_BadRoleCode_Rejected()
    {
        var v = new UserGroupCreateInputValidator();
        var input = Valid() with { Roles = new[] { "good_ROLE" } };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.StartsWith("Roles", StringComparison.Ordinal));
    }

    // ───────── Modify validator ─────────

    /// <summary>R2270 — modify input with valid change reason passes.</summary>
    [Fact]
    public void Modify_GoodInput_Accepted()
    {
        var v = new UserGroupModifyInputValidator();
        var input = new UserGroupModifyInputDto(
            DisplayName: "New name",
            Description: null,
            Kind: null,
            Roles: null,
            ChangeReason: "Updated naming convention per org chart");

        var result = v.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>R2270 — missing change reason → rejected.</summary>
    [Fact]
    public void Modify_MissingChangeReason_Rejected()
    {
        var v = new UserGroupModifyInputValidator();
        var input = new UserGroupModifyInputDto(
            DisplayName: "New name",
            Description: null,
            Kind: null,
            Roles: null,
            ChangeReason: string.Empty);

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.ChangeReason));
    }

    // ───────── Reason validator ─────────

    /// <summary>R2270 — reason shorter than 3 chars → rejected.</summary>
    [Fact]
    public void Reason_TooShort_Rejected()
    {
        var v = new UserGroupReasonInputValidator();
        var input = new UserGroupReasonInputDto("a");

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.Reason));
    }

    /// <summary>R2270 — reason within bounds accepted.</summary>
    [Fact]
    public void Reason_GoodInput_Accepted()
    {
        var v = new UserGroupReasonInputValidator();
        var input = new UserGroupReasonInputDto("Re-organisation of branches");

        var result = v.Validate(input);

        result.IsValid.Should().BeTrue();
    }
}
