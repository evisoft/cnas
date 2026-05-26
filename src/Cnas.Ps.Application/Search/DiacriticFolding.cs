using System.Globalization;
using System.Text;

namespace Cnas.Ps.Application.Search;

/// <summary>
/// Strips diacritics from a string for canonical comparison. Used by:
/// <list type="bullet">
///   <item>The InMemory test provider's search fallback (no <c>unaccent()</c> extension).</item>
///   <item>The input canonicalization step before constructing a SQL <c>LIKE</c> pattern, so
///   the pattern matches what <c>unaccent(col)</c> produces server-side.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Romanian + Moldovan + Russian (Cyrillic) characters all transit our user-facing text
/// fields. The fold uses Unicode NFD decomposition + non-combining filter, which handles
/// Latin diacritics (<c>ăâîșțĂÂÎȘȚ</c>, <c>áéíóú</c> etc.) cleanly and leaves Cyrillic
/// letters unchanged (they have no Unicode combining-mark form for the typical surface).
/// </para>
/// <para>
/// Special-cases: <c>ș</c>/<c>ț</c> in modern Romanian use COMBINING COMMA BELOW (U+0326)
/// which NFD handles; legacy CEDILLA forms (<c>ş</c>/<c>ţ</c>, U+015F / U+0163) also
/// round-trip — NFD on a single-codepoint cedilla letter likewise decomposes to base + mark.
/// </para>
/// <para>
/// Case-PRESERVING by design: <c>Fold("Ștefan") == "Stefan"</c> not <c>"stefan"</c>. Case
/// insensitivity is the consumer's responsibility (PostgreSQL <c>ILIKE</c> or
/// <see cref="StringComparison.OrdinalIgnoreCase"/> in memory).
/// </para>
/// <para>
/// R0162 — diacritic-insensitive + case-insensitive search (CF 03.13). The Postgres path
/// uses the <c>unaccent</c> extension (registered in
/// <c>Cnas.Ps.Infrastructure.Persistence.CnasDbContext</c> via
/// <c>HasDbFunction</c> on <c>CnasDbFunctions.Unaccent</c>); this helper exists for the
/// in-process branch only. Never logs, never persists — pure transformation, safe for
/// PII inputs.
/// </para>
/// </remarks>
public static class DiacriticFolding
{
    /// <summary>
    /// Folds diacritics off the supplied string, returning a canonical form suitable for
    /// case-insensitive substring matching.
    /// </summary>
    /// <param name="input">
    /// The text to fold. <c>null</c> and empty inputs short-circuit to <see cref="string.Empty"/>
    /// so callers don't need a null guard.
    /// </param>
    /// <returns>
    /// The folded string in NFC form. Cyrillic, digits, whitespace, and ASCII punctuation
    /// pass through unchanged.
    /// </returns>
    /// <example>
    /// <code>
    /// DiacriticFolding.Fold("Ștefan Cărbune") // "Stefan Carbune"
    /// DiacriticFolding.Fold("Popéscu")        // "Popescu"
    /// DiacriticFolding.Fold("Иван Петров")    // "Иван Петров" (unchanged)
    /// </code>
    /// </example>
    public static string Fold(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // FormD decomposes combined glyphs (ș = s + combining-comma-below) into base + mark
        // sequences. We then strip every non-spacing mark and recompose to NFC. This handles
        // both the modern combining forms and the legacy cedilla single-codepoint forms
        // (ş/ţ) — both decompose to a base + mark sequence under NFD.
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
