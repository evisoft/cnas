using System.Net;
using System.Text.Json;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using Cnas.Ps.Web.Components;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Services;

/// <summary>
/// R0170 / TOR CF 22.02 — tests for <see cref="ClientNotificationPoller"/>.
/// Pins:
/// <list type="bullet">
///   <item>Initial poll on an empty inbox is silent (no toasts).</item>
///   <item>New unread item enqueues exactly one toast carrying its deep-link.</item>
///   <item>Transport / HTTP errors are swallowed — the poller stays alive.</item>
/// </list>
/// </summary>
public sealed class ClientNotificationPollerTests
{
    [Fact]
    public async Task PollAsync_EmptyInbox_DoesNotEnqueueAnyToast()
    {
        var mock = new MockHttpMessageHandler();
        var empty = new PagedResult<NotificationOutput>(
            Items: Array.Empty<NotificationOutput>(),
            Page: 1, PageSize: 20, TotalCount: 0);
        mock.When("https://api.test/api/notifications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(empty));

        var (poller, queue) = BuildHarness(mock);

        var emitted = await poller.PollAsync();

        emitted.Should().Be(0);
        queue.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_NewUnreadRow_EnqueuesOneToastWithDeepLink()
    {
        var mock = new MockHttpMessageHandler();
        var paged = new PagedResult<NotificationOutput>(
            Items: new[]
            {
                new NotificationOutput(
                    Id: "n1",
                    Channel: "InApp",
                    Subject: "Decision ready",
                    Body: "Your application was approved.",
                    CreatedAtUtc: new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc),
                    ReadAtUtc: null,
                    DeliveryStatus: "Delivered",
                    DeepLinkUrl: "/applications/k3Gq9"),
            },
            Page: 1, PageSize: 20, TotalCount: 1);
        mock.When("https://api.test/api/notifications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(paged));

        var (poller, queue) = BuildHarness(mock);

        var emitted = await poller.PollAsync();

        emitted.Should().Be(1);
        var toast = queue.Snapshot().Single();
        toast.Title.Should().Be("Decision ready");
        toast.Body.Should().Be("Your application was approved.");
        toast.DeepLinkUrl.Should().Be("/applications/k3Gq9");
    }

    [Fact]
    public async Task PollAsync_TransportError_SwallowsAndReturnsZero()
    {
        var mock = new MockHttpMessageHandler();
        mock.When("https://api.test/api/notifications/mine*")
            .Respond(HttpStatusCode.InternalServerError, "text/plain", "upstream queue unavailable");

        var (poller, queue) = BuildHarness(mock);

        var emitted = await poller.PollAsync();

        emitted.Should().Be(0);
        queue.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_SecondCallWithSameRow_DoesNotRePush()
    {
        var mock = new MockHttpMessageHandler();
        var paged = new PagedResult<NotificationOutput>(
            Items: new[]
            {
                new NotificationOutput(
                    Id: "n1",
                    Channel: "InApp",
                    Subject: "Decision ready",
                    Body: "Your application was approved.",
                    CreatedAtUtc: new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc),
                    ReadAtUtc: null,
                    DeliveryStatus: "Delivered",
                    DeepLinkUrl: "/applications/k3Gq9"),
            },
            Page: 1, PageSize: 20, TotalCount: 1);
        mock.When("https://api.test/api/notifications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(paged));

        var (poller, queue) = BuildHarness(mock);

        var first = await poller.PollAsync();
        var second = await poller.PollAsync();

        first.Should().Be(1);
        second.Should().Be(0,
            "the watermark should prevent re-emitting toasts for an unchanged row");
        queue.Snapshot().Count.Should().Be(1);
    }

    // ─── helpers ───

    private static (ClientNotificationPoller poller, IClientToastQueue queue) BuildHarness(MockHttpMessageHandler mock)
    {
        var http = mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        var api = new CnasApiClient(http, NullLogger<CnasApiClient>.Instance);
        var queue = new ClientToastQueue();
        var poller = new ClientNotificationPoller(api, queue, NullLogger<ClientNotificationPoller>.Instance);
        return (poller, queue);
    }
}
