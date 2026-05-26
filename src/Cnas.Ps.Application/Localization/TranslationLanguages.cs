namespace Cnas.Ps.Application.Localization;

/// <summary>
/// R0210 / TOR UI 003 — canonical ISO-639-1 language codes supported by the
/// translation registry (RO / EN / RU). Centralised so the validator, the resolver's
/// fallback chain, and the controller's route-segment guard share one allow-list.
/// </summary>
/// <remarks>
/// Adding a fourth language is a deliberate decision because every persisted key
/// gains a new row to author. The constants live here in the Application layer so
/// Core / Contracts stay free of presentation vocabulary.
/// </remarks>
public static class TranslationLanguages
{
    /// <summary>Romanian — the default + final fallback language.</summary>
    public const string Romanian = "ro";

    /// <summary>English.</summary>
    public const string English = "en";

    /// <summary>Russian.</summary>
    public const string Russian = "ru";

    /// <summary>The frozen allow-list of supported language codes.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Romanian, English, Russian };

    /// <summary>
    /// Returns <c>true</c> when <paramref name="language"/> is one of the canonical
    /// codes (case-sensitive — operators are expected to use lowercase).
    /// </summary>
    /// <param name="language">Candidate language code.</param>
    /// <returns><c>true</c> when supported.</returns>
    public static bool IsSupported(string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return false;
        }
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], language, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
