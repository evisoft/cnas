using System.Text.RegularExpressions;
using Cnas.Ps.Application.Search;

namespace Cnas.Ps.Application.Tests.Search;

/// <summary>
/// Unit tests for <see cref="WildcardMask"/>. R0164 / UI 012 / CF 03.02 — the helper
/// translates a user-entered query (Windows file-mask convention: <c>*</c> = any
/// sequence) into a Postgres LIKE pattern and into an equivalent <see cref="Regex"/>
/// for the InMemory test provider's fallback path.
/// </summary>
/// <remarks>
/// <para>
/// Two compatibility checkpoints the tests pin:
/// </para>
/// <list type="bullet">
///   <item><b>R0162 substring behaviour</b> — when the input contains no <c>*</c>, the
///   helper wraps with implicit <c>%...%</c> so existing searches keep behaving like
///   <c>Contains</c>. Without this, R0164 would be a breaking change.</item>
///   <item><b>SQL safety</b> — LIKE wildcards (<c>%</c>, <c>_</c>) and the backslash
///   escape character typed verbatim by a user must be escaped so they cannot widen
///   the result set (e.g. <c>"100%"</c> matching every prefix). This is not a SQL
///   injection vector by itself (parameters still travel through EF) but it's the
///   semantic contract callers rely on.</item>
/// </list>
/// </remarks>
public class WildcardMaskTests
{
    // ─────────────────────── ToLikePattern ───────────────────────

    /// <summary>Null and whitespace-only input short-circuits to <see cref="string.Empty"/>.</summary>
    [Fact]
    public void ToLikePattern_NullOrWhitespace_ReturnsEmpty()
    {
        WildcardMask.ToLikePattern(null).Should().Be(string.Empty);
        WildcardMask.ToLikePattern(string.Empty).Should().Be(string.Empty);
        WildcardMask.ToLikePattern("   ").Should().Be(string.Empty);
    }

    /// <summary>
    /// Backward compatibility with R0162: input without <c>*</c> wraps to <c>%input%</c>
    /// so the helper drops into the established Contains-style substring search.
    /// </summary>
    [Fact]
    public void ToLikePattern_PlainText_WrapsImplicitlyWithPercent()
    {
        WildcardMask.ToLikePattern("popescu").Should().Be("%popescu%");
    }

    /// <summary>
    /// <c>*ESCU</c> — Windows file-mask convention for "ends with ESCU". The <c>*</c>
    /// becomes <c>%</c> and the right edge is anchored (no implicit trailing <c>%</c>).
    /// </summary>
    [Fact]
    public void ToLikePattern_StarPrefix_TranslatesToPercent()
    {
        WildcardMask.ToLikePattern("*ESCU").Should().Be("%ESCU");
    }

    /// <summary>
    /// <c>ASIGUR*</c> — "starts with ASIGUR". The left edge is anchored (no implicit
    /// leading <c>%</c>) and the trailing <c>*</c> becomes <c>%</c>.
    /// </summary>
    [Fact]
    public void ToLikePattern_StarSuffix_TranslatesToPercent()
    {
        WildcardMask.ToLikePattern("ASIGUR*").Should().Be("ASIGUR%");
    }

    /// <summary>
    /// <c>*ASIGUR*</c> — "contains ASIGUR". Both <c>*</c> become <c>%</c>; no implicit
    /// wrap (the user supplied explicit wildcards). Functionally identical to the plain
    /// <c>ASIGUR</c> substring path but the helper still routes through the explicit
    /// branch for predictability.
    /// </summary>
    [Fact]
    public void ToLikePattern_StarBothSides_NoImplicitWrap()
    {
        WildcardMask.ToLikePattern("*ASIGUR*").Should().Be("%ASIGUR%");
    }

    /// <summary>Multiple internal <c>*</c>s each translate to <c>%</c>.</summary>
    [Fact]
    public void ToLikePattern_MultipleStars_AllTranslate()
    {
        WildcardMask.ToLikePattern("A*BC*D").Should().Be("A%BC%D");
    }

