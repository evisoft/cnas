using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0211 / TOR UI 003 — validation rules for
/// <see cref="ProfileLanguageInputValidator"/>. The validator gates the thin
/// payload accepted by <c>PUT /api/profile/language</c>: language must be in the
/// allow-list <c>ro</c> / <c>en</c> / <c>ru</c>.
/// </summary>
public sealed class ProfileLanguageInputValidatorTests
{
    [Theory]
    [InlineData("ro")]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("RO")] // Case-insensitive at the boundary.
    [InlineData("Ru")]
    public void Validate_AllowedLanguage_Succeeds(string language)
    {
        var sut = new ProfileLanguageInputValidator();

        var result = sut.Validate(new ProfileLanguageInputDto(language));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("fr")] // outside the allow-list
    [InlineData("xx")]
    [InlineData("english")]
    public void Validate_DisallowedLanguage_Fails(string language)
    {
        var sut = new ProfileLanguageInputValidator();

        var result = sut.Validate(new ProfileLanguageInputDto(language));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ProfileLanguageInputDto.Language));
    }
}
