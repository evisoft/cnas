using System.Text;
using System.Text.RegularExpressions;

namespace Cnas.Ps.Application.Search;

/// <summary>
/// Translates a user-entered search query (UI 012 / CF 03.02 — the Windows file-mask
/// convention) to a SQL <c>LIKE</c> pattern and to an equivalent <see cref="Regex"/>
/// for the InMemory test provider's client-side fallback path. R0164.
/// </summary>
/// <remarks>
/// <para>
/// <b>Translation rules.</b>
/// </para>
/// <list type="bullet">
///   <item><c>*</c> in user input → <c>%</c> in <c>LIKE</c> / <c>.*</c> in regex
///   (greedy any-sequence).</item>
///   <item>Literal <c>LIKE</c> wildcards (<c>%</c>, <c>_</c>) typed by the user are
///   escaped via the default Postgres backslash escape — so a citizen entering
///   <c>"100%"</c> matches the literal string and not every 4-char prefix.</item>
///   <item>Backslash (<c>\</c>) is escaped to <c>\\</c> for the same reason — Postgres
///   <c>LIKE</c> would otherwise treat the user's <c>\</c> as the escape introducer.</item>
///   <item>If the input contains NO <c>*</c>, the helper IMPLICITLY wraps with
///   <c>%...%</c> (substring match, the established R0162 behaviour). If the input
///   contains at least one <c>*</c>, the explicit wildcard placement wins — no
///   implicit wrap, so <c>"ASIGUR*"</c> truly anchors the prefix.</item>
///   <item>Null or whitespace-only input returns <see cref="string.Empty"/>; callers
///   should skip the search entirely in that case (the helper is defensive but the
///   call-site <c>string.IsNullOrWhiteSpace</c> guard is the proper short-circuit).</item>
/// </list>
/// <para>
/// <b>Security.</b> This helper performs only string-level escaping; the resulting
/// pattern still flows through parameterised SQL via <c>EF.Functions.ILike</c>, so
/// SQL injection is mitigated by EF, not by this transformation. The escaping
/// prevents a different class of bug — user input unintentionally widening the
/// result set via the LIKE wildcard primitives.
/// </para>
/// <para>
/// <b>Ordering with R0162.</b> The diacritic fold (R0162) MUST run before the
/// wildcard translation: <c>trim → fold → wildcard mask</c>. The fold leaves
/// <c>*</c>, <c>%</c>, <c>_</c>, and <c>\</c> alone (they are not diacritics), so the
/// wildcard processor sees the user's mask characters intact. This is enforced at
/// each call site; the helper does NOT fold internally — that would couple it to
/// the diacritic helper unnecessarily.
/// </para>
/// </remarks>
public static class WildcardMask
{
    /// <summary>
    /// Builds a Postgres <c>LIKE</c> pattern from the user-entered <paramref name="userInput"/>,
    /// applying the wildcard-mask translation rules described on the type.
    /// </summary>
    /// <param name="userInput">
    /// Free-form user query. <c>null</c> / empty / whitespace-only returns
    /// <see cref="string.Empty"/>; callers should skip the search.
    /// </param>
    /// <returns>
    /// A <c>LIKE</c>-compatible pattern with <c>*</c> translated to <c>%</c>, literal
    /// <c>%</c>/<c>_</c>/<c>\</c> escaped, and implicit <c>%...%</c> wrap applied when
    /// the input contains no <c>*</c>.
    /// </returns>
    /// <example>
    /// <code>
    /// WildcardMask.ToLikePattern("popescu")  // "%popescu%"
    /// WildcardMask.ToLikePattern("*ESCU")    // "%ESCU"
    /// WildcardMask.ToLikePattern("ASIGUR*")  // "ASIGUR%"
    /// WildcardMask.ToLikePattern("*ASIGUR*") // "%ASIGUR%"
    /// WildcardMask.ToLikePattern("100%")     // "%100\\%%"
    /// </code>
    /// </example>
    public static string ToLikePattern(string? userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return string.Empty;
        }

        var trimmed = userInput.Trim();
        var hasStar = trimmed.Contains('*');

