using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0593 / TOR CF 10.04 — pins the behaviour that the
/// <see cref="DecisionWorkflowService"/> fires a single
/// <see cref="NotificationTriggerKind.ActionResult"/> trigger on every
/// terminal decision (approve / reject) so the citizen inbox shows the
/// verdict per CF 10.04. The trigger payload anchors the deep-link to the
/// underlying <see cref="ServiceApplication"/> via
/// <see cref="NotificationRelatedEntityTypes.Application"/>.
/// </summary>
/// <remarks>
/// The existing <see cref="DecisionWorkflowServiceTests"/> suite covers the
/// state-machine + audit + MCabinet outputs but does NOT assert on the
/// optional <see cref="INotificationTriggerDispatcher"/> collaborator —
/// these tests pin the iter-127 verification note that R0593 is fully
/// wired end-to-end.
/// </remarks>
public sealed class DecisionWorkflowNotificationTriggerTests
{
    /// <summary>Deterministic clock so the dispatched payload's correlation/subject are stable.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ApproveAsync_HappyPath_DispatchesActionResultTrigger()
    {
        // Arrange — wire the SUT with a spy trigger dispatcher so the test can
        // assert that the ActionResult notification was emitted with the right
        // recipient (the citizen solicitant) and the right related-entity link
        // (the Application, NOT the Dossier).
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        // Act
        var result = await harness.Service.ApproveAsync("DOSS-SQID", note: "OK");

        // Assert
        result.IsSuccess.Should().BeTrue();
        await harness.Triggers.Received(1).DispatchAsync(
            NotificationTriggerKind.ActionResult,
            Arg.Is<NotificationTriggerPayload>(p =>
                p.RecipientUserId == seeded.SolicitantId
                && p.RelatedEntityType == NotificationRelatedEntityTypes.Application
                && p.RelatedEntityId == seeded.AppId
                && p.Subject.Contains("aprobat", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RejectAsync_HappyPath_DispatchesActionResultTriggerWithRejectionSubject()
    {
        // Arrange
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        // Act
        var result = await harness.Service.RejectAsync("DOSS-SQID", reason: "Documente lipsa.");

        // Assert — the trigger MUST fire on rejection too (CF 10.04 covers
        // both decision branches) and the subject MUST differentiate it from
        // the approval payload so the inbox UI can render the correct icon.
        result.IsSuccess.Should().BeTrue();
        await harness.Triggers.Received(1).DispatchAsync(
            NotificationTriggerKind.ActionResult,
            Arg.Is<NotificationTriggerPayload>(p =>
                p.RecipientUserId == seeded.SolicitantId
                && p.RelatedEntityType == NotificationRelatedEntityTypes.Application
                && p.RelatedEntityId == seeded.AppId
                && p.Subject.Contains("resp", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_DispatchedBody_CarriesApplicationReferenceNumber_R0605()
    {
        // R0605 / TOR CF 11.07 — at the moment a decision is approved the
        // citizen's documents become "available" (the Decizia + Fișa de
        // calcul are persisted) and the recipient-notification fires
        // through the same ActionResult trigger. The notification body
        // must carry the application reference number so the citizen can
        // locate the issued documents inside their MCabinet inbox without
        // relying on the deep-link alone.
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.ApproveAsync("DOSS-SQID", note: "Documents issued.");

        result.IsSuccess.Should().BeTrue();
        await harness.Triggers.Received(1).DispatchAsync(
            NotificationTriggerKind.ActionResult,
            Arg.Is<NotificationTriggerPayload>(p =>
                p.RecipientUserId == seeded.SolicitantId
                && p.Body.Contains("PS-NTR-0001", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_TriggerDispatchFails_StateMachineUnaffected()
    {
        // Best-effort contract — the dispatcher throwing MUST NOT roll back
        // the persisted approval. Mirrors the MCabinet best-effort pattern
        // documented in the production service.
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        harness.Triggers
            .DispatchAsync(Arg.Any<NotificationTriggerKind>(), Arg.Any<NotificationTriggerPayload>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new InvalidOperationException("dispatcher offline"));

        var result = await harness.Service.ApproveAsync("DOSS-SQID", note: "OK");

        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Approved);
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-notif-trigger-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning a fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Identifiers of seeded entities returned to the test for assertion targeting.</summary>
    private sealed record SeedResult(long AppId, long DossierId, long SolicitantId);

    /// <summary>Bundles the SUT and its mock collaborators.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required DecisionWorkflowService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required INotificationTriggerDispatcher Triggers { get; init; }

        /// <summary>
        /// Wires the SUT with NSubstitute fakes including the
        /// <see cref="INotificationTriggerDispatcher"/> spy this suite is
        /// focused on.
        /// </summary>
        public static Harness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-CALLER");
            caller.UserId.Returns(1L);
            caller.Roles.Returns(["cnas-decider"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-127");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var triggers = Substitute.For<INotificationTriggerDispatcher>();
            triggers
                .DispatchAsync(Arg.Any<NotificationTriggerKind>(), Arg.Any<NotificationTriggerPayload>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var service = new DecisionWorkflowService(
                db, sqids, clock, caller, audit, mcabinet,
                NullLogger<DecisionWorkflowService>.Instance,
                budget: null, qbeConverter: null, accessScopeFilter: null,
                triggers: triggers);
            return new Harness { Db = db, Service = service, Sqids = sqids, Triggers = triggers };
        }

        /// <summary>
        /// Seeds a coherent solicitant + passport + application + dossier graph
        /// and binds the canonical <c>"DOSS-SQID"</c> sqid → dossier.Id mapping
        /// the tests rely on.
        /// </summary>
        public async Task<SeedResult> SeedAsync()
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = "2000000000017",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Maria Test",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP-NTR",
                NameRo = "Test pas-notif",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-NTR",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow,
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                SubmittedAtUtc = ClockNow.AddDays(-1),
                ReferenceNumber = "PS-NTR-0001",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = "D-2026-NTR1",
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            app.DossierId = dossier.Id;
            await Db.SaveChangesAsync();

            Sqids.TryDecode("DOSS-SQID").Returns(Result<long>.Success(dossier.Id));

            return new SeedResult(app.Id, dossier.Id, solicitant.Id);
        }
    }
}
