using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Dashboard;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Dashboard;

/// <summary>
/// R0170 / TOR CF 22.02 + CF 04.02 — unit tests for
/// <see cref="UnreadNotificationsTileProducer"/>. Pins:
/// <list type="bullet">
///   <item>Only rows belonging to the calling user are counted.</item>
///   <item>Read rows (<c>ReadAtUtc</c> non-null) are excluded.</item>
///   <item>Soft-deleted rows (<c>IsActive=false</c>) are excluded.</item>
///   <item>The producer declares the wildcard role allow-list.</item>
/// </list>
/// </summary>
public sealed class UnreadNotificationsTileProducerTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ProduceAsync_CountsOnlyUnreadActiveRowsForCaller()
    {
        const long callerId = 7L;
        var harness = Harness.Create();
        harness.Db.Notifications.AddRange(
            // counted: belongs to caller, unread, active.
            BuildNotification(recipient: callerId, readAtUtc: null, active: true),
            BuildNotification(recipient: callerId, readAtUtc: null, active: true),
            // NOT counted: already read.
            BuildNotification(recipient: callerId, readAtUtc: ClockNow.AddMinutes(-5), active: true),
            // NOT counted: soft-deleted.
            BuildNotification(recipient: callerId, readAtUtc: null, active: false),
            // NOT counted: belongs to another user.
            BuildNotification(recipient: 99L, readAtUtc: null, active: true));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        var widget = result.Value!.Single();
        widget.Code.Should().Be("NOTIFICATIONS_UNREAD");
        widget.Value.Should().Be(2m);
        widget.Category.Should().Be(nameof(DashboardCategory.SystemNotifications));
    }

    [Fact]
    public async Task ProduceAsync_NoMatches_ReturnsZero()
    {
        var harness = Harness.Create();
        var result = await harness.Producer.ProduceAsync(userId: 42L);
        result.IsSuccess.Should().BeTrue();
        result.Value!.Single().Value.Should().Be(0m);
    }

    /// <summary>Expected wildcard role allow-list — pre-allocated to satisfy CA1861.</summary>
    private static readonly string[] ExpectedWildcardRoles = ["*"];

    [Fact]
    public void Producer_DeclaresWildcardRoleAllowList()
    {
        var harness = Harness.Create();
        harness.Producer.SupportedRoles.Should().BeEquivalentTo(ExpectedWildcardRoles);
        harness.Producer.Category.Should().Be(DashboardCategory.SystemNotifications);
    }

    // ─── helpers ───

    private static Notification BuildNotification(long recipient, DateTime? readAtUtc, bool active) => new()
    {
        RecipientUserId = recipient,
        Channel = NotificationChannel.InApp,
        Subject = "Subject",
        Body = "Body",
        DeliveryStatus = NotificationDeliveryStatus.Delivered,
        ReadAtUtc = readAtUtc,
        IsActive = active,
        CreatedAtUtc = ClockNow.AddMinutes(-10),
    };

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required UnreadNotificationsTileProducer Producer { get; init; }

        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-unread-tile-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            IReadOnlyCnasDbContext readDb = db;
            var producer = new UnreadNotificationsTileProducer(readDb);
            return new Harness { Db = db, Producer = producer };
        }
    }
}
