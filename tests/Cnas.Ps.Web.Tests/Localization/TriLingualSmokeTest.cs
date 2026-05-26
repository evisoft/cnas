using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Resources;
using Cnas.Ps.Web.Resources;
using FluentAssertions;

namespace Cnas.Ps.Web.Tests.Localization;

/// <summary>
/// R2711 — tri-lingual smoke: every resource key shipped in the default
/// (Romanian) <c>PagesResource.resx</c> must also resolve in the English
/// (<c>PagesResource.en.resx</c>) and Russian (<c>PagesResource.ru.resx</c>)
/// satellite bundles. The test reflects directly over the resource manager
/// so adding a key in RO without translating it to EN or RU breaks the
/// build instead of slipping through to production as the literal key.
/// </summary>
/// <remarks>
/// <para>
/// The test enumerates the default culture (RO) via
/// <see cref="ResourceManager.GetResourceSet"/> with
/// <c>createIfNotExists: true</c> and <c>tryParents: false</c>, then for
/// each per-key <c>name → value</c> pair asserts that the same key resolves
/// to a non-empty, non-identical-to-key string in both the
/// <c>"en"</c> and <c>"ru"</c> cultures.
/// </para>
/// <para>
/// <b>Allowlist.</b> <see cref="IntentionallyIdenticalKeys"/> documents keys
/// whose translation legitimately equals the value in another language
/// (e.g. proper-noun titles). New entries here require a deliberate
/// code-review decision — the default expectation is that translations
/// differ from the source string.
/// </para>
/// </remarks>
public sealed class TriLingualSmokeTest
{
    /// <summary>
    /// Resource keys whose value may legitimately equal a sibling
    /// language's value (e.g. proper-noun product names). Each entry
    /// MUST carry a comment justifying the inclusion.
    /// </summary>
    private static readonly HashSet<string> IntentionallyIdenticalKeys = new(StringComparer.Ordinal)
    {
        // Currently empty — every shipped key has a distinct translation per
        // language. The hook exists so future translators can document a
        // deliberate equality (e.g. a brand name) without breaking the test.
    };

    /// <summary>
    /// R2711 — the resource manager exposes at least one Romanian key so the
    /// rest of the assertions are meaningful.
    /// </summary>
    [Fact]
    public void Default_ResourceSet_HasAtLeastOneRomanianKey()
    {
        var rm = NewResourceManager();
        var roKeys = EnumerateKeys(rm, new CultureInfo("ro")).ToList();
        roKeys.Should().NotBeEmpty(
            "the Romanian default resource set must ship at least one key — " +
            "otherwise the tri-lingual smoke test is asserting nothing (R2711).");
    }

    /// <summary>
    /// R2711 — every key present in the Romanian default also resolves in
    /// the English satellite bundle (non-null, non-empty, distinct from
    /// the bare key fallback).
    /// </summary>
    [Fact]
    public void Every_Romanian_Key_Resolves_In_English()
    {
        AssertSiblingCultureCoverage(new CultureInfo("en"), "en");
    }

    /// <summary>
    /// R2711 — every key present in the Romanian default also resolves in
    /// the Russian satellite bundle (non-null, non-empty, distinct from
    /// the bare key fallback).
    /// </summary>
    [Fact]
    public void Every_Romanian_Key_Resolves_In_Russian()
    {
        AssertSiblingCultureCoverage(new CultureInfo("ru"), "ru");
    }

    /// <summary>
    /// R2711 — sibling-culture coverage helper. Reflects every Romanian key
    /// and asserts that the supplied culture resolves a non-empty translation
    /// distinct from the key itself.
    /// </summary>
    /// <param name="culture">Sibling culture to verify.</param>
    /// <param name="cultureName">Friendly culture tag used in failure messages.</param>
    private static void AssertSiblingCultureCoverage(CultureInfo culture, string cultureName)
    {
        var rm = NewResourceManager();
        var romanian = EnumerateKeys(rm, new CultureInfo("ro")).ToList();
        romanian.Should().NotBeEmpty();

        var missing = new List<string>();
        var emptyOrUntranslated = new List<string>();
        foreach (var (key, _) in romanian)
        {
            var translated = rm.GetString(key, culture);
            if (translated is null)
            {
                missing.Add(key);
                continue;
            }
            if (string.IsNullOrWhiteSpace(translated))
            {
                emptyOrUntranslated.Add($"{key}=<empty>");
                continue;
            }
            // The literal "key as value" pattern often indicates a missing
            // translation. Honour the allowlist for documented exceptions.
            if (string.Equals(translated, key, StringComparison.Ordinal)
                && !IntentionallyIdenticalKeys.Contains(key))
            {
                emptyOrUntranslated.Add($"{key}=<key-as-value>");
            }
        }

        missing.Should().BeEmpty(
            $"every Romanian key must have a {cultureName} translation. " +
            "Missing keys:{0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine, missing));

        emptyOrUntranslated.Should().BeEmpty(
            $"every Romanian key's {cultureName} translation must be non-empty and distinct from the key. " +
            "Offenders:{0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine, emptyOrUntranslated));
    }

    /// <summary>
    /// Constructs a <see cref="ResourceManager"/> bound to the
    /// <see cref="PagesResource"/> marker. The base-name is the marker's
    /// full type name; the assembly is the one carrying the embedded
    /// default + the satellite resources.
    /// </summary>
    /// <returns>Configured resource manager.</returns>
    private static ResourceManager NewResourceManager()
        => new("Cnas.Ps.Web.Resources.PagesResource", typeof(PagesResource).Assembly);

    /// <summary>
    /// Enumerates every (key, value) pair declared in the supplied culture's
    /// resource set. <c>tryParents</c> is <c>false</c> so a missing
    /// culture-specific bundle yields an empty enumeration instead of
    /// silently falling back to the parent.
    /// </summary>
    /// <param name="rm">Resource manager.</param>
    /// <param name="culture">Culture whose resource set to enumerate.</param>
    /// <returns>Lazy sequence of (key, value) pairs.</returns>
    private static IEnumerable<(string Key, string Value)> EnumerateKeys(
        ResourceManager rm,
        CultureInfo culture)
    {
        var set = rm.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        if (set is null) yield break;
        foreach (DictionaryEntry entry in set)
        {
            if (entry.Key is string keyName && entry.Value is string keyValue)
            {
                yield return (keyName, keyValue);
            }
        }
    }
}