        // +2 reserves space for the optional leading/trailing implicit '%'. The escape
        // path can grow the buffer beyond the original length but StringBuilder will
        // expand transparently.
        var sb = new StringBuilder(trimmed.Length + 2);
        if (!hasStar)
        {
            sb.Append('%');
        }
        foreach (var ch in trimmed)
        {
            switch (ch)
            {
                case '*':
                    sb.Append('%');
                    break;
                case '%':
                    // Escape the LIKE any-sequence wildcard so a literal '%' typed by the
                    // user matches itself instead of widening to every suffix.
                    sb.Append("\\%");
                    break;
                case '_':
                    // Escape the LIKE single-char wildcard for the same reason.
                    sb.Append("\\_");
                    break;
                case '\\':
                    // Escape the escape character itself — Postgres LIKE's default escape
                    // is '\', so a literal backslash MUST be doubled in the pattern.
                    sb.Append("\\\\");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        if (!hasStar)
        {
            sb.Append('%');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds a <see cref="Regex"/> equivalent of the wildcard mask for the InMemory
    /// provider's client-side fallback path. Mirrors the anchoring semantics of
    /// <see cref="ToLikePattern"/>: explicit <c>*</c> on a side anchors the opposite
    /// side; absence of <c>*</c> falls back to unanchored substring matching.
    /// </summary>
    /// <param name="userInput">
    /// Free-form user query. <c>null</c> / empty / whitespace-only returns a regex that
    /// matches nothing (using the impossible <c>(?!)</c> negative lookahead), so callers
    /// that forget the empty guard still produce a deterministic empty result set rather
    /// than the misleading "matches everything" alternative.
    /// </param>
    /// <returns>
    /// A compiled-style <see cref="Regex"/> with <see cref="RegexOptions.IgnoreCase"/> and
    /// <see cref="RegexOptions.CultureInvariant"/> applied. Case insensitivity is built in
    /// so callers don't need to repeat <see cref="StringComparison"/> qualifiers.
    /// </returns>
    /// <example>
    /// <code>
    /// WildcardMask.ToRegex("escu").IsMatch("Popescu")    // true (substring)
    /// WildcardMask.ToRegex("*escu").IsMatch("Popescu")   // true (ends with)
    /// WildcardMask.ToRegex("Asigur*").IsMatch("Asigura") // true (starts with)
    /// </code>
    /// </example>
    public static Regex ToRegex(string? userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return s_alwaysFalse;
        }

        var trimmed = userInput.Trim();
        var hasStar = trimmed.Contains('*');

        // When the user supplied explicit '*'s, anchor the regex with ^...$ so the mask
        // semantics ("starts with"/"ends with") survive translation. When they did not,
        // leave it unanchored (.* on both sides) for substring behaviour — the R0162
        // baseline. Without anchoring, "*escu" would otherwise match "Popescu Ion".
        var sb = new StringBuilder();
        sb.Append(hasStar ? "^" : ".*");
        foreach (var ch in trimmed)
        {
            switch (ch)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '\\':
                    // Escape the regex escape character itself so a literal backslash in
                    // the user query becomes \\ in the regex pattern.
                    sb.Append("\\\\");
                    break;
                default:
                    // Regex meta-characters that are NOT alphanumerics need to be escaped
                    // so that user input like "100." or "(test)" is treated literally
                    // rather than as a regex construct. '%' and '_' have no special regex
                    // meaning so they pass through unescaped — exactly matching their
                    // literal-character semantics.
                    if ("[]{}()|^$.+?".Contains(ch))
                    {
                        sb.Append('\\');
                    }
                    sb.Append(ch);
                    break;
            }
        }
        sb.Append(hasStar ? "$" : ".*");

        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Singleton regex that matches nothing. Returned from <see cref="ToRegex"/> for
    /// null/whitespace input so callers always receive a valid <see cref="Regex"/>
    /// reference (no null guards required) but cannot accidentally match every row.
    /// </summary>
    /// <remarks>
    /// <c>(?!)</c> is a negative lookahead for the empty string — it always fails to
    /// match at every position, so <see cref="Regex.IsMatch(string)"/> returns
    /// <see langword="false"/> for any input.
    /// </remarks>
    private static readonly Regex s_alwaysFalse = new(@"(?!)", RegexOptions.Compiled);
}
