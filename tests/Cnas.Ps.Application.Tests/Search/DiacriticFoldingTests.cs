using Cnas.Ps.Application.Search;

namespace Cnas.Ps.Application.Tests.Search;

/// <summary>
/// Unit tests for <see cref="DiacriticFolding"/>. The helper folds Romanian (and other
/// Latin) diacritics off a string so that the InMemory test provider's search fallback
/// produces the same set of matches as Postgres + the <c>unaccent</c> extension. The
/// folder also runs on the SQL <c>LIKE</c> pattern so a query string containing diacritics
/// matches the folded form of the column.
/// </summary>
public class DiacriticFoldingTests
{
    /// <summary>Null and empty inputs short-circuit to <see cref="string.Empty"/>.</summary>
    [Fact]
    public void Fold_NullOrEmpty_ReturnsEmpty()
    {
        DiacriticFolding.Fold(null).Should().Be(string.Empty);
        DiacriticFolding.Fold(string.Empty).Should().Be(string.Empty);
    }

    /// <summary>ASCII-only text round-trips unchanged.</summary>
    [Fact]
    public void Fold_NoDiacritics_RoundTrips()
    {
        DiacriticFolding.Fold("Popescu").Should().Be("Popescu");
        DiacriticFolding.Fold("alpha-001 SRL").Should().Be("alpha-001 SRL");
    }

    /// <summary>
    /// Romanian diacritics (<c>ăâîșțĂÂÎȘȚ</c>) and generic Latin diacritics
    /// (<c>éáí</c>) all fold to their ASCII bases. Both the modern combining-comma
    /// form and the cedilla legacy form for <c>ș</c>/<c>ț</c> are covered by
    /// <see cref="Fold_CedillaLegacyForms_AreStripped"/>.
    /// </summary>
    [Fact]
    public void Fold_RomanianDiacritics_AreStripped()
    {
        DiacriticFolding.Fold("Ștefan").Should().Be("Stefan");
        DiacriticFolding.Fold("Ștefan Cărbune").Should().Be("Stefan Carbune");
        DiacriticFolding.Fold("Popéscu").Should().Be("Popescu");
        DiacriticFolding.Fold("Întreprinderea Țăranu").Should().Be("Intreprinderea Taranu");
    }

    /// <summary>
    /// Legacy CEDILLA forms (<c>ş</c>/<c>ţ</c>, U+015F / U+0163) also fold to ASCII.
    /// Romanian text predating the 2014 Unicode revision frequently still uses these
    /// codepoints, so search must tolerate both.
    /// </summary>
    [Fact]
    public void Fold_CedillaLegacyForms_AreStripped()
    {
        DiacriticFolding.Fold("Ştefan").Should().Be("Stefan"); // Ş (cedilla)
        DiacriticFolding.Fold("ţara").Should().Be("tara");     // ţ (cedilla)
    }

    /// <summary>
    /// Cyrillic letters carry no Unicode combining marks for their typical surface
    /// form, so the NFD pass leaves them untouched. CNAS data carries Russian /
    /// Moldovan-Cyrillic strings in some legacy registries; folding must not corrupt
    /// them.
    /// </summary>
    [Fact]
    public void Fold_CyrillicText_IsUnchanged()
    {
        DiacriticFolding.Fold("Иван Петров").Should().Be("Иван Петров");
    }

    /// <summary>
    /// Folding is case-PRESERVING. Case insensitivity is the caller's responsibility
    /// (either <c>ILIKE</c> server-side or <c>StringComparison.OrdinalIgnoreCase</c>
    /// in memory). Keeping the helper case-preserving makes it usable for any
    /// downstream comparison strategy.
    /// </summary>
    [Fact]
    public void Fold_PreservesCase()
    {
        DiacriticFolding.Fold("Ștefan").Should().Be("Stefan");
        DiacriticFolding.Fold("ȘTEFAN").Should().Be("STEFAN");
        DiacriticFolding.Fold("ștefan").Should().Be("stefan");
    }

    /// <summary>
    /// Mixed Latin + Romanian text fold cleanly. Tests the full Romanian alphabet
    /// (ă â î ș ț) in a realistic country/name string.
    /// </summary>
    [Fact]
    public void Fold_MixedLatinAndRomanian()
    {
        DiacriticFolding.Fold("Țară Românească").Should().Be("Tara Romaneasca");
    }

    /// <summary>Digits, whitespace, and ASCII punctuation pass through unchanged.</summary>
    [Fact]
    public void Fold_DigitsAndPunctuation_PassThrough()
    {
        DiacriticFolding.Fold("12345").Should().Be("12345");
        DiacriticFolding.Fold("a.b-c, d_e!").Should().Be("a.b-c, d_e!");
    }
}
