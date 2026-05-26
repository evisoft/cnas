using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Notifications;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0172 / TOR CF 22.05 — service-level integration tests proving that
/// <see cref="NotificationService.InboxAsync(NotificationInboxQuery, CancellationToken)"/>
/// pipes the persisted <c>RelatedEntityType</c> + <c>RelatedEntityId</c>
/// columns through the wired <see cref="INotificationDeepLinkResolver"/> and
/// emits the resolved URL on <see cref="NotificationOutput.DeepLinkUrl"/>.
/// </summary>
/// <remarks>
/// These tests are written test-first per CLAUDE.md RULE 1 — they cover the
/// resolver wiring inside the service layer (the resolver itself is unit-
/// tested separately by <see cref="Notifications.NotificationDeepLinkResolverTests"/>).
/// </remarks>
public sealed class NotificationServiceDeepLinkTests
{
    /// <summary>Deterministic anchor for "now" across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task InboxAsync_RowWithRelatedEntity_PopulatesDeepLinkUrl()
    {
        // Arrange — one notification anchored to Application#4523.
        var harness = await Harness.CreateAsync(withResolver: true);
        await harness.SeedNotificationAsync(
            subject: "Decision ready",
            relatedEntityType: NotificationRelatedEntityTypes.Application,
            relatedEntityId: 4523L);

        // Act
        var result = await harness.Service.InboxAsync(
            new NotificationInboxQuery(new PageRequest(1, 20)));

        // Assert — DeepLinkUrl reflects the resolver's encoded route.
        result.IsSuccess.Should().BeTrue();
        var row = result.Value.Items.Single();
        var expected = $"/applications/{harness.Sqids.Encode(4523L)}";
        row.DeepLinkUrl.Should().Be(expected);
    }

    [Fact]
    public async Task InboxAsync_RowWithoutRelatedEntity_LeavesDeepLinkUrlNull()
    {
        // Arrange — one notification with NO related-entity columns set.
        var harness = await Harness.CreateAsync(withResolver: true);
        await harness.SeedNotificationAsync(
            subject: "Generic broadcast",
            relatedEntityType: null,
            relatedEntityId: null);

        var result = await harness.Service.InboxAsync(
            new NotificationInboxQuery(new PageRequest(1, 20)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Single().DeepLinkUrl.Should().BeNull();
    }

    [Fact]
    public async Task InboxAsync_RowWithUnknownType_LeavesDeepLinkUrlNull()
    {
        // Arrange — type the resolver does not recognise.
        var harness = await Harness.CreateAsync(withResolver: true);
        await harness.SeedNotificationAsync(
            subject: "Mystery entity",
            relatedEntityType: "UnknownAlienType",
            relatedEntityId: 99L);

        var result = await harness.Service.InboxAsync(
            new NotificationInboxQuery(new PageRequest(1, 20)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Single().DeepLinkUrl.Should().BeNull();
    }

    [Fact]
    public async Task InboxAsync_NoResolverWired_LeavesDeepLinkUrlNull()
    {
        // Arrange — legacy DI scope: resolver NOT supplied. The constructor's
        // nullable parameter means the service still works, but the DeepLinkUrl
        // is unconditionally null even for rows that DO carry related-entity
        // columns. This pins the back-compat contract.
        var harness = await Harness.CreateAsync(withResolver: false);
        await harness.SeedNotificationAsync(
            subject: "Decision ready",
            relatedEntityType: NotificationRelatedEntityTypes.Application,
            relatedEntityId: 4523L);

        var result = await harness.Service.InboxAsync(
            new NotificationInboxQuery(new PageRequest(1, 20)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Single().DeepLinkUrl.Should().BeNull();
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
        public required ISqidService Sqids { get; init; }

        /// <summary>
        /// Creates a freshly-seeded harness. When <paramref name="withResolver"/>
        /// is true the deep-link resolver is wired into the service constructor;
        /// otherwise the legacy 5-arg constructor path is used.
        /// </summary>
        public static async Task<Harness> CreateAsync(bool withResolver)
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-notif-deeplink-{Guid.NewGuid():N}")
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
                MPassSubject = "uc22-deeplink-recipient",
                DisplayName = "DeepLink Recipient",
                Email = "deeplink@example.md",
                NationalId = "0000000000077",
                PreferredLanguage = "ro",
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            db.UserProfiles.Add(recipient);
            await db.SaveChangesAsync();

            caller.UserId.Returns(recipient.Id);
            caller.UserSqid.Returns(sqids.Encode(recipient.Id));

            INotificationDeepLinkResolver? resolver =
                withResolver ? new NotificationDeepLinkResolver(sqids) : null;

            var service = new NotificationService(db, clock, sqids, mnotify, caller, resolver);
            return new Harness
            {
                Db = db,
                Service = service,
                RecipientId = recipient.Id,
                Sqids = sqids,
            };
        }

        /// <summary>
        /// Seeds a notification row anchored (or not) to a related business
        /// object. <paramref name="relatedEntityType"/> and
        /// <paramref name="relatedEntityId"/> may be <c>null</c> to model the
        /// legacy "no business object" row shape.
        /// </summary>
        public async Task<Notification> SeedNotificationAsync(
            string subject,
            string? relatedEntityType,
            long? relatedEntityId)
        {
            var row = new Notification
            {
                CreatedAtUtc = ClockNow,
                RecipientUserId = RecipientId,
                Channel = NotificationChannel.InApp,
                Subject = subject,
                Body = "body",
                DeliveryStatus = NotificationDeliveryStatus.Delivered,
                IsActive = true,
                RelatedEntityType = relatedEntityType,
                RelatedEntityId = relatedEntityId,
            };
            Db.Notifications.Add(row);
            await Db.SaveChangesAsync();
            return row;
        }
    }
}
