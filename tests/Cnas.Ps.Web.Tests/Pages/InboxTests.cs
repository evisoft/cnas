using System.Collections;
using System.Net;
using System.Reflection;
using System.Resources;
using System.Text.Json;
using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages;
using Cnas.Ps.Web.Resources;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages;

/// <summary>
/// bUnit tests for the citizen notification <see cref="Inbox"/> page (UC22).
/// Verifies the rendered list, the empty-state copy, the error alert when the
/// upstream <c>/api/notifications/mine</c> endpoint fails, and the auth-gate
/// that prevents anonymous callers from hitting the (always-401) endpoint.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these assertions are written against the page's stable
/// <c>data-testid</c> hooks (added in this batch): <c>heading</c>,
/// <c>inbox-list</c>, <c>notification-item</c>, <c>empty-inbox</c>,
/// <c>error-alert</c>, <c>error-detail</c>. Anything that depends on a state
/// change after an awaited HTTP call is wrapped in <c>WaitForAssertion</c> to
/// avoid the parallel-test re-render race documented in <c>DashboardTests</c>
/// (#80).
/// </remarks>
public sealed class InboxTests : TestContext
{
    /// <summary>HTTP mock used by <see cref="CnasApiClient"/>.</summary>
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>
    /// Wires bUnit's service container with the minimal services every Web page
    /// expects: a mocked <see cref="HttpClient"/>, the API client wrapper, the
    /// localiser, and a loose JS runtime so any layout-level interop calls
    /// don't bring the render down.
    /// </summary>
    public InboxTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        // R0170 — Inbox.razor injects the client poller; provide the per-circuit
        // dependencies so bUnit can resolve them. The poller's PollAsync is fire-
        // and-forget from the page's OnAfterRender; the mock HttpClient absorbs it.
        Services.AddSingleton<Cnas.Ps.Web.Components.IClientToastQueue, Cnas.Ps.Web.Components.ClientToastQueue>();
        Services.AddSingleton<Cnas.Ps.Web.Components.ClientNotificationPoller>(sp =>
            new Cnas.Ps.Web.Components.ClientNotificationPoller(
                sp.GetRequiredService<CnasApiClient>(),
                sp.GetRequiredService<Cnas.Ps.Web.Components.IClientToastQueue>(),
                NullLogger<Cnas.Ps.Web.Components.ClientNotificationPoller>.Instance));
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Builds the canonical authenticated <see cref="UserSession"/> used by every
    /// test that wants to exercise the API-calling branch of the page.
    /// </summary>
    /// <returns>An authenticated session with a stub citizen profile.</returns>
    private static UserSession AuthenticatedSession()
        => new(true, new ProfileOutput("u1", "Ion Citizen", null, null, "ro", Array.Empty<IssuedDocumentSummaryDto>()));

    /// <summary>
    /// Happy path — the API returns two notifications and the page renders one
    /// <c>[data-testid='notification-item']</c> per item containing the subject
    /// and body text.
    /// </summary>
    [Fact]
    public void Inbox_WhenApiReturnsItems_RendersNotifications()
    {
        var paged = new PagedResult<NotificationOutput>(
            Items: new[]
            {
                new NotificationOutput(
                    Id: "n1",
                    Channel: "Email",
                    Subject: "Decision ready",
                    Body: "Your application has been approved.",
                    CreatedAtUtc: new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc),
                    ReadAtUtc: null,
                    DeliveryStatus: "Delivered"),
                new NotificationOutput(
                    Id: "n2",
                    Channel: "InApp",
                    Subject: "Request more info",
                    Body: "Please upload the missing document.",
                    CreatedAtUtc: new DateTime(2026, 5, 2, 10, 0, 0, DateTimeKind.Utc),
                    ReadAtUtc: new DateTime(2026, 5, 2, 11, 0, 0, DateTimeKind.Utc),
                    DeliveryStatus: "Delivered"),
            },
            Page: 1, PageSize: 20, TotalCount: 2);

