using Cnas.Ps.Application.Localization;

namespace Cnas.Ps.Application.Tests.Localization;

/// <summary>
/// R0027 / TOR ARH 022 — pins the documented fallback chain on
/// <see cref="LocalizedNameResolver"/>. Each test covers one branch so a
/// regression in the policy fails its dedicated row.
/// </summary>
public sealed class LocalizedNameResolverTests
{
    private readonly LocalizedNameResolver _resolver = new();

    /// <summary>
    /// Happy path — the requested culture's per-locale column is populated and
    /// MUST win over every fallback.
    /// </summary>
    [Theory]
    [InlineData("ro", "RoCol")]
    [InlineData("ru", "RuCol")]
    [InlineData("en", "EnCol")]
    [InlineData("ro-MD", "RoCol")] // BCP-47 region tag — two-letter prefix wins.
    [InlineData("RU", "RuCol")]    // Case-insensitive.
    public void Resolve_WhenPerCultureColumnPopulated_ReturnsIt(string culture, string expected)
    {
        var actual = _resolver.Resolve(
            baseName: "Base",
            nameRo: "RoCol",
            nameRu: "RuCol",
            nameEn: "EnCol",
            culture: culture);

        actual.Should().Be(expected);
    }

    /// <summary>
    /// Step 2 — when the requested culture is RU but the RU column is null, the
    /// resolver falls back to RO (not EN — Romanian is the project default).
    /// </summary>
    [Fact]
    public void Resolve_WhenRequestedCultureMissing_FallsBackToRomanian()
    {
        var actual = _resolver.Resolve(
            baseName: "Base",
            nameRo: "Roman",
            nameRu: null,
            nameEn: "English",
            culture: "ru");

        actual.Should().Be("Roman");
    }

    /// <summary>
    /// Step 3 — when RU is requested, both RU and RO are null, EN is the
    /// next fallback before the base name.
    /// </summary>
    [Fact]
    public void Resolve_WhenRoAndRequestedCultureMissing_FallsBackToEnglish()
    {
        var actual = _resolver.Resolve(
            baseName: "Base",
            nameRo: null,
            nameRu: null,
            nameEn: "English",
            culture: "ru");

        actual.Should().Be("English");
    }

    /// <summary>
    /// Step 4 — every per-locale column is null/whitespace; the resolver MUST
    /// surface the entity's required <c>baseName</c> so legacy rows keep
    /// rendering a label.
    /// </summary>
    [Fact]
    public void Resolve_WhenAllLocaleColumnsMissing_FallsBackToBaseName()
    {
        var actual = _resolver.Resolve(
            baseName: "Legacy Base Name",
            nameRo: null,
            nameRu: "   ",
            nameEn: string.Empty,
            culture: "ru");

        actual.Should().Be("Legacy Base Name");
    }

    /// <summary>
    /// Defensive — degenerate input where every input is null/whitespace. The
    /// resolver MUST return an empty string (never null) so callers' null-safety
    /// story stays uniform.
    /// </summary>
    [Fact]
    public void Resolve_WhenEverythingMissing_ReturnsEmptyString()
    {
        var actual = _resolver.Resolve(null, null, null, null, "ro");

        actual.Should().Be(string.Empty);
    }

    /// <summary>
    /// Unknown culture tag — falls through step 1 directly to RO.
    /// </summary>
    [Fact]
    public void Resolve_WhenCultureUnknown_StartsAtRomanianFallback()
    {
        var actual = _resolver.Resolve(
            baseName: "Base",
            nameRo: "Roman",
            nameRu: "Russian",
            nameEn: "English",
            culture: "??-malformed");

        actual.Should().Be("Roman");
    }

    /// <summary>
    /// Null / empty culture argument — same as "unknown culture" branch.
    /// </summary>
    [Fact]
    public void Resolve_WhenCultureNullOrEmpty_StartsAtRomanianFallback()
    {
        var nullCulture = _resolver.Resolve("Base", "Roman", "Russian", "English", null);
        var emptyCulture = _resolver.Resolve("Base", "Roman", "Russian", "English", string.Empty);

        nullCulture.Should().Be("Roman");
        emptyCulture.Should().Be("Roman");
    }
}
