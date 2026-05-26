using Microsoft.Playwright;

namespace Cnas.Ps.E2E.Tests;

/// <summary>
/// xUnit collection fixture that bootstraps Playwright for the entire test run:
/// installs the Chromium browser binaries (idempotent, network-gated) and launches
/// a single headless Chromium instance shared by all journey tests.
/// </summary>
/// <remarks>
/// <para>
/// Browser install is a one-time download performed via
/// <c>Microsoft.Playwright.Program.Main(new[] { "install", ... })</c>. To skip the
/// download (offline CI, pre-baked container image, etc.) set the environment
/// variable <c>PLAYWRIGHT_SKIP_INSTALL</c> to any non-empty value.
/// </para>
/// <para>
/// Tests that need a fresh browser context obtain it via
/// <see cref="IBrowser.NewContextAsync"/> on <see cref="Browser"/> — avoid relying on
/// shared cookies/localStorage between tests.
/// </para>
/// </remarks>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    /// <summary>Argument vector for the Playwright CLI install step. Held as a static
    /// readonly field per CA1861 to avoid re-allocating on every invocation.</summary>
    private static readonly string[] InstallArgs = ["install", "--with-deps", "chromium"];


    /// <summary>Top-level Playwright entry-point. Disposed in <see cref="DisposeAsync"/>.</summary>
    public IPlaywright Playwright { get; private set; } = null!;

    /// <summary>Headless Chromium browser shared by every journey test.</summary>
    public IBrowser Browser { get; private set; } = null!;

    /// <summary>
    /// Installs the Chromium browser (unless <c>PLAYWRIGHT_SKIP_INSTALL</c> is set),
    /// then creates the <see cref="IPlaywright"/> driver and launches Chromium.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLAYWRIGHT_SKIP_INSTALL")))
        {
            // Returns a non-zero exit code if the install fails — we surface it as an
            // InvalidOperationException so xUnit reports a clean fixture failure rather
            // than the tests silently using a missing browser.
            var exit = Microsoft.Playwright.Program.Main(InstallArgs);
            if (exit != 0)
            {
                throw new InvalidOperationException(
                    $"Playwright Chromium install failed with exit code {exit}. "
                    + "Set PLAYWRIGHT_SKIP_INSTALL=1 if the browser is pre-provisioned.");
            }
        }

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        }).ConfigureAwait(false);
    }

    /// <summary>Closes the browser and disposes the Playwright driver.</summary>
    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync().ConfigureAwait(false);
        }
        Playwright?.Dispose();
    }
}
