using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests covering MCabinet outbound publish wiring on the
/// <see cref="ApplicationProcessingService.AdvanceAsync"/> pipeline. The eligible branch
/// opens a dossier and must publish an <see cref="MCabinetStatus.InExamination"/> card;
/// the auto-reject branch (engine failure / ineligible) closes the application and must
/// publish an <see cref="MCabinetStatus.Rejected"/> card keyed by the application Sqid
/// (no dossier exists yet on that path). The publisher is substituted; failures are
/// swallowed (best-effort projection — the dossier state change is the source of truth).
/// </summary>
public class ApplicationProcessingMCabinetWiringTests
{
    private const string SolicitantNationalId = "2000000000007";
    private const string PassportCode = "SP-TEST";
    private const string PassportNameRo = "Test passport";
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    private static readonly string[] EligibleReasonCodes = ["BIRTH_GRANT_ELIGIBLE"];
    private static readonly string[] IneligibleReasonCodes = ["INELIGIBLE_NOT_INSURED"];
    private static readonly string[] CallerRoles = ["cnas-system"];

    // ─────────────────────── Tests ───────────────────────

    [Fact]
    public async Task AdvanceAsync_AssignsExaminer_PublishesInExaminationCard()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        harness.Engine
            .Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Success(new DecisionOutcome(
                IsEligible: true,
                Amount: Money.Mdl(11000m),
                ReasonCodes: EligibleReasonCodes,
                ComputedValues: new Dictionary<string, object?>())));

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsSuccess.Should().BeTrue();

        // Sanity check that we landed in the eligible branch (dossier was opened).
        _ = seeded;
        var dossier = await harness.Db.Dossiers.SingleAsync();

        await harness.MCabinet.Received(1).PublishCardAsync(
            Arg.Is<MCabinetCard>(c =>
                !string.IsNullOrWhiteSpace(c.ExternalId)
                && c.CitizenIdnp == SolicitantNationalId
                && c.ServiceCode == PassportCode
                && c.Status == MCabinetStatus.InExamination
                && c.TitleRo == PassportNameRo
                && c.SubtitleRo == dossier.DossierNumber
                && c.EventUtc == ClockNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceAsync_PublisherFails_StillReturnsSuccess()
    {
        var harness = Harness.Create();
        await harness.SeedAsync();
        harness.Engine
            .Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Success(new DecisionOutcome(
                IsEligible: true,
                Amount: Money.Mdl(11000m),
                ReasonCodes: EligibleReasonCodes,
                ComputedValues: new Dictionary<string, object?>())));
        harness.MCabinet
            .PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.MCabinetPublishFailed, "Upstream MCabinet down."));

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        // Best-effort projection — main flow must succeed despite publisher failure.
        result.IsSuccess.Should().BeTrue();
        (await harness.Db.Dossiers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AdvanceAsync_NonAdvancingTransition_DoesNotPublishInExamination()
    {
        // Application not in Submitted state → AdvanceAsync short-circuits with a
        // Conflict-equivalent failure and must NOT publish a card (no transition occurred).
        var harness = Harness.Create();
        await harness.SeedAsync(status: ApplicationStatus.Approved);

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApplicationNotSubmitted);
        await harness.MCabinet.DidNotReceive().PublishCardAsync(
            Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceAsync_IneligibleOutcome_PublishesRejectedCardOnApplicationSqid()
    {
        // The auto-reject branch closes the application without opening a dossier — the
        // wiring must still emit a Rejected card so the citizen sees the terminal state.
        // The card is keyed by the application Sqid (the only stable external id at that point).
        var harness = Harness.Create();
        await harness.SeedAsync();
        harness.Engine
            .Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Success(new DecisionOutcome(
                IsEligible: false,
                Amount: null,
                ReasonCodes: IneligibleReasonCodes,
                ComputedValues: new Dictionary<string, object?>())));

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Ineligible);

        await harness.MCabinet.Received(1).PublishCardAsync(
            Arg.Is<MCabinetCard>(c =>
                !string.IsNullOrWhiteSpace(c.ExternalId)
                && c.CitizenIdnp == SolicitantNationalId
                && c.ServiceCode == PassportCode
                && c.Status == MCabinetStatus.Rejected
                && c.TitleRo == PassportNameRo
                && c.EventUtc == ClockNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceAsync_PublisherThrowsOnInExamination_DoesNotCrash()
    {
        var harness = Harness.Create();
        await harness.SeedAsync();
        harness.Engine
            .Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Success(new DecisionOutcome(
                IsEligible: true,
                Amount: Money.Mdl(11000m),
                ReasonCodes: EligibleReasonCodes,
                ComputedValues: new Dictionary<string, object?>())));
        harness.MCabinet
            .PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new InvalidOperationException("boom"));

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsSuccess.Should().BeTrue();
        (await harness.Db.Dossiers.CountAsync()).Should().Be(1);
        harness.Logger.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(ILogger.Log)
                      && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning)
            .Should().BeTrue();
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-mc-proc-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record SeedResult(long AppId, long SolicitantId, long PassportId);

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required ApplicationProcessingService Service { get; init; }
        public required IDecisionEngine Engine { get; init; }
        public required IMCabinetPublisher MCabinet { get; init; }
        public required ILogger<ApplicationProcessingService> Logger { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var engine = Substitute.For<IDecisionEngine>();
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            var notify = Substitute.For<INotificationService>();
            notify.EnqueueAsync(
                    Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-CALLER");
            caller.UserId.Returns(1L);
            caller.Roles.Returns(CallerRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var logger = Substitute.For<ILogger<ApplicationProcessingService>>();

            var service = new ApplicationProcessingService(
                db, sqids, clock, engine, audit, notify, caller, mcabinet, logger);
            return new Harness
            {
                Db = db,
                Service = service,
                Engine = engine,
                MCabinet = mcabinet,
                Logger = logger,
                Caller = caller,
                Sqids = sqids,
            };
        }

        public async Task<SeedResult> SeedAsync(ApplicationStatus status = ApplicationStatus.Submitted)
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = SolicitantNationalId,
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
                IsProactive = false,
                DecisionRulesJson = "{\"code\":\"TEST\"}",
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow,
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = status,
                FormPayloadJson = """{"isInsured":true,"birthOrder":1}""",
                SnapshotJson = "{}",
                SubmittedAtUtc = ClockNow.AddMinutes(-5),
                ReferenceNumber = "PS-TEST-0001",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Sqids.TryDecode("APP-SQID").Returns(Result<long>.Success(app.Id));

            return new SeedResult(app.Id, solicitant.Id, passport.Id);
        }
    }
}
