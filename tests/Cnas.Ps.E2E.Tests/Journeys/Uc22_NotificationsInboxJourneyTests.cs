using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC22 — "Notificare utilizatori". End-to-end journey covering a citizen reading their
/// in-app notification inbox and marking an entry as read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> Recipient — a citizen authenticated via <see cref="TestAuthHandler"/>.
/// The persona's Sqid id matches the seeded notification's <see cref="Notification.RecipientUserId"/>.
/// </para>
/// <para>
/// <b>Business outcome asserted.</b>
/// <list type="number">
///   <item>HTTP 200 OK from <c>GET /api/notifications/mine</c> with at least one entry whose
///         <c>readAtUtc</c> is null (unread).</item>
///   <item>HTTP 204 No Content from <c>POST /api/notifications/{id}/read</c>.</item>
///   <item>The DB row for that notification has <see cref="Notification.ReadAtUtc"/> stamped.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc22_NotificationsInboxJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc22_NotificationsInboxJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The notification recipient retrieves their inbox and marks a notification as read.
    /// </summary>
    [Fact]
    public async Task Inbox_ReadsAndMarksReadFlowThroughHttp()
    {
        // Arrange — seed a UserProfile (the inbox keys off UserProfile id, not Solicitant id)
        // and an unread Notification addressed to that profile.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var recipient = new UserProfile
        {
            MPassSubject = "uc22-recipient-sub",
            DisplayName = "UC22 Recipient",
            Email = "uc22@example.test",
            Roles = ["cnas-user"],
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.UserProfiles.Add(recipient);
        await db.SaveChangesAsync();

        var notification = new Notification
        {
            RecipientUserId = recipient.Id,
            Channel = NotificationChannel.InApp,
            Subject = "UC22 E2E subject",
            Body = "UC22 E2E body — locked subject so the assertion is deterministic.",
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();

        var recipientSqid = sqids.Encode(recipient.Id);
        var notificationSqid = sqids.Encode(notification.Id);

        // Build the recipient HTTP client.
        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: recipientSqid, Roles: ["cnas-user"])));

        // Act 1 — fetch inbox.
        using var inboxResponse = await client.GetAsync("/api/notifications/mine?page=1&pageSize=20");
        inboxResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await inboxResponse.Content.ReadAsStringAsync());
        var page = await inboxResponse.Content.ReadFromJsonAsync<PagedResult<NotificationOutput>>();
        page.Should().NotBeNull();
        var inboxEntry = page!.Items.SingleOrDefault(n => n.Id == notificationSqid);
        inboxEntry.Should().NotBeNull("the seeded notification must surface in the inbox");
        inboxEntry!.Subject.Should().Be("UC22 E2E subject");
        inboxEntry.ReadAtUtc.Should().BeNull("the notification has not been marked read yet");

        // Act 2 — mark read.
        using var markResponse = await client.PostAsync(
            $"/api/notifications/{notificationSqid}/read",
            content: null);
        markResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await markResponse.Content.ReadAsStringAsync());

        // Assert — DB row has ReadAtUtc stamped.
        await using var readScope = _fixture.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var refreshed = await readDb.Notifications.AsNoTracking()
            .SingleOrDefaultAsync(n => n.Id == notification.Id);
        refreshed.Should().NotBeNull();
        refreshed!.ReadAtUtc.Should().NotBeNull("MarkReadAsync must stamp the column");
    }
}
