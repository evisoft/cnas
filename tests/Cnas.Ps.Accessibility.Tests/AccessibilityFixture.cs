using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Playwright;

namespace Cnas.Ps.Accessibility.Tests;

/// <summary>
/// xUnit collection fixture for the accessibility test suite. Boots a single
/// Playwright + Chromium pair AND a tiny Kestrel host that serves the citizen
/// portal's static <c>wwwroot</c> shell. The shell is sufficient for the axe-core
/// scan to flag the HTML structure (lang attr, landmark roles, page title,
/// document-level color contrast) without paying the cost of a full Blazor WASM
/// runtime bootstrap.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not host the API / WebApplicationFactory?</b> The Web project is Blazor
/// WebAssembly Standalone — there is no server-rendered shell, the API is a
/// separate process, and a WASM bootstrap downloads several MB of runtime DLLs
/// before any page becomes scannable. The static-shell scan locks the most
/// commonly-failed top-level WCAG rules (html-has-lang, document-title,
/// landmark-one-main, region) cheaply and reliably; once the staff-console
/// Blazor pages and the server-rendered application-flow shells land, future
/// batches will widen the surface.
/// </para>
/// <para>
/// <b>Port selection.</b> Binds Kestrel to <c>http://127.0.0.1:0</c> so the OS
/// picks a free port — keeps parallel CI runs safe.
/// </para>
/// </remarks>
public sealed class AccessibilityFixture : IAsyncLifetime
{
    /// <summary>Argument vector for the Playwright CLI install step. Mirrored from
    /// <c>Cnas.Ps.E2E.Tests.PlaywrightFixture</c>; held as a static readonly field per
    /// CA1861 to avoid re-allocating on every invocation.</summary>
    private static readonly string[] InstallArgs = ["install", "--with-deps", "chromium"];

    private WebApplication? _host;

    /// <summary>Top-level Playwright entry-point. Disposed in <see cref="DisposeAsync"/>.</summary>
    public IPlaywright Playwright { get; private set; } = null!;

    /// <summary>Headless Chromium browser shared by every accessibility scan.</summary>
    public IBrowser Browser { get; private set; } = null!;

    /// <summary>Base address of the running Kestrel host (e.g. <c>http://127.0.0.1:54321</c>).</summary>
    public string BaseAddress { get; private set; } = string.Empty;

    /// <summary>
    /// Installs Chromium (unless <c>PLAYWRIGHT_SKIP_INSTALL</c> is set), launches the
    /// browser, then starts the static-content host pointing at the Web project's
    /// build-output <c>wwwroot</c> directory.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLAYWRIGHT_SKIP_INSTALL")))
        {
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

        // Locate the Cnas.Ps.Web source wwwroot. See ResolveWebWwwroot remarks for
        // why we point at the source tree rather than a build-output copy.
        var wwwrootPath = ResolveWebWwwroot()
            ?? throw new InvalidOperationException(
                "Web wwwroot not found — walked up from the running assembly looking for "
                + "src/Cnas.Ps.Web/wwwroot/index.html. The repository layout must include the "
                + "Web project at the canonical path for the accessibility scan to host its shell.");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _host = builder.Build();
        _host.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = new PhysicalFileProvider(wwwrootPath),
            DefaultFileNames = new List<string> { "index.html" },
        });
        _host.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(wwwrootPath),
        });

        await _host.StartAsync().ConfigureAwait(false);

        var server = _host.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");
        BaseAddress = addresses.Addresses.First().TrimEnd('/');
    }

    /// <summary>Closes the browser, disposes Playwright, and stops the static-content host.</summary>
    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync().ConfigureAwait(false);
        }
        Playwright?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(false);
            await _host.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Walks up the directory tree from the running test assembly until it finds a
    /// directory containing <c>Cnas.Ps.slnx</c> (the repository root), then resolves
    /// the source <c>src/Cnas.Ps.Web/wwwroot</c> directory.
    /// </summary>
    /// <remarks>
    /// We deliberately point at the SOURCE wwwroot rather than a build-output copy.
    /// A Blazor WebAssembly Standalone build does not copy <c>index.html</c> /
    /// <c>css/</c> / <c>appsettings.json</c> into <c>bin/&lt;cfg&gt;/&lt;tfm&gt;/wwwroot</c>
    /// during regular <c>dotnet build</c> — only <c>_framework/</c> runtime artefacts
    /// land there; the static shell only materialises when <c>dotnet publish</c>
    /// generates the deployment bundle. Pointing at the source wwwroot lets the
    /// accessibility scan run on a stock <c>dotnet build</c> output without forcing
    /// every contributor (and the CI lane) to add a publish step.
    /// </remarks>
    /// <returns>The absolute path to the source Web wwwroot directory, or <c>null</c>
    /// if the repo root cannot be located.</returns>
    private static string? ResolveWebWwwroot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(AccessibilityFixture).Assembly.Location);
        if (string.IsNullOrEmpty(assemblyDir))
        {
            return null;
        }

        // Walk up looking for the repo root marker (the slnx).
        var dir = new DirectoryInfo(assemblyDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cnas.Ps.slnx")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            return null;
        }

        var sourceWwwroot = Path.Combine(dir.FullName, "src", "Cnas.Ps.Web", "wwwroot");
        return Directory.Exists(sourceWwwroot) && File.Exists(Path.Combine(sourceWwwroot, "index.html"))
            ? sourceWwwroot
            : null;
    }
}

/// <summary>
/// xUnit collection definition that pins the shared <see cref="AccessibilityFixture"/>
/// to every Playwright-driven accessibility test so the browser + host pair is
/// constructed exactly once per test run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AccessibilityCollection : ICollectionFixture<AccessibilityFixture>
{
    /// <summary>The collection name referenced by <c>[Collection]</c> attributes on tests.</summary>
    public const string Name = "Accessibility";
}
