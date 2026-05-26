using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Suppression-branch tests for <see cref="NotificationService.EnqueueAsync"/>
/// (R0171 — CF 22.02 / CF 04.08). Confirms the per-channel opt-out flag on the
/// recipient's <c>UserProfile.NotificationPreferences</c> persists a row with
/// <see cref="NotificationDeliveryStatus.Suppressed"/> and skips the actual send.
/// </summary>
/// <remarks>
/// <para>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the production code that
/// adds the suppression branch. They pin down five invariants:
/// </para>
/// <list type="bullet">
///   <item>Opt-out for Email persists the email row as Suppressed and never calls MNotify.</item>
///   <item>Opt-in for Email persists the email row as Delivered and DOES call MNotify.</item>
///   <item>Null preferences JSON behaves as default-opt-in (back-compat for pre-R0171 rows).</item>
///   <item>Malformed preferences JSON fails OPEN as default-opt-in (dispatcher invariant).</item>
///   <item>Each suppressed row increments <c>CnasMeter.NotificationSuppressed</c>.</item>
/// </list>
/// <para>
/// Member of <see cref="CnasMeterCollection"/> — the suppression-counter assertion
/// is sensitive to cross-test pollution because the meter is process-static.
/// </para>
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class NotificationServiceSuppressionTests
{
    /// <summary>Deterministic clock anchor for all tests in this file.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Constant national id used for the test recipient — keeps MNotify happy.</summary>
    private const string TestIdnp = "0000000000000";

    [Fact]
    public async Task Enqueue_RecipientOptedOutOfEmail_PersistsRowAsSuppressed_AndSkipsSend()
    {
        // Arrange — recipient explicitly opted OUT of Email but kept the in-app inbox.
        var harness = await Harness.CreateAsync(prefs: new NotificationPreferences
        {
            Email = false,
            Sms = true,
            InApp = true,
        });

        // Act
        var result = await harness.Service.EnqueueAsync(
            harness.RecipientId, subject: "Hello", body: "Body", correlationId: "corr-1");

        // Assert — success + an Email row exists as Suppressed + no MNotify call.
        result.IsSuccess.Should().BeTrue();
        var emailRow = await harness.Db.Notifications
            .SingleAsync(n => n.RecipientUserId == harness.RecipientId
                              && n.Channel == NotificationChannel.Email);
        emailRow.DeliveryStatus.Should().Be(NotificationDeliveryStatus.Suppressed);
        // The in-app row is still Delivered (InApp was opted IN).
        var inAppRow = await harness.Db.Notifications
            .SingleAsync(n => n.RecipientUserId == harness.RecipientId
                              && n.Channel == NotificationChannel.InApp);
        inAppRow.DeliveryStatus.Should().Be(NotificationDeliveryStatus.Delivered);
        // No upstream send for the suppressed channel.
        await harness.MNotify.DidNotReceive().SendAsync(Arg.Any<MNotifyMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enqueue_RecipientOptedInToEmail_PersistsRowAsDelivered_AndCallsSendPath()
    {
        // Arrange — all opt-ins, MNotify returns success.
        var harness = await Harness.CreateAsync(prefs: NotificationPreferences.Default);
        harness.MNotify.SendAsync(Arg.Any<MNotifyMessage>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await harness.Service.EnqueueAsync(
            harness.RecipientId, "Hello", "Body", "corr-2");

        // Assert — email row Delivered + MNotify called once.
        result.IsSuccess.Should().BeTrue();
        var emailRow = await harness.Db.Notifications
            .SingleAsync(n => n.RecipientUserId == harness.RecipientId
                              && n.Channel == NotificationChannel.Email);
        emailRow.DeliveryStatus.Should().Be(NotificationDeliveryStatus.Delivered);
        await harness.MNotify.Received(1).SendAsync(Arg.Any<MNotifyMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enqueue_RecipientNullPrefs_BehavesAsDefault_AllChannelsAllowed()
    {
        // Arrange — recipient profile carries NULL preferences (pre-R0171 row).
        var harness = await Harness.CreateAsync(rawPrefsJson: null);
        harness.MNotify.SendAsync(Arg.Any<MNotifyMessage>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await harness.Service.EnqueueAsync(
            harness.RecipientId, "Hello", "Body", "corr-3");

        // Assert — back-compat: NULL means default opt-in, so the email is sent.
        result.IsSuccess.Should().BeTrue();
        var emailRow = await harness.Db.Notifications
            .SingleOrDefaultAsync(n => n.RecipientUserId == harness.RecipientId
                                       && n.Channel == NotificationChannel.Email);
        emailRow.Should().NotBeNull();
        emailRow!.DeliveryStatus.Should().Be(NotificationDeliveryStatus.Delivered);
        await harness.MNotify.Received(1).SendAsync(Arg.Any<MNotifyMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enqueue_MalformedPrefsJson_FailsOpen_AsDefault()
    {
        // Arrange — malformed JSON column. The dispatcher must NOT silently drop the
        // notification — it falls back to default-opt-in (the fail-open contract).
        var harness = await Harness.CreateAsync(rawPrefsJson: "{not-valid-json");
        harness.MNotify.SendAsync(Arg.Any<MNotifyMessage>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await harness.Service.EnqueueAsync(
            harness.RecipientId, "Hello", "Body", "corr-4");

        // Assert — same as the null-prefs case: email dispatched as Delivered.
        result.IsSuccess.Should().BeTrue();
        var emailRow = await harness.Db.Notifications
            .SingleOrDefaultAsync(n => n.RecipientUserId == harness.RecipientId
                                       && n.Channel == NotificationChannel.Email);
        emailRow.Should().NotBeNull();
        emailRow!.DeliveryStatus.Should().Be(NotificationDeliveryStatus.Delivered);
        await harness.MNotify.Received(1).SendAsync(Arg.Any<MNotifyMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enqueue_Suppressed_IncrementsCnasMeterCounter()
    {
        // Arrange — opted OUT of Email so we expect a single counter increment.
        using var capture = new MetricCapture("cnas.notification.suppressed");
        var harness = await Harness.CreateAsync(prefs: new NotificationPreferences
        {
            Email = false,
            Sms = true,
            InApp = true,
        });

        // Act
        var result = await harness.Service.EnqueueAsync(
            harness.RecipientId, "Hello", "Body", "corr-5");

        // Assert — exactly one increment recorded for the single suppressed channel.
        result.IsSuccess.Should().BeTrue();
        capture.TotalIncrement.Should().Be(1L,
            "exactly one suppression event must be recorded for one opted-out channel.");
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>Deterministic clock for tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// MeterListener-based capture for a single instrument name on
    /// <see cref="CnasMeter.MeterName"/>. Disposes the listener at the end of the test
    /// so the next test starts from a clean slate. Mirrors the helper in
    /// <c>MissingDocsSlaJobTests</c> — kept private here to avoid cross-file coupling.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly List<long> _measurements = new();
        private readonly object _gate = new();

        public long TotalIncrement
        {
            get { lock (_gate) { return _measurements.Sum(); } }
        }

        public MetricCapture(string instrumentName)
        {
            _listener = new System.Diagnostics.Metrics.MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName
                        && instrument.Name == instrumentName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                lock (_gate) { _measurements.Add(value); }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Bundles the SUT and its collaborators for compact tests.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required NotificationService Service { get; init; }
        public required IMNotifyClient MNotify { get; init; }
        public required ICallerContext Caller { get; init; }
        public required long RecipientId { get; init; }

        /// <summary>Creates a harness with the supplied parsed preferences.</summary>
        public static Task<Harness> CreateAsync(NotificationPreferences prefs)
            => CreateInternalAsync(NotificationPreferencesJson.Serialize(prefs));

        /// <summary>Creates a harness storing the supplied raw JSON (or null) verbatim.</summary>
        public static Task<Harness> CreateAsync(string? rawPrefsJson)
            => CreateInternalAsync(rawPrefsJson);

        private static async Task<Harness> CreateInternalAsync(string? prefsJson)
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-notif-suppress-{Guid.NewGuid():N}")
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
                MPassSubject = "uc22-recipient",
                DisplayName = "Recipient",
                Email = "recipient@example.md",
                NationalId = TestIdnp,
                PreferredLanguage = "ro",
                NotificationPreferences = prefsJson,
                CreatedAtUtc = ClockNow.AddDays(-1),
                IsActive = true,
            };
            db.UserProfiles.Add(recipient);
            await db.SaveChangesAsync();

            var service = new NotificationService(db, clock, sqids, mnotify, caller);

            return new Harness
            {
                Db = db,
                Service = service,
                MNotify = mnotify,
                Caller = caller,
                RecipientId = recipient.Id,
            };
        }
    }
}
