using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0116 + R0195 — pins the contract enforced by
/// <see cref="MLogCategoryConfigInputValidator"/>.
/// </summary>
public sealed class MLogCategoryConfigInputValidatorTests
{
    private readonly MLogCategoryConfigInputValidator _sut = new();

    private static MLogCategoryConfigInputDto NewValid() => new(
        CategoryCode: "APPLICATION.RECEIVE",
        DisplayName: "Application receive",
        IsEnabled: true,
        MinSeverity: MLogSeverityFloorDto.Notice);

    /// <summary>Happy path.</summary>
    [Fact]
    public void Valid_Input_HasNoErrors()
    {
        var result = _sut.TestValidate(NewValid());
        result.IsValid.Should().BeTrue();
    }

    /// <summary>Category code shape is enforced.</summary>
    [Theory]
    [InlineData("lowercase")]
    [InlineData("Has Spaces")]
    [InlineData("")]
    public void Invalid_CategoryCode_Fails(string code)
    {
        var input = NewValid() with { CategoryCode = code };
        var result = _sut.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.CategoryCode);
    }

    /// <summary>Display name is required.</summary>
    [Fact]
    public void Empty_DisplayName_Fails()
    {
        var input = NewValid() with { DisplayName = string.Empty };
        var result = _sut.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.DisplayName);
    }
}
