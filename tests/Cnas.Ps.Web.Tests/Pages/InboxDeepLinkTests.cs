using System.Net;
using System.Text.Json;
using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Pages;

/// <summary>
/// R0172 / TOR CF 22.05 — bUnit tests for the deep-link branch in
/// <see cref="Inbox"/>. Verifies the page renders the notification subject as
/// an <c>&lt;a href&gt;</c> when the API surfaces a non-null
/// <see cref="NotificationOutput.DeepLinkUrl"/>, and falls back to plain text
/// when the URL is null. Mirrors the pattern in
/// <see cref="InboxTests"/> — same minimal service-container wiring and same
/// <c>data-testid</c>-based selectors.
/// </summary>
public sealed class InboxDeepLinkTests : TestContext
{
    /// <summary>HTTP mock used by <see cref="CnasApiClient"/>.</summary>
    private readonly MockHttpMessageHandler _mock = new();

    /// <summary>Wires the minimal service container the page expects.</summary>
    public InboxDeepLinkTests()
    {
        var http = _mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        Services.AddSingleton(http);
        Services.AddSingleton(sp => new CnasApiClient(http, NullLogger<CnasApiClient>.Instance));
        // R0170 — Inbox.razor injects the client poller; provide the per-circuit
        // dependencies so bUnit can resolve them.
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
    /// Builds an authenticated session matching <see cref="InboxTests"/>'s
    /// fixture so the page passes the anonymous-gate and issues the API call.
    /// </summary>
    private static UserSession AuthenticatedSession()
        => new(true, new ProfileOutput("u1", "Ion Citizen", null, null, "ro", Array.Empty<IssuedDocumentSummaryDto>()));

    [Fact]
    public void Inbox_WhenDeepLinkPresent_RendersAnchorPointingAtRoute()
    {
        // Arrange — one notification carries a DeepLinkUrl computed server-side.
        var paged = new PagedResult<NotificationOutput>(
            Items: new[]
            {
                new NotificationOutput(
                    Id: "n1",
                    Channel: "InApp",
                    Subject: "Cererea ta a fost aprobată",
                    Body: "Cererea #4523 a fost aprobată.",
                    CreatedAtUtc: new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc),
                    ReadAtUtc: null,
                    DeliveryStatus: "Delivered",
                    DeepLinkUrl: "/applications/k3Gq9"),
            },
            Page: 1, PageSize: 20, TotalCount: 1);

        _mock.When("https://api.test/api/notifications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(paged));

        var cut = RenderComponent<Inbox>(p => p.Add(i => i.Session, AuthenticatedSession()));

        cut.WaitForAssertion(() =>
        {
            // The anchor exists, carries the resolved URL, and wraps the subject.
            var anchor = cut.Find("[data-testid='notification-deep-link']");
            anchor.Should().NotBeNull();
            anchor.GetAttribute("href").Should().Be("/applications/k3Gq9");
            anchor.TextContent.Should().Contain("Cererea ta a fost aprobată");
        });
    }

    [Fact]
    public void Inbox_WhenDeepLinkAbsent_RendersPlainSubjectWithoutAnchor()
    {
        // Arrange — notification has NO DeepLinkUrl (null), the legacy shape.
        var paged = new PagedResult<NotificationOutput>(
            Items: new[]
            {
                new NotificationOutput(
                    Id: "n1",
                    Channel: "Email",
                    Subject: "Generic broadcast",
                    Body: "System announcement.",
                    CreatedAtUtc: new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc),
                    ReadAtUtc: null,
                    DeliveryStatus: "Delivered",
                    DeepLinkUrl: null),
            },
            Page: 1, PageSize: 20, TotalCount: 1);

        _mock.When("https://api.test/api/notifications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(paged));

        var cut = RenderComponent<Inbox>(p => p.Add(i => i.Session, AuthenticatedSession()));

        cut.WaitForAssertion(() =>
        {
            // The list rendered, the item rendered, but no deep-link anchor
            // exists for a null URL — the subject is plain text in the header.
            cut.FindAll("[data-testid='notification-item']").Count.Should().Be(1);
            cut.FindAll("[data-testid='notification-deep-link']").Count.Should().Be(0);
            cut.Markup.Should().Contain("Generic broadcast");
        });
    }
}