        _mock.When("https://api.test/api/notifications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(paged));

        var cut = RenderComponent<Inbox>(p => p.Add(i => i.Session, AuthenticatedSession()));

        // The list renders only after OnInitializedAsync's await completes — wrap
        // in WaitForAssertion to avoid the same race we hit with DashboardTests #80.
        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll("[data-testid='notification-item']");
            items.Count.Should().Be(2);
            cut.Find("[data-testid='inbox-list']").Should().NotBeNull();
            cut.Markup.Should().Contain("Decision ready");
            cut.Markup.Should().Contain("Your application has been approved.");
            cut.Markup.Should().Contain("Request more info");
        });
    }

    /// <summary>
    /// Empty-state — the API returns an empty page and the dedicated
    /// <c>[data-testid='empty-inbox']</c> container renders in place of the list.
    /// </summary>
    [Fact]
    public void Inbox_WhenEmpty_ShowsEmptyMessage()
    {
        var empty = new PagedResult<NotificationOutput>(
            Items: Array.Empty<NotificationOutput>(),
            Page: 1, PageSize: 20, TotalCount: 0);

        _mock.When("https://api.test/api/notifications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(empty));

        var cut = RenderComponent<Inbox>(p => p.Add(i => i.Session, AuthenticatedSession()));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='empty-inbox']").Should().NotBeNull();
            cut.FindAll("[data-testid='notification-item']").Count.Should().Be(0);
            cut.FindAll("[data-testid='inbox-list']").Count.Should().Be(0);
        });
    }

    /// <summary>
    /// Failure path — the API returns 500 and the page renders the
    /// <c>[data-testid='error-alert']</c> container with the server-provided
    /// detail message inside <c>[data-testid='error-detail']</c>. The loading
    /// marker must not be visible after the failure resolves.
    /// </summary>
    [Fact]
    public void Inbox_WhenApiFails_ShowsErrorAlert()
    {
        const string problemBody = "upstream queue unavailable";
        _mock.When("https://api.test/api/notifications/mine*")
            .Respond(HttpStatusCode.InternalServerError, "text/plain", problemBody);

        var cut = RenderComponent<Inbox>(p => p.Add(i => i.Session, AuthenticatedSession()));

        // The error alert only renders after OnInitializedAsync awaits the failed
        // HTTP call and re-renders — wrap in WaitForAssertion (same pattern as
        // DashboardTests #80 and the Applications/New tests).
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='error-alert']").Should().NotBeNull();
            cut.Find("[data-testid='error-detail']").TextContent.Should().Contain(problemBody);
            cut.FindAll("[data-testid='inbox-list']").Count.Should().Be(0);
            cut.FindAll("[data-testid='notification-item']").Count.Should().Be(0);
        });
    }

    /// <summary>
    /// Auth gate — when the cascaded <see cref="UserSession"/> is anonymous the
    /// page MUST NOT call <c>/api/notifications/mine</c> (the call would always
    /// 401 and would still leak the endpoint's existence to unauth callers). We
    /// register the stub so any unexpected call would match (and be counted) but
    /// expect zero matches.
    /// </summary>
    [Fact]
    public void Inbox_WhenUnauthenticated_DoesNotRequestNotifications()
    {
        var empty = new PagedResult<NotificationOutput>(
            Items: Array.Empty<NotificationOutput>(),
            Page: 1, PageSize: 20, TotalCount: 0);
        var stub = _mock.When("https://api.test/api/notifications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(empty));

        // Render with the default anonymous session — no Session parameter supplied.
        var cut = RenderComponent<Inbox>();

        // The heading is still rendered (page is a public chrome with a sign-in
        // prompt), but no API request was issued.
        cut.Find("[data-testid='heading']").Should().NotBeNull();
        _mock.GetMatchCount(stub).Should().Be(0);
    }

    /// <summary>
    /// Auth gate (positive) — when the caller is authenticated the page issues
    /// at least one call to <c>/api/notifications/mine</c>. This pins the
    /// regression risk that a future refactor silently drops the auth-gate
    /// branch (the call would never happen). Since R0170 wired the
    /// ClientNotificationPoller into OnAfterRenderAsync the call count is at
    /// least 1 (page) + 1 (poller refresh).
    /// </summary>
    [Fact]
    public void Inbox_WhenAuthenticated_RequestsNotificationsAtLeastOnce()
    {
        var empty = new PagedResult<NotificationOutput>(
            Items: Array.Empty<NotificationOutput>(),
            Page: 1, PageSize: 20, TotalCount: 0);
        var stub = _mock.When("https://api.test/api/notifications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(empty));

        var cut = RenderComponent<Inbox>(p => p.Add(i => i.Session, AuthenticatedSession()));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='empty-inbox']").Should().NotBeNull();
            _mock.GetMatchCount(stub).Should().BeGreaterThanOrEqualTo(1);
        });
    }

    /// <summary>
    /// Localization contract — pins the keys the <see cref="Inbox"/> page resolves
    /// from <c>PagesResource.resx</c>. If any of these keys are removed or renamed
    /// the page will fall back to rendering the literal key string in the user's
    /// face. The assertion reads the embedded neutral (Romanian) resource bundle
    /// straight from the <c>Cnas.Ps.Web</c> assembly to avoid coupling the test
    /// to any per-culture satellite assembly that may or may not have shipped.
    /// </summary>
    [Fact]
    public void Inbox_RomanianResource_ContainsAllExpectedKeys()
    {
        // The .resx is embedded as `<RootNamespace>.<RelativePath>.<BaseName>.resources`.
        // For this project: Cnas.Ps.Web.Resources.PagesResource.resources.
        var asm = typeof(PagesResource).Assembly;
        const string resourceName = "Cnas.Ps.Web.Resources.PagesResource.resources";

        using var stream = asm.GetManifestResourceStream(resourceName);
        stream.Should().NotBeNull("the neutral PagesResource.resx must be embedded in Cnas.Ps.Web.dll");

        using var reader = new ResourceReader(stream!);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in reader)
        {
            keys.Add((string)entry.Key);
        }

        // The 5 keys consumed by Inbox.razor. `Common.Error` is reused from the
        // shared bucket (Dashboard / Applications/* also bind it); the four Inbox.*
        // keys are page-specific.
        keys.Should().Contain("Inbox.Title");
        keys.Should().Contain("Inbox.Loading");
        keys.Should().Contain("Inbox.Empty");
        keys.Should().Contain("Inbox.Anonymous");
        keys.Should().Contain("Common.Error");
    }
}
