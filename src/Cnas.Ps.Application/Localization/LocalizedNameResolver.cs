using System;

namespace Cnas.Ps.Application.Localization;

/// <summary>
/// R0027 / TOR ARH 022 — reference implementation of
/// <see cref="ILocalizedNameResolver"/>. Pure / allocation-free / thread-safe;
/// register as a singleton.
/// </summary>
public sealed class LocalizedNameResolver : ILocalizedNameResolver
{
    /// <inheritdoc />
    public string Resolve(
        string? baseName,
        string? nameRo,
        string? nameRu,
        string? nameEn,
        string? culture)
    {
        // Step 1 — match the requested culture against the per-locale columns.
        // We only inspect the two-letter prefix so callers can pass
        // BCP-47 codes like "ro-MD" or .NET culture names like "ru-RU"
        // without separately normalising.
        var prefix = ExtractTwoLetterPrefix(culture);
        var perCulture = SelectByCulture(prefix, nameRo, nameRu, nameEn);
        if (!string.IsNullOrWhiteSpace(perCulture))
        {
            return perCulture;
        }

        // Step 2 — Romanian fallback (skipped if RO was the requested culture
        // because step 1 already covered it).
        if (!IsRomanianPrefix(prefix) && !string.IsNullOrWhiteSpace(nameRo))
        {
            return nameRo;
        }

        // Step 3 — English fallback.
        if (!string.IsNullOrWhiteSpace(nameEn))
        {
            return nameEn;
        }

        // Step 4 — base-name fallback (the entity's legacy required column).
        if (!string.IsNullOrWhiteSpace(baseName))
        {
            return baseName;
        }

        // Step 5 — degenerate input: every field null/whitespace. Returning an
        // empty string keeps the caller's null-safety story uniform; the missing
        // string is the caller's problem to surface.
        return string.Empty;
    }

    /// <summary>
    /// Returns the lower-case two-letter prefix of <paramref name="culture"/>, or
    /// null when the input is missing / malformed. Allocation-free on the
    /// success path.
    /// </summary>
    /// <param name="culture">BCP-47 / .NET culture name (e.g. <c>"ro-MD"</c>, <c>"RU"</c>).</param>
    /// <returns>Lower-case 2-letter prefix or null.</returns>
    private static string? ExtractTwoLetterPrefix(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return null;
        }
        var span = culture.AsSpan().Trim();
        if (span.Length < 2)
        {
            return null;
        }
        var c0 = char.ToLowerInvariant(span[0]);
        var c1 = char.ToLowerInvariant(span[1]);
        // Reject if either char is non-alphabetic (defensive — protects against
        // junk input like "??-??").
        if (c0 is < 'a' or > 'z' || c1 is < 'a' or > 'z')
        {
            return null;
        }
        return new string(new[] { c0, c1 });
    }

    /// <summary>
    /// Returns the per-locale column matching <paramref name="prefix"/>, or null
    /// when <paramref name="prefix"/> does not map to a supported culture.
    /// </summary>
    /// <param name="prefix">Lower-case 2-letter culture prefix or null.</param>
    /// <param name="nameRo">Romanian column value.</param>
    /// <param name="nameRu">Russian column value.</param>
    /// <param name="nameEn">English column value.</param>
    /// <returns>The matching column value or null.</returns>
    private static string? SelectByCulture(
        string? prefix,
        string? nameRo,
        string? nameRu,
        string? nameEn) => prefix switch
        {
            "ro" => nameRo,
            "ru" => nameRu,
            "en" => nameEn,
            _ => null,
        };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="prefix"/> already
    /// targeted the Romanian column (so the step-2 RO fallback is redundant).
    /// </summary>
    /// <param name="prefix">Lower-case 2-letter culture prefix or null.</param>
    /// <returns><see langword="true"/> when the prefix is <c>"ro"</c>.</returns>
    private static bool IsRomanianPrefix(string? prefix) =>
        string.Equals(prefix, "ro", StringComparison.Ordinal);
}
