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
/// R0933 / TOR §10.1 — integration tests for <see cref="PriorDecisionTerminator"/>.
/// Drives the SUT against an EF Core InMemory store; substitutes the Sqid encoder
/// with a trivial fake so encoded ids stay assertion-friendly. Per CLAUDE.md
/// RULE 1 these tests are RED→GREEN drivers: they were written before the
/// production body and pin the observable lifecycle behaviour.
/// </summary>
public sealed class PriorDecisionTerminatorTests
{
    /// <summary>Deterministic UTC clock so audit timestamps stay assertable.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 13, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── CompareAsync ───────────────────────

    [Fact]
    public async Task CompareAsync_NewDecisionMissing_ReturnsNotFound()
    {
        // Defensive guard — an unknown decision id must not silently return an empty comparison.
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.CompareAsync(99_999L);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task CompareAsync_NoPriorDecision_ReturnsHasPriorFalse()
    {
        // First-time applicant — the comparison surface must signal HasPrior=false so the UI
        // skips the warning + supersession step.
        var harness = await Harness.CreateAsync();
        var seed = await harness.SeedNewDecisionOnlyAsync(newAmount: 1500m);

        var result = await harness.Service.CompareAsync(seed.NewDecisionId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HasPrior.Should().BeFalse();
        result.Value.PreviousDecisionSqid.Should().BeNull();
        result.Value.PriorAmount.Should().BeNull();
        result.Value.NewAmount.Should().Be(1500m);
        result.Value.Difference.Should().BeNull();
        result.Value.LowerSumWarning.Should().BeFalse();
    }

    [Fact]
    public async Task CompareAsync_NewAmountGreaterThanPrior_NoWarning_PositiveDifference()
    {
        var harness = await Harness.CreateAsync();
        var seed = await harness.SeedPriorAndNewAsync(priorAmount: 1000m, newAmount: 1500m);

        var result = await harness.Service.CompareAsync(seed.NewDecisionId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HasPrior.Should().BeTrue();
        result.Value.PriorAmount.Should().Be(1000m);
        result.Value.NewAmount.Should().Be(1500m);
        result.Value.Difference.Should().Be(500m);
        result.Value.LowerSumWarning.Should().BeFalse();
    }

    [Fact]
    public async Task CompareAsync_NewAmountLowerThanPrior_RaisesLowerSumWarning()
    {
        var harness = await Harness.CreateAsync();
        var seed = await harness.SeedPriorAndNewAsync(priorAmount: 2000m, newAmount: 1200m);

        var result = await harness.Service.CompareAsync(seed.NewDecisionId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HasPrior.Should().BeTrue();
        result.Value.Difference.Should().Be(-800m);
        result.Value.LowerSumWarning.Should().BeTrue();
    }

    [Fact]
    public async Task CompareAsync_DifferentServiceCode_TreatsAsNoPrior()
    {
        // R0933 explicitly scopes the prior-decision lookup to the SAME service code.
        // An approved decision for an unrelated service must NOT be reported as prior.
        var harness = await Harness.CreateAsync();
        var seed = await harness.SeedTwoDifferentServiceDecisionsAsync(
            priorAmount: 1000m, newAmount: 1500m);

        var result = await harness.Service.CompareAsync(seed.NewDecisionId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HasPrior.Should().BeFalse();
    }

    // ─────────────────────── TerminateOnAcceptanceAsync ───────────────────────

    [Fact]
    public async Task TerminateOnAcceptanceAsync_NewDecisionMissing_ReturnsNotFound()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.TerminateOnAcceptanceAsync(99_999L);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task TerminateOnAcceptanceAsync_NoPriorDecision_ReturnsNullSuperession()
    {
        var harness = await Harness.CreateAsync();
        var seed = await harness.SeedNewDecisionOnlyAsync(newAmount: 1500m);

        var result = await harness.Service.TerminateOnAcceptanceAsync(seed.NewDecisionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
        var rows = await harness.Db.DecisionSupersessions.CountAsync();
        rows.Should().Be(0);
        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task TerminateOnAcceptanceAsync_PriorActive_SupersedesAndAudits()
    {
        var harness = await Harness.CreateAsync();
        var seed = await harness.SeedPriorAndNewAsync(priorAmount: 1000m, newAmount: 1500m);

        var result = await harness.Service.TerminateOnAcceptanceAsync(seed.NewDecisionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.PriorAmount.Should().Be(1000m);
        result.Value.NewAmount.Should().Be(1500m);

        // Prior decision must now be Closed with timestamp stamped.
        var prior = await harness.Db.Applications.SingleAsync(a => a.Id == seed.PriorDecisionId);
        prior.Status.Should().Be(ApplicationStatus.Closed);
        prior.ClosedAtUtc.Should().Be(ClockNow);
        prior.UpdatedAtUtc.Should().Be(ClockNow);

        // A supersession row must exist.
        var rows = await harness.Db.DecisionSupersessions.ToListAsync();
        rows.Should().ContainSingle(s =>
            s.PreviousDecisionId == seed.PriorDecisionId
            && s.NewDecisionId == seed.NewDecisionId
            && s.SupersededAtUtc == ClockNow);

        await harness.Audit.Received(1).RecordAsync(
            "DECISION.SUPERSEDED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(ServiceApplication),
            seed.PriorDecisionId,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TerminateOnAcceptanceAsync_PriorAlreadyTerminated_IsIdempotent()
    {
        // Calling twice for the same (prior, new) pair must NOT double-audit, double-flip,
        // or insert a second supersession row. The natural-key uniqueness guards persistence;
        // the service body short-circuits on the existing-row read.
        var harness = await Harness.CreateAsync();
        var seed = await harness.SeedPriorAndNewAsync(priorAmount: 1000m, newAmount: 1500m);

        await harness.Service.TerminateOnAcceptanceAsync(seed.NewDecisionId);
        harness.Audit.ClearReceivedCalls();
        var second = await harness.Service.TerminateOnAcceptanceAsync(seed.NewDecisionId);

        second.IsSuccess.Should().BeTrue();
        second.Value.Should().NotBeNull();
        var rows = await harness.Db.DecisionSupersessions.CountAsync();
        rows.Should().Be(1);
        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task TerminateOnAcceptanceAsync_NewSumLowerThanPrior_StillSupersedesButRecordsWarning()
    {
        // The terminator does NOT gate on the lower-sum warning (the gate lives on the
        // decider UI / controller). When the decider proceeds anyway the supersession row
        // captures the warning verbatim in its Reason column.
        var harness = await Harness.CreateAsync();
        var seed = await harness.SeedPriorAndNewAsync(priorAmount: 2000m, newAmount: 1200m);

        var result = await harness.Service.TerminateOnAcceptanceAsync(seed.NewDecisionId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Reason.Should().Contain("lower-sum-warning");
        var prior = await harness.Db.Applications.SingleAsync(a => a.Id == seed.PriorDecisionId);
        prior.Status.Should().Be(ApplicationStatus.Closed);
    }

    [Fact]
    public async Task TerminateOnAcceptanceAsync_DifferentServiceCode_NoSupersession()
    {
        // Cross-service replacement is NOT modelled by R0933 — two distinct services may
        // be paid concurrently. The terminator must therefore find no prior to terminate.
        var harness = await Harness.CreateAsync();
        var seed = await harness.SeedTwoDifferentServiceDecisionsAsync(
            priorAmount: 1000m, newAmount: 1500m);

        var result = await harness.Service.TerminateOnAcceptanceAsync(seed.NewDecisionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
        var prior = await harness.Db.Applications.SingleAsync(a => a.Id == seed.PriorDecisionId);
        prior.Status.Should().Be(ApplicationStatus.Approved); // Untouched.
    }

    [Fact]
    public async Task TerminateOnAcceptanceAsync_DispatchesActionResultNotification_WhenWired()
    {
        var harness = await Harness.CreateAsync(withTriggers: true);
        var seed = await harness.SeedPriorAndNewAsync(priorAmount: 1000m, newAmount: 1500m);

        var result = await harness.Service.TerminateOnAcceptanceAsync(seed.NewDecisionId);

        result.IsSuccess.Should().BeTrue();
        await harness.Triggers!.Received(1).DispatchAsync(
            NotificationTriggerKind.ActionResult,
            Arg.Is<NotificationTriggerPayload>(p => p.RelatedEntityId == seed.PriorDecisionId),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-prior-terminator-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record SeedResult(long SolicitantId, long PassportId, long PriorDecisionId, long NewDecisionId);

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required PriorDecisionTerminator Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }
        public INotificationTriggerDispatcher? Triggers { get; init; }

        public static Task<Harness> CreateAsync(bool withTriggers = false)
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
            caller.UserSqid.Returns("SQID-DECIDER");
            caller.UserId.Returns(42L);
            caller.Roles.Returns(["cnas-decider"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-prior-1");

            INotificationTriggerDispatcher? triggers = null;
            if (withTriggers)
            {
                triggers = Substitute.For<INotificationTriggerDispatcher>();
                triggers.DispatchAsync(
                        Arg.Any<NotificationTriggerKind>(),
                        Arg.Any<NotificationTriggerPayload>(),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(Result.Success()));
            }

            var service = new PriorDecisionTerminator(
                db, sqids, clock, caller, audit,
                NullLogger<PriorDecisionTerminator>.Instance,
                triggers);

            return Task.FromResult(new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
                Caller = caller,
                Sqids = sqids,
                Triggers = triggers,
            });
        }

        public async Task<SeedResult> SeedNewDecisionOnlyAsync(decimal newAmount)
        {
            var solicitant = AddSolicitant();
            var passport = AddPassport("SP-OLDAGE", "Old-age pension");
            await Db.SaveChangesAsync();
            var newDec = AddDecision(solicitant.Id, passport.Id, ApplicationStatus.UnderExamination, newAmount, "PS-NEW-1");
            await Db.SaveChangesAsync();
            return new SeedResult(solicitant.Id, passport.Id, 0L, newDec.Id);
        }

        public async Task<SeedResult> SeedPriorAndNewAsync(decimal priorAmount, decimal newAmount)
        {
            var solicitant = AddSolicitant();
            var passport = AddPassport("SP-OLDAGE", "Old-age pension");
            await Db.SaveChangesAsync();
            var prior = AddDecision(solicitant.Id, passport.Id, ApplicationStatus.Approved, priorAmount, "PS-PRIOR-1");
            prior.SubmittedAtUtc = ClockNow.AddYears(-2);
            var newer = AddDecision(solicitant.Id, passport.Id, ApplicationStatus.UnderExamination, newAmount, "PS-NEW-1");
            newer.SubmittedAtUtc = ClockNow.AddDays(-1);
            await Db.SaveChangesAsync();
            return new SeedResult(solicitant.Id, passport.Id, prior.Id, newer.Id);
        }

        public async Task<SeedResult> SeedTwoDifferentServiceDecisionsAsync(decimal priorAmount, decimal newAmount)
        {
            var solicitant = AddSolicitant();
            var passportOld = AddPassport("SP-OLDAGE", "Old-age pension");
            var passportDisability = AddPassport("SP-DISAB", "Disability pension");
            await Db.SaveChangesAsync();
            var prior = AddDecision(solicitant.Id, passportOld.Id, ApplicationStatus.Approved, priorAmount, "PS-PRIOR-OLD");
            prior.SubmittedAtUtc = ClockNow.AddYears(-2);
            var newer = AddDecision(solicitant.Id, passportDisability.Id, ApplicationStatus.UnderExamination, newAmount, "PS-NEW-DIS");
            newer.SubmittedAtUtc = ClockNow.AddDays(-1);
            await Db.SaveChangesAsync();
            return new SeedResult(solicitant.Id, passportOld.Id, prior.Id, newer.Id);
        }

        private Solicitant AddSolicitant()
        {
            var s = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = $"2000000000{Random.Shared.Next(100, 999)}",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Test Solicitant",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(s);
            return s;
        }

        private ServicePassport AddPassport(string code, string name)
        {
            var p = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = code,
                NameRo = name,
                DescriptionRo = name,
                FormSchemaJson = "{}",
                WorkflowCode = "WF-TEST",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(p);
            return p;
        }

        private ServiceApplication AddDecision(
            long solicitantId, long passportId, ApplicationStatus status, decimal amount, string refNum)
        {
            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow,
                SolicitantId = solicitantId,
                ServicePassportId = passportId,
                Status = status,
                FormPayloadJson = $"{{\"monthlyAmountMdl\":{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}",
                SnapshotJson = "{}",
                SubmittedAtUtc = ClockNow.AddDays(-1),
                ReferenceNumber = refNum,
                IsActive = true,
            };
            Db.Applications.Add(app);
            return app;
        }
    }
}
