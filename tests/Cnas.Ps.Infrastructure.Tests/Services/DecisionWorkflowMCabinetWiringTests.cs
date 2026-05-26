using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests covering MCabinet outbound publish wiring on the
/// <see cref="DecisionWorkflowService"/> decider transitions:
/// <c>ApproveAsync → Approved</c> and <c>RejectAsync → Rejected</c>. The publisher is
/// substituted via NSubstitute; failures must be swallowed (best-effort projection — the
/// dossier state change is the source of truth, not the citizen-portal card).
/// </summary>
/// <remarks>
/// CLAUDE.md cross-cutting: "Idempotent Callbacks" plus the project rule that publish
/// failures are best-effort projections — the decider's verdict commits regardless of any
/// citizen-portal projection failure.
/// </remarks>
public class DecisionWorkflowMCabinetWiringTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);
    private const string DossierSqid = "DOSS-SQID";
    private const string SolicitantIdnp = "2000000000007";
    private const string PassportCode = "SP-TEST";
    private const string PassportNameRo = "Test passport";

    private static readonly string[] DeciderRoles = ["cnas-decider"];
    private static readonly string[] NonDeciderRoles = ["cnas-examiner"];

    // ─────────────────────── ApproveAsync ───────────────────────

    [Fact]
    public async Task ApproveAsync_Success_PublishesApprovedCard()
    {
        var harness = Harness.Create();
        await harness.SeedAsync();

        var result = await harness.Service.ApproveAsync(DossierSqid, note: "ok");

        result.IsSuccess.Should().BeTrue();
        await harness.MCabinet.Received(1).PublishCardAsync(
            Arg.Is<MCabinetCard>(c =>
                !string.IsNullOrWhiteSpace(c.ExternalId)
                && c.CitizenIdnp == SolicitantIdnp
                && c.ServiceCode == PassportCode
                && c.Status == MCabinetStatus.Approved
                && c.TitleRo == PassportNameRo
                && c.EventUtc == ClockNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_PublisherFails_StillReturnsSuccess()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        harness.MCabinet
            .PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.MCabinetPublishFailed, "Upstream MCabinet down."));

        var result = await harness.Service.ApproveAsync(DossierSqid, note: "ok");

        // Best-effort projection — approval must persist regardless of the citizen-card publish.
        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Approved);
    }

    [Fact]
    public async Task ApproveAsync_PublisherThrows_DoesNotCrash()
    {
        // Defense in depth — even if the publisher throws an unhandled exception
        // (e.g. a misconfigured Polly retry pipeline), the decider transition must not
        // crash. The wiring wraps the call in try/catch and logs a warning.
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        harness.MCabinet
            .PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new InvalidOperationException("boom"));

        var result = await harness.Service.ApproveAsync(DossierSqid, note: "ok");

        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Approved);
        // The substitute logger received at least one warning, proving the catch-block ran.
        harness.Logger.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(ILogger.Log)
                      && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning)
            .Should().BeTrue();
    }

    [Fact]
    public async Task ApproveAsync_CallerLacksDeciderRole_DoesNotPublish()
    {
        var harness = Harness.Create(roles: NonDeciderRoles);
        await harness.SeedAsync();

        var result = await harness.Service.ApproveAsync(DossierSqid, note: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.WorkflowNotDecider);
        await harness.MCabinet.DidNotReceive().PublishCardAsync(
            Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_DossierNotFound_DoesNotPublish()
    {
        var harness = Harness.Create();
        // No seed → dossier lookup returns null and the service short-circuits with NotFound.
        harness.Sqids.TryDecode(DossierSqid).Returns(Result<long>.Success(99999L));

        var result = await harness.Service.ApproveAsync(DossierSqid, note: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        await harness.MCabinet.DidNotReceive().PublishCardAsync(
            Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>());
    }

    // ─────────────────────── RejectAsync ───────────────────────

    [Fact]
    public async Task RejectAsync_Success_PublishesRejectedCard()
    {
        var harness = Harness.Create();
        await harness.SeedAsync();

        var result = await harness.Service.RejectAsync(DossierSqid, reason: "Documente lipsa.");

        result.IsSuccess.Should().BeTrue();
        await harness.MCabinet.Received(1).PublishCardAsync(
            Arg.Is<MCabinetCard>(c =>
                c.CitizenIdnp == SolicitantIdnp
                && c.ServiceCode == PassportCode
                && c.Status == MCabinetStatus.Rejected
                && c.EventUtc == ClockNow),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-mc-dec-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record SeedResult(long DossierId, long AppId, long SolicitantId, long PassportId);

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required DecisionWorkflowService Service { get; init; }
        public required IMCabinetPublisher MCabinet { get; init; }
        public required ILogger<DecisionWorkflowService> Logger { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }

        public static Harness Create(IReadOnlyCollection<string>? roles = null)
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
            caller.Roles.Returns(roles ?? DeciderRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var logger = Substitute.For<ILogger<DecisionWorkflowService>>();

            var service = new DecisionWorkflowService(db, sqids, clock, caller, audit, mcabinet, logger);
            return new Harness
            {
                Db = db,
                Service = service,
                MCabinet = mcabinet,
                Logger = logger,
                Caller = caller,
                Sqids = sqids,
            };
        }

        public async Task<SeedResult> SeedAsync()
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = SolicitantIdnp,
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Ion Popescu",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = PassportCode,
                NameRo = PassportNameRo,
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-TEST",
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
                ReferenceNumber = "PS-TEST-0001",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = "D-2026-ABCD1234",
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            app.DossierId = dossier.Id;
            await Db.SaveChangesAsync();

            Sqids.TryDecode(DossierSqid).Returns(Result<long>.Success(dossier.Id));

            return new SeedResult(dossier.Id, app.Id, solicitant.Id, passport.Id);
        }
    }
}
