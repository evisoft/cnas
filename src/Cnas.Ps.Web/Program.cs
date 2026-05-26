using Cnas.Ps.Web;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The backend API base URL is read from wwwroot/appsettings.json (Backend:ApiBaseUrl) at runtime.
// Cnas.Ps.Web is browser-only and communicates with Cnas.Ps.Api strictly over HTTP.
var apiBaseUrl = builder.Configuration["Backend:ApiBaseUrl"]
    ?? builder.HostEnvironment.BaseAddress;
builder.Services.AddHttpClient<CnasApiClient>(c =>
{
    c.BaseAddress = new Uri(apiBaseUrl);
    c.Timeout = TimeSpan.FromSeconds(15);
});

// Plain HttpClient registration — used by MainLayout (and any future page) that
// wants to issue raw HTTP calls without going through the typed CnasApiClient
// wrapper. Mirrors the BaseAddress / Timeout of the typed client so callers see
// consistent transport behaviour.
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(apiBaseUrl),
    Timeout = TimeSpan.FromSeconds(15),
});

// CookieAuthState polls /api/profile/me once per page load (60 s cache) and supplies the
// session as a CascadingValue<UserSession> in the layout. See CLAUDE.md §2.3.
builder.Services.AddScoped<CookieAuthState>();

// Blazor authorization plumbing — wires up the framework's AuthorizeView /
// AuthorizeRouteView surface so [Authorize] attributes on pages are actually
// enforced. Policies map to the canonical CNAS role bag; the cookie session
// is projected into a ClaimsPrincipal by CookieAuthStateProvider.
builder.Services.AddAuthorizationCore(options =>
{
    options.AddPolicy("CnasUser", p => p.RequireAuthenticatedUser());
    options.AddPolicy("CnasAdmin", p => p.RequireRole("cnas-admin"));
    options.AddPolicy("CnasTechAdmin", p => p.RequireRole("cnas-tech-admin"));
    options.AddPolicy("CnasDecider", p => p.RequireRole("cnas-decider"));
    options.AddPolicy("SefulDirectiei", p => p.RequireRole("cnas-decider"));
});
builder.Services.AddScoped<AuthenticationStateProvider, CookieAuthStateProvider>();

// R0403 / CF 17.08 — IClassifierLookup backs the single + multi classifier pickers.
// Scoped: re-uses the per-circuit HttpClient registered above; the lookup itself is stateless.
builder.Services.AddScoped<IClassifierLookup, ClassifierLookup>();

// R0170 / TOR CF 22.02 — per-circuit toast queue + on-demand notification poller.
// The poller is intentionally pull-based (no HostedService) so it stays compatible
// with the bUnit test harness (which disposes the component tree between tests).
builder.Services.AddScoped<IClientToastQueue, ClientToastQueue>();
builder.Services.AddScoped<ClientNotificationPoller>();

// Localization: resx files live under Resources/. See Resources/Pages.cs for the marker type
// PagesResource. CLAUDE.md task requirement: every visible string flows through
// IStringLocalizer<PagesResource>.
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");

await builder.Build().RunAsync().ConfigureAwait(false);
