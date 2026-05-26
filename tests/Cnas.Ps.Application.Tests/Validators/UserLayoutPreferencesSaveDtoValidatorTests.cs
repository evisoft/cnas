using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0535 / CF 04.07-08 — tests for <see cref="UserLayoutPreferencesSaveDtoValidator"/>.
/// Mirror the design of <c>AttachmentUploadDtoValidatorTests</c> — one test per
/// branch of the rule set, plus a baseline "happy path" sanity check.
/// </summary>
public sealed class UserLayoutPreferencesSaveDtoValidatorTests
{
    /// <summary>Returns a canonical valid DTO that callers tweak per test.</summary>
    /// <returns>A baseline DTO that passes validation.</returns>
    private static UserLayoutPreferencesSaveDto Valid() => new(
        Grids: new Dictionary<string, GridLayoutDto>
        {
            ["solicitants"] = new(
                VisibleColumns: ["name", "idnp"],
                ColumnOrder: ["idnp", "name"],
                PageSize: 50),
        },
        DefaultPageSize: 25,
        DashboardWidgetOrder: ["tasks"]);

    [Fact]
    public void Validate_Baseline_Succeeds()
    {
        var sut = new UserLayoutPreferencesSaveDtoValidator();

        var result = sut.Validate(Valid());

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_DefaultPageSizeBelowMin_Fails()
    {
        var sut = new UserLayoutPreferencesSaveDtoValidator();
        var dto = Valid() with { DefaultPageSize = 5 };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UserLayoutPreferencesSaveDto.DefaultPageSize));
    }

    [Fact]
    public void Validate_DefaultPageSizeAboveMax_Fails()
    {
        var sut = new UserLayoutPreferencesSaveDtoValidator();
        var dto = Valid() with { DefaultPageSize = 201 };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UserLayoutPreferencesSaveDto.DefaultPageSize));
    }

    [Fact]
    public void Validate_GridKeyWithUppercaseFirstChar_Fails()
    {
        var sut = new UserLayoutPreferencesSaveDtoValidator();
        var dto = Valid() with
        {
            Grids = new Dictionary<string, GridLayoutDto>
            {
                ["Solicitants"] = new(["name"], ["name"], 25),
            },
        };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse(
            "grid keys must match ^[a-z][a-z0-9.-]{2,63}$ — uppercase first char is rejected.");
        result.Errors.Should().Contain(e =>
            e.PropertyName.StartsWith("Grids[", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_GridPageSizeOutOfRange_Fails()
    {
        var sut = new UserLayoutPreferencesSaveDtoValidator();
        var dto = Valid() with
        {
            Grids = new Dictionary<string, GridLayoutDto>
            {
                ["solicitants"] = new(["name"], ["name"], PageSize: 5),
            },
        };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName.EndsWith("PageSize", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NullPerGridLayoutValue_Fails()
    {
        var sut = new UserLayoutPreferencesSaveDtoValidator();
        var dto = Valid() with
        {
            Grids = new Dictionary<string, GridLayoutDto>
            {
                ["solicitants"] = null!,
            },
        };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
    }
}
