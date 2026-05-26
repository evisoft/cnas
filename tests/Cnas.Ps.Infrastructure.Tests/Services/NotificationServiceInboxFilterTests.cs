using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0371 — extended-filter + mark-all-read tests for <see cref="NotificationService"/>.
/// The original inbox API surfaced only page/pageSize; the dashboard history view needs
/// to filter by read/unread + channel and to support a bulk "mark all read" action.
/// </summary>
/// <remarks>
/// <para>
/// Written test-first per CLAUDE.md RULE 1 — these assertions FAIL until
/// <see cref="INotificationService.InboxAsync"/> grows its filter overload and
/// <see cref="INotificationService.MarkAllReadAsync"/> ships.
/// </para>
/// </remarks>
public sealed class NotificationServiceInboxFilterTests
{
    /// <summary>Deterministic anchor for "now" across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task InboxAsync_UnreadOnlyFilter_ReturnsOnlyUnreadRows()
    {
        // Arrange — three notifications, two unread, one already read.
        var harness = await Harness.CreateAsync();
        await harness.SeedNotificationAsync("Subj A", channel: NotificationChannel.InApp, readAtUtc: null);
        await harness.SeedNotificationAsync("Subj B", channel: NotificationChannel.Email, readAtUtc: ClockNow.AddDays(-1));
        await harness.SeedNotificationAsync("Subj C", channel: NotificationChannel.InApp, readAtUtc: null);

        // Act
        var result = await harness.Service.InboxAsync(
            new NotificationInboxQuery(new PageRequest(1, 20), UnreadOnly: true, Channel: null));

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        var expectedSubjects = new[] { "Subj A", "Subj C" };
        result.Value.Items.Select(i => i.Subject).Should().BeEquivalentTo(expectedSubjects);
    }

    [Fact]
    public async Task InboxAsync_ChannelFilter_ReturnsOnlyRowsOnRequestedChannel()
    {
        var harness = await Harness.CreateAsync();
        await harness.SeedNotificationAsync("Email row", channel: NotificationChannel.Email, readAtUtc: null);
        await harness.SeedNotificationAsync("Inapp row", channel: NotificationChannel.InApp, readAtUtc: null);
        await harness.SeedNotificationAsync("Sms row", channel: NotificationChannel.Sms, readAtUtc: null);

        var result = await harness.Service.InboxAsync(
            new NotificationInboxQuery(new PageRequest(1, 20), UnreadOnly: false, Channel: NotificationChannelCodes.Email));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Single().Subject.Should().Be("Email row");
    }

    [Fact]
    public async Task InboxAsync_PaginationContract_ClampsAndSkipsCorrectly()
    {
        var harness = await Harness.CreateAsync();
        // Seed 25 notifications so we can probe both page 1 and page 2 of a 20-page slice.
        for (var i = 0; i < 25; i++)
        {
            await harness.SeedNotificationAsync($"Subj {i:D2}", channel: NotificationChannel.InApp,
                readAtUtc: null, createdOffsetMinutes: i);
        }

        // Page 2 of pageSize 20 should yield items #20..#24 — the OLDEST 5 (we order descending by created).
        var result = await harness.Service.InboxAsync(
            new NotificationInboxQuery(new PageRequest(2, 20), UnreadOnly: false, Channel: null));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(25);
        result.Value.Items.Should().HaveCount(5, "page 2 of pageSize 20 over 25 rows returns the trailing 5");
        result.Value.HasNext.Should().BeFalse();
    }

    [Fact]
    public async Task MarkAllReadAsync_FlipsEveryUnreadRowToReadAndReturnsCount()
    {
        // Arrange — mix of read + unread rows; expect only the unread rows to be touched.
        var harness = await Harness.CreateAsync();
        await harness.SeedNotificationAsync("A", channel: NotificationChannel.InApp, readAtUtc: null);
        await harness.SeedNotificationAsync("B", channel: NotificationChannel.InApp, readAtUtc: null);
        var alreadyRead = await harness.SeedNotificationAsync(
            "C-already-read", channel: NotificationChannel.InApp, readAtUtc: ClockNow.AddDays(-2));

        var result = await harness.Service.MarkAllReadAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2, "exactly two rows were unread; the third was already read");

        var rows = await harness.Db.Notifications
            .Where(n => n.RecipientUserId == harness.RecipientId)
            .ToListAsync();
        rows.Should().HaveCount(3);
        rows.Where(r => r.Id != alreadyRead.Id).Should().OnlyContain(r => r.ReadAtUtc == ClockNow,
            "the unread rows must have ReadAtUtc stamped to the clock");
        rows.Single(r => r.Id == alreadyRead.Id).ReadAtUtc.Should().Be(ClockNow.AddDays(-2),
            "already-read rows must not be re-stamped");
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>Deterministic clock for tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUT and its collaborators for compact tests.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required NotificationService Service { get; init; }
        public required long RecipientId { get; init; }

        /// <summary>Creates a freshly-seeded harness with a deterministic recipient.</summary>
        public static async Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-notif-filter-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var clock = new StubClock(ClockNow);
            var sqids = new SqidService(Options.Create(new SqidOptions
            {
                Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                MinLength = 6,
            }));
            var mnotify = Substitute.For<IMNotifyClient>();
            var caller = Substitute.For<ICallerContext>();

            var recipient = new UserProfile
            {
                MPassSubject = "uc22-history-recipient",
                DisplayName = "History Recipient",
                Email = "history@example.md",
                NationalId = "0000000000099",
                PreferredLanguage = "ro",
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            db.UserProfiles.Add(recipient);
            await db.SaveChangesAsync();

            caller.UserId.Returns(recipient.Id);
            caller.UserSqid.Returns(sqids.Encode(recipient.Id));

            var service = new NotificationService(db, clock, sqids, mnotify, caller);
            return new Harness { Db = db, Service = service, RecipientId = recipient.Id };
        }

        /// <summary>
        /// Seeds a notification row for the harness recipient. The <paramref name="createdOffsetMinutes"/>
        /// parameter lets callers stagger CreatedAtUtc so ordering assertions are stable.
        /// </summary>
        public async Task<Notification> SeedNotificationAsync(
            string subject,
            NotificationChannel channel,
            DateTime? readAtUtc,
            int createdOffsetMinutes = 0)
        {
            var row = new Notification
            {
                CreatedAtUtc = ClockNow.AddMinutes(-createdOffsetMinutes),
                RecipientUserId = RecipientId,
                Channel = channel,
                Subject = subject,
                Body = "body",
                DeliveryStatus = NotificationDeliveryStatus.Delivered,
                ReadAtUtc = readAtUtc,
                IsActive = true,
            };
            Db.Notifications.Add(row);
            await Db.SaveChangesAsync();
            return row;
        }
    }
}
