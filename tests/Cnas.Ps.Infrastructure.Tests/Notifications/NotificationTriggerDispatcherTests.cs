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

namespace Cnas.Ps.Infrastructure.Tests.Notifications;

/// <summary>
/// R0174 / TOR CF 22.03 — service-level tests for
/// <see cref="NotificationTriggerDispatcher"/>. The dispatcher funnels every
/// canonical trigger (TaskAssignment / SlaBreach / ApprovalNeeded /
/// ActionResult / PerformanceAlert) through
/// <see cref="INotificationService.EnqueueAsync"/> so the inbox row reflects
/// the related-entity pair (for deep-link, R0172) and the MNotify mirror
/// honours the per-channel opt-out preferences (R0171).
/// </summary>
/// <remarks>
/// Tests are written test-first per CLAUDE.md RULE 1 — they cover each of the
/// five trigger kinds end-to-end against a fresh InMemory <see cref="CnasDbContext"/>.
/// </remarks>
public sealed class NotificationTriggerDispatcherTests
{
    /// <summary>Deterministic anchor for "now" across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DispatchAsync_TaskAssignment_PersistsInAppRow_WithWorkflowTaskAnchor()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Dispatcher.DispatchAsync(
            NotificationTriggerKind.TaskAssignment,
            new NotificationTriggerPayload(
                RecipientUserId: harness.RecipientId,
                Subject: "Sarcină nouă",
                Body: "Ați primit o sarcină nouă.",
                CorrelationId: "wf-corr-1",
                RelatedEntityType: NotificationRelatedEntityTypes.WorkflowTask,
                RelatedEntityId: 4242L));

        result.IsSuccess.Should().BeTrue();
        var inAppRow = await harness.Db.Notifications
            .SingleAsync(n => n.RecipientUserId == harness.RecipientId
                              && n.Channel == NotificationChannel.InApp);
        inAppRow.Subject.Should().Be("Sarcină nouă");
        inAppRow.RelatedEntityType.Should().Be(NotificationRelatedEntityTypes.WorkflowTask);
        inAppRow.RelatedEntityId.Should().Be(4242L);
        inAppRow.CorrelationId.Should().Be("wf-corr-1");
    }

    [Fact]
    public async Task DispatchAsync_SlaBreach_PersistsInAppRow_WithWorkflowTaskAnchor()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Dispatcher.DispatchAsync(
            NotificationTriggerKind.SlaBreach,
            new NotificationTriggerPayload(
                RecipientUserId: harness.RecipientId,
                Subject: "Sarcină depășită",
                Body: "Sarcina X a depășit termenul.",
                CorrelationId: "sla-corr-1",
                RelatedEntityType: NotificationRelatedEntityTypes.WorkflowTask,
                RelatedEntityId: 9001L));

        result.IsSuccess.Should().BeTrue();
        var inAppRow = await harness.Db.Notifications
            .SingleAsync(n => n.RecipientUserId == harness.RecipientId
                              && n.Channel == NotificationChannel.InApp);
        inAppRow.RelatedEntityType.Should().Be(NotificationRelatedEntityTypes.WorkflowTask);
        inAppRow.RelatedEntityId.Should().Be(9001L);
    }

    [Fact]
    public async Task DispatchAsync_ApprovalNeeded_PersistsInAppRow_WithDossierAnchor()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Dispatcher.DispatchAsync(
            NotificationTriggerKind.ApprovalNeeded,
            new NotificationTriggerPayload(
                RecipientUserId: harness.RecipientId,
                Subject: "Aprobare necesară",
                Body: "Dosarul X așteaptă aprobare.",
                CorrelationId: "approve-corr-1",
                RelatedEntityType: NotificationRelatedEntityTypes.Dossier,
                RelatedEntityId: 17L));

        result.IsSuccess.Should().BeTrue();
        var inAppRow = await harness.Db.Notifications
            .SingleAsync(n => n.RecipientUserId == harness.RecipientId
                              && n.Channel == NotificationChannel.InApp);
        inAppRow.Subject.Should().Be("Aprobare necesară");
        inAppRow.RelatedEntityType.Should().Be(NotificationRelatedEntityTypes.Dossier);
        inAppRow.RelatedEntityId.Should().Be(17L);
    }

    [Fact]
    public async Task DispatchAsync_ActionResult_PersistsInAppRow_WithApplicationAnchor()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Dispatcher.DispatchAsync(
            NotificationTriggerKind.ActionResult,
            new NotificationTriggerPayload(
                RecipientUserId: harness.RecipientId,
                Subject: "Cererea aprobată",
                Body: "Cererea dvs. a fost aprobată.",
                CorrelationId: "dec-corr-1",
                RelatedEntityType: NotificationRelatedEntityTypes.Application,
                RelatedEntityId: 4523L));

        result.IsSuccess.Should().BeTrue();
        var inAppRow = await harness.Db.Notifications
            .SingleAsync(n => n.RecipientUserId == harness.RecipientId
                              && n.Channel == NotificationChannel.InApp);
        inAppRow.RelatedEntityType.Should().Be(NotificationRelatedEntityTypes.Application);
        inAppRow.RelatedEntityId.Should().Be(4523L);
    }

    [Fact]
    public async Task DispatchAsync_PerformanceAlert_PersistsInAppRow_WithReportRunAnchor()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Dispatcher.DispatchAsync(
            NotificationTriggerKind.PerformanceAlert,
            new NotificationTriggerPayload(
                RecipientUserId: harness.RecipientId,
                Subject: "Raport încet",
                Body: "Raportul rulează de mai mult de 5 minute.",
                CorrelationId: "perf-corr-1",
                RelatedEntityType: NotificationRelatedEntityTypes.ReportRun,
                RelatedEntityId: 71L));

        result.IsSuccess.Should().BeTrue();
        var inAppRow = await harness.Db.Notifications
            .SingleAsync(n => n.RecipientUserId == harness.RecipientId
                              && n.Channel == NotificationChannel.InApp);
        inAppRow.RelatedEntityType.Should().Be(NotificationRelatedEntityTypes.ReportRun);
        inAppRow.RelatedEntityId.Should().Be(71L);
    }

    [Fact]
    public async Task DispatchAsync_NullPayload_Throws()
    {
        var harness = await Harness.CreateAsync();
        Func<Task> act = () => harness.Dispatcher.DispatchAsync(
            NotificationTriggerKind.TaskAssignment,
            payload: null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ───────────────────────── helpers ─────────────────────────

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required INotificationTriggerDispatcher Dispatcher { get; init; }
        public required long RecipientId { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-trig-disp-{Guid.NewGuid():N}")
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
            mnotify.SendAsync(Arg.Any<MNotifyMessage>(), Arg.Any<CancellationToken>())
                .Returns(Result.Success());
            var caller = Substitute.For<ICallerContext>();

            var recipient = new UserProfile
            {
                MPassSubject = "uc22-trigger-recipient",
                DisplayName = "Trigger Recipient",
                Email = "trigger@example.md",
                NationalId = "0000000000088",
                PreferredLanguage = "ro",
                CreatedAtUtc = ClockNow.AddDays(-10),
                IsActive = true,
            };
            db.UserProfiles.Add(recipient);
            await db.SaveChangesAsync();
            caller.UserId.Returns(recipient.Id);
            caller.UserSqid.Returns(sqids.Encode(recipient.Id));

            var resolver = new NotificationDeepLinkResolver(sqids);
            var notification = new NotificationService(db, clock, sqids, mnotify, caller, resolver);
            var dispatcher = new NotificationTriggerDispatcher(notification);

            return new Harness
            {
                Db = db,
                Dispatcher = dispatcher,
                RecipientId = recipient.Id,
            };
        }
    }
}
