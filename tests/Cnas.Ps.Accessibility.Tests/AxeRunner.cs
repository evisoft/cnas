using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;

namespace Cnas.Ps.Accessibility.Tests;

/// <summary>
/// Loads the vendored axe-core bundle, injects it into a Playwright page, and parses
/// the resulting <c>axe.run()</c> output into a strongly-typed
/// <see cref="AxeAnalysisResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// The bundle is read from an embedded resource at <c>Resources/axe.min.js</c>. The
/// .csproj wires the file in as an <c>EmbeddedResource</c> so the bundle ships with
/// the test DLL and the runner does not depend on the file system layout of the
/// containing test runner.
/// </para>
/// <para>
/// The runner intentionally exposes <see cref="IsPlaceholderBundle"/> as a static
/// helper that operates on raw string content. The Playwright theory uses the helper
/// before booting a browser context so a missing bundle yields a logged warning
/// rather than a thrown exception.
/// </para>
/// </remarks>
public static class AxeRunner
{
    /// <summary>
    /// Logical resource name of the vendored axe-core bundle. The .NET build embeds
    /// the file under its project-relative path, with backslashes replaced by dots —
    /// i.e. <c>Cnas.Ps.Accessibility.Tests.Resources.axe.min.js</c>.
    /// </summary>
    private const string BundleResourceName = "Cnas.Ps.Accessibility.Tests.Resources.axe.min.js";

    /// <summary>
    /// Token written on the very first line of the placeholder bundle. Detection is
    /// substring-based (so additional whitespace or leading BOM does not defeat it).
    /// </summary>
    private const string PlaceholderToken = "// PLACEHOLDER";

    /// <summary>
    /// Loads the embedded axe-core bundle and returns its full contents as a string.
    /// </summary>
    /// <returns>The bundle source, never <c>null</c> or empty.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the embedded resource is
    /// missing — indicates a broken .csproj wiring rather than a missing-bundle case
    /// (use <see cref="IsPlaceholderBundle"/> for that).</exception>
    public static async Task<string> LoadBundleAsync()
    {
        var assembly = typeof(AxeRunner).Assembly;
        await using var stream = assembly.GetManifestResourceStream(BundleResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{BundleResourceName}' not found — check the "
                + "Cnas.Ps.Accessibility.Tests.csproj EmbeddedResource wiring.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="bundleSource"/> is the committed
    /// placeholder bundle. Detection is substring-based on the literal
    /// <c>// PLACEHOLDER</c> token written at the head of the placeholder file.
    /// </summary>
    /// <param name="bundleSource">The full text of <c>axe.min.js</c>.</param>
    /// <returns><c>true</c> when the placeholder token is present anywhere in the
    /// file; <c>false</c> otherwise.</returns>
    public static bool IsPlaceholderBundle(string? bundleSource)
    {
        if (string.IsNullOrEmpty(bundleSource))
        {
            // A truly empty bundle is also "missing" — fall through to the same code path.
            return true;
        }
        return bundleSource.Contains(PlaceholderToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// Injects axe-core into <paramref name="page"/> and runs the default rule set
    /// against the entire document.
    /// </summary>
    /// <param name="page">A loaded Playwright page. The caller must have navigated
    /// to the target URL and waited for the network to settle before calling this.</param>
    /// <param name="bundleSource">The pre-loaded axe-core bundle. Pass the value of a
    /// previous <see cref="LoadBundleAsync"/> call to avoid reading the embedded
    /// resource per invocation when scanning multiple pages.</param>
    /// <returns>The parsed scan result.</returns>
    /// <exception cref="AxeBundleMissingException">Thrown when <paramref name="bundleSource"/>
    /// is the placeholder bundle — callers should translate this into a logged warning
    /// rather than a test failure (offline dev).</exception>
    public static async Task<AxeAnalysisResult> RunAsync(IPage page, string bundleSource)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (IsPlaceholderBundle(bundleSource))
        {
            throw new AxeBundleMissingException();
        }

        // Add axe-core as an inline script so the page does not need a CDN round-trip
        // and so we sidestep any CSP that would block external script-src.
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Content = bundleSource })
            .ConfigureAwait(false);

        // Evaluate axe.run on the entire document. axe-core returns the result as a
        // Promise, which Playwright's EvaluateAsync awaits transparently. We serialise
        // to a JSON string first so the round-trip through Newtonsoft (Playwright's
        // internal serialiser) doesn't lose unknown properties.
        var jsonElement = await page.EvaluateAsync<JsonElement>(
            "async () => JSON.stringify(await axe.run(document))").ConfigureAwait(false);
        var json = jsonElement.GetString()
            ?? throw new InvalidOperationException("axe.run returned a non-string result.");
        return AxeAnalysisResult.FromJson(json);
    }
}
