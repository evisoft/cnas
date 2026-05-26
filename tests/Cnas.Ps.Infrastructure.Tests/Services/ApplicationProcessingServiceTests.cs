using System.Text.RegularExpressions;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests for <see cref="ApplicationProcessingService"/>. Uses EF Core
/// InMemory for the persistence backend and NSubstitute for the surrounding
/// collaborators (engine, audit, notification, caller, sqid encoder, clock).
/// </summary>
public class ApplicationProcessingServiceTests
{
    private const string SolicitantNationalId = "2000000000007";
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    // Reusable readonly arrays to satisfy CA1861 (constant array literals would allocate per call).
    private static readonly string[] EligibleReasonCodes = ["BIRTH_GRANT_ELIGIBLE"];
    private static readonly string[] IneligibleReasonCodes = ["INELIGIBLE_NOT_INSURED", "INELIGIBLE_LATE_CLAIM"];
    private static readonly string[] CallerRoles = ["cnas-system"];

    // ─────────────────────── Tests ───────────────────────

    [Fact]
    public async Task AdvanceAsync_InvalidSqid_ReturnsInvalidSqidFailure()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("bad").Returns(Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));

        var result = await harness.Service.AdvanceAsync("bad");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    [Fact]
    public async Task AdvanceAsync_ApplicationMissing_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("missing").Returns(Result<long>.Success(9999L));

        var result = await harness.Service.AdvanceAsync("missing");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task AdvanceAsync_ApplicationNotInSubmittedState_ReturnsConflict()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(status: ApplicationStatus.Approved);

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApplicationNotSubmitted);

        // Status must be unchanged.
        var reloaded = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        reloaded.Status.Should().Be(ApplicationStatus.Approved);
    }

    [Fact]
    public async Task AdvanceAsync_EngineFailure_AutoRejectsApplicationAndAudits()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        harness.Engine
            .Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Failure(ErrorCodes.BadRule, "malformed rule"));

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BadRule);

        var reloaded = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        reloaded.Status.Should().Be(ApplicationStatus.Rejected);
        reloaded.ClosedAtUtc.Should().Be(ClockNow);

        await harness.Audit.Received(1).RecordAsync(
            "APPLICATION.AUTO_REJECTED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(ServiceApplication),
            seeded.AppId,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await harness.Notify.Received(1).EnqueueAsync(
            seeded.SolicitantId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceAsync_IneligibleOutcome_RejectsWithReasonCodes()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        var outcome = new DecisionOutcome(
            IsEligible: false,
            Amount: null,
            ReasonCodes: IneligibleReasonCodes,
            ComputedValues: new Dictionary<string, object?>());
        harness.Engine
            .Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Success(outcome));

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Ineligible);
        result.ErrorMessage.Should().Contain("INELIGIBLE_NOT_INSURED");
        result.ErrorMessage.Should().Contain("INELIGIBLE_LATE_CLAIM");

        var reloaded = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        reloaded.Status.Should().Be(ApplicationStatus.Rejected);

        // The audit details JSON must mention the codes for explainability.
        await harness.Audit.Received(1).RecordAsync(
            "APPLICATION.AUTO_REJECTED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(ServiceApplication),
            seeded.AppId,
            Arg.Is<string>(s => s.Contains("INELIGIBLE_NOT_INSURED")
                                && s.Contains("INELIGIBLE_LATE_CLAIM")),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceAsync_EligibleOutcome_OpensDossierAndCreatesTask()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        var outcome = new DecisionOutcome(
            IsEligible: true,
            Amount: Money.Mdl(11000m),
            ReasonCodes: EligibleReasonCodes,
            ComputedValues: new Dictionary<string, object?>());
        harness.Engine
            .Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Success(outcome));

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsSuccess.Should().BeTrue();

        var reloaded = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        reloaded.Status.Should().Be(ApplicationStatus.UnderExamination);
        reloaded.DossierId.Should().NotBeNull();

        var dossiers = await harness.Db.Dossiers.ToListAsync();
        dossiers.Should().ContainSingle();
        var dossier = dossiers[0];
        Regex.IsMatch(dossier.DossierNumber, "^D-2026-[A-F0-9]{8}$")
             .Should().BeTrue($"actual: {dossier.DossierNumber}");
        dossier.ApplicationId.Should().Be(seeded.AppId);

        var tasks = await harness.Db.WorkflowTasks.ToListAsync();
        tasks.Should().ContainSingle();
        tasks[0].GroupCode.Should().Be("cnas-examiner");
        tasks[0].Title.Should().Be("Examinare cerere");
        tasks[0].Status.Should().Be(WorkflowTaskStatus.Pending);
        tasks[0].DossierId.Should().Be(dossier.Id);

        await harness.Audit.Received(1).RecordAsync(
            "APPLICATION.ACCEPTED_FOR_EXAMINATION",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(ServiceApplication),
            seeded.AppId,
            Arg.Is<string>(s => s.Contains(dossier.DossierNumber)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceAsync_EligibleOutcome_SetsDueDateFromPassportMaxProcessingDays()
    {
        const int maxProcessingDays = 10;
        var harness = Harness.Create();
        await harness.SeedAsync(maxProcessingDays: maxProcessingDays);
        var outcome = new DecisionOutcome(
            IsEligible: true,
            Amount: Money.Mdl(11000m),
            ReasonCodes: EligibleReasonCodes,
            ComputedValues: new Dictionary<string, object?>());
        harness.Engine
            .Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Success(outcome));

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsSuccess.Should().BeTrue();
        var task = await harness.Db.WorkflowTasks.SingleAsync();
        task.DueAtUtc.Should().Be(ClockNow.AddDays(maxProcessingDays));
    }

    /// <summary>
    /// Closes the wire-up TODO between the decision engine and <c>MPayDispatcherJob</c>:
    /// when the engine returns an eligible outcome with a computed <see cref="Money"/>,
    /// the persisted <see cref="Dossier.ComputedAmountMdl"/> must hold the decimal amount
    /// so the dispatcher can later enqueue the outbound MPay transfer.
    /// </summary>
    [Fact]
    public async Task AdvanceAsync_EligibleOutcomeWithAmount_StoresComputedAmountOnDossier()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        var outcome = new DecisionOutcome(
            IsEligible: true,
            Amount: Money.Mdl(2500.00m),
            ReasonCodes: ["X_ELIGIBLE"],
            ComputedValues: new Dictionary<string, object?>());
        harness.Engine
            .Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Success(outcome));

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsSuccess.Should().BeTrue();

        var dossier = await harness.Db.Dossiers
            .SingleAsync(d => d.ApplicationId == seeded.AppId);
        dossier.ComputedAmountMdl.Should().Be(2500.00m);
    }

    /// <summary>
    /// Eligible outcomes that carry no monetary amount (asset-grant / voucher services)
    /// must leave <see cref="Dossier.ComputedAmountMdl"/> null so the dispatcher skips
    /// the row instead of sending a zero-amount transfer.
    /// </summary>
    [Fact]
    public async Task AdvanceAsync_EligibleOutcomeWithoutAmount_LeavesComputedAmountNull()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        var outcome = new DecisionOutcome(
            IsEligible: true,
            Amount: null,
            ReasonCodes: EligibleReasonCodes,
            ComputedValues: new Dictionary<string, object?>());
        harness.Engine
            .Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Success(outcome));

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsSuccess.Should().BeTrue();

        var dossier = await harness.Db.Dossiers
            .SingleAsync(d => d.ApplicationId == seeded.AppId);
        dossier.ComputedAmountMdl.Should().BeNull();
    }

    [Fact]
    public async Task AdvanceAsync_PassportMissing_ReturnsNotFound()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        // Hard-delete the passport so the loader returns null.
        var passport = await harness.Db.ServicePassports.SingleAsync(p => p.Id == seeded.PassportId);
        harness.Db.ServicePassports.Remove(passport);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.AdvanceAsync("APP-SQID");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-{Guid.NewGuid():N}")
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
        public required IAuditService Audit { get; init; }
        public required INotificationService Notify { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }
        public required ICnasTimeProvider Clock { get; init; }

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

            var service = new ApplicationProcessingService(
                db, sqids, clock, engine, audit, notify, caller,
                mcabinet, NullLogger<ApplicationProcessingService>.Instance);
            return new Harness
            {
                Db = db,
                Service = service,
                Engine = engine,
                Audit = audit,
                Notify = notify,
                Caller = caller,
                Sqids = sqids,
                Clock = clock,
            };
        }

        public async Task<SeedResult> SeedAsync(
            ApplicationStatus status = ApplicationStatus.Submitted,
            int maxProcessingDays = 30,
            string? decisionRulesJson = null,
            string? formPayloadJson = null)
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
                Code = "SP-TEST",
                NameRo = "Test passport",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-TEST",
                MaxProcessingDays = maxProcessingDays,
                IsEnabled = true,
                IsProactive = false,
                DecisionRulesJson = decisionRulesJson ?? "{\"code\":\"TEST\"}",
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
                FormPayloadJson = formPayloadJson ?? """{"isInsured":true,"birthOrder":1}""",
                SnapshotJson = "{}",
                SubmittedAtUtc = ClockNow.AddMinutes(-5),
                ReferenceNumber = "PS-TEST-0001",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            // Bind the canonical "APP-SQID" → app.Id mapping the tests rely on.
            Sqids.TryDecode("APP-SQID").Returns(Result<long>.Success(app.Id));

            return new SeedResult(app.Id, solicitant.Id, passport.Id);
        }
    }
}