    /// <summary>
    /// A literal <c>%</c> typed by the user MUST be escaped (Postgres LIKE default
    /// escape is <c>\</c>); otherwise <c>"100%"</c> would match every 4-char prefix.
    /// </summary>
    [Fact]
    public void ToLikePattern_LiteralPercent_IsEscaped()
    {
        WildcardMask.ToLikePattern("100%").Should().Be("%100\\%%");
    }

    /// <summary>
    /// Underscore is LIKE's single-char wildcard — must be escaped when typed verbatim.
    /// </summary>
    [Fact]
    public void ToLikePattern_LiteralUnderscore_IsEscaped()
    {
        WildcardMask.ToLikePattern("a_b").Should().Be("%a\\_b%");
    }

    /// <summary>
    /// Backslash is the LIKE escape character itself — must be doubled so it is
    /// preserved literally rather than swallowed by Postgres' LIKE parser.
    /// </summary>
    [Fact]
    public void ToLikePattern_LiteralBackslash_IsEscaped()
    {
        WildcardMask.ToLikePattern("path\\file").Should().Be("%path\\\\file%");
    }

    /// <summary>Leading and trailing whitespace are trimmed before pattern construction.</summary>
    [Fact]
    public void ToLikePattern_TrimsLeadingAndTrailingWhitespace()
    {
        WildcardMask.ToLikePattern("  popescu  ").Should().Be("%popescu%");
        WildcardMask.ToLikePattern("  *escu  ").Should().Be("%escu");
    }

    // ─────────────────────── ToRegex ───────────────────────

    /// <summary>
    /// Plain text → substring regex (unanchored). Case insensitivity is built into
    /// the regex options so callers don't repeat <see cref="StringComparison"/>.
    /// </summary>
    [Fact]
    public void Regex_PlainText_MatchesAsSubstring_CaseInsensitive()
    {
        var regex = WildcardMask.ToRegex("escu");

        regex.IsMatch("Popescu").Should().BeTrue();
        regex.IsMatch("POPESCU").Should().BeTrue();
        regex.IsMatch("Ion").Should().BeFalse();
    }

    /// <summary>
    /// <c>*escu</c> → anchored "ends with escu" — the regex must NOT match
    /// <c>"Popescu Ion"</c> because the suffix is not at end-of-string.
    /// </summary>
    [Fact]
    public void Regex_StarPrefix_MatchesEndsWith()
    {
        var regex = WildcardMask.ToRegex("*escu");

        regex.IsMatch("Popescu").Should().BeTrue();
        regex.IsMatch("Popescu Ion").Should().BeFalse();
    }

    /// <summary>
    /// <c>Asigur*</c> → anchored "starts with Asigur" — the regex must NOT match
    /// <c>"Re-asigurări"</c> because the prefix is not at start-of-string. Also
    /// confirms diacritic-bearing input (the regex doesn't fold; that's the
    /// caller's job — but the regex still works on diacritic chars).
    /// </summary>
    [Fact]
    public void Regex_StarSuffix_MatchesStartsWith()
    {
        var regex = WildcardMask.ToRegex("Asigur*");

        regex.IsMatch("Asigurări").Should().BeTrue();
        regex.IsMatch("Re-asigurări").Should().BeFalse();
    }

    /// <summary>
    /// <c>*asigur*</c> → unanchored "contains asigur" — must match anywhere.
    /// </summary>
    [Fact]
    public void Regex_StarBothSides_MatchesContains()
    {
        var regex = WildcardMask.ToRegex("*asigur*");

        regex.IsMatch("Re-asigurări").Should().BeTrue();
        regex.IsMatch("Asigurări").Should().BeTrue();
        regex.IsMatch("Pensii").Should().BeFalse();
    }

    /// <summary>
    /// Literal <c>%</c> in the user query: the regex must match the literal character
    /// (and only that), not behave like a wildcard. <c>%</c> is also a regex
    /// meta-character only inside groups, so we don't need explicit escaping — the
    /// match-as-literal behaviour comes for free.
    /// </summary>
    [Fact]
    public void Regex_LiteralPercent_MatchesLiteral()
    {
        var regex = WildcardMask.ToRegex("100%");

        regex.IsMatch("100%").Should().BeTrue();
        regex.IsMatch("100Z").Should().BeFalse();
    }
}
