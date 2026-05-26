using Bunit;
using Cnas.Ps.Web.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Web.Tests.Components;

/// <summary>
/// R0170 / TOR CF 22.02 — bUnit tests for <see cref="ToastNotificationHost"/>.
/// Pins three invariants:
/// <list type="bullet">
///   <item>An empty queue renders the host container without any toast items.</item>
///   <item>Pushing an item via the queue triggers a re-render that surfaces the row.</item>
///   <item>Clicking the dismiss button removes the row from the rendered output.</item>
/// </list>
/// </summary>
public sealed class ToastNotificationHostTests : TestContext
{
    public ToastNotificationHostTests()
    {
        Services.AddSingleton<IClientToastQueue, ClientToastQueue>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Host_WhenQueueEmpty_RendersNoToastItems()
    {
        var cut = RenderComponent<ToastNotificationHost>();
        cut.Find("[data-testid='toast-host']").Should().NotBeNull();
        cut.FindAll("[data-testid='toast-item']").Count.Should().Be(0);
    }

    [Fact]
    public void Host_WhenEnqueueCalled_RendersOneToastItem()
    {
        var queue = Services.GetRequiredService<IClientToastQueue>();
        var cut = RenderComponent<ToastNotificationHost>();

        queue.Enqueue(ToastLevel.Info, "New decision", "Your application was approved.", "/applications/k3Gq9");

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='toast-item']").Count.Should().Be(1);
            cut.Markup.Should().Contain("New decision");
            cut.Markup.Should().Contain("Your application was approved.");
            var link = cut.Find("[data-testid='toast-link']");
            link.GetAttribute("href").Should().Be("/applications/k3Gq9");
        });
    }

    [Fact]
    public void Host_WhenDismissClicked_RemovesToastFromRender()
    {
        var queue = Services.GetRequiredService<IClientToastQueue>();
        queue.Enqueue(ToastLevel.Info, "T1", "Body 1", null);

        var cut = RenderComponent<ToastNotificationHost>();
        cut.FindAll("[data-testid='toast-item']").Count.Should().Be(1);

        cut.Find("[data-testid='toast-dismiss']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='toast-item']").Count.Should().Be(0);
        });
    }
}
