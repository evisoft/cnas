using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Recalculation;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Recalculation;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Recalculation;

/// <summary>
/// R1503 / TOR §3.7-D — shared helpers for the mass-recalculation test suite.
/// </summary>
internal static class RecalculationTestHelpers
{
    /// <summary>Canonical "now" used across the recalculation tests.</summary>
    public static readonly DateTime ClockNow = new(2026, 5, 23, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>Creates a fresh EF Core InMemory context (mirrors IntegrityTestHelpers).</summary>
    /// <returns>A new context backed by a uniquely named InMemory store.</returns>
    public static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-mass-recalc-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Test-only fixed UTC clock.</summary>
    public sealed class StubClock : ICnasTimeProvider
    {
        /// <summary>Constructs the clock with a fixed UTC instant.</summary>
        /// <param name="now">Instant returned from <see cref="UtcNow"/>.</param>
        public StubClock(DateTime now) { UtcNow = now; }

        /// <inheritdoc />
        public DateTime UtcNow { get; }
    }

    /// <summary>Stable Sqid mock — encodes <c>n</c> as <c>SQID-n</c>.</summary>
    /// <returns>Configured NSubstitute mock.</returns>
    public static ISqidService NewSqidMock()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        s.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return s;
    }

    /// <summary>Audit mock that captures every event code written.</summary>
    /// <param name="codes">Out parameter — the list captured codes will be appended to.</param>
    /// <returns>Configured mock.</returns>
    public static IAuditService NewAuditCapturing(out List<string> codes)
    {
        var list = new List<string>();
        codes = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => { list.Add(c.ArgAt<string>(0)); return Task.FromResult(Result.Success()); });
        return a;
    }

    /// <summary>Caller-context mock returning sqid "USR-1" / userId 1.</summary>
    /// <returns>Configured mock.</returns>
    public static ICallerContext NewCallerMock()
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(1L);
        c.UserSqid.Returns("USR-1");
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-recalc");
        return c;
    }

    /// <summary>Builds a fully-wired <see cref="LegalChangeEventService"/>.</summary>
    /// <param name="db">DB context.</param>
    /// <param name="audit">Audit service.</param>
    /// <returns>Ready-to-use service.</returns>
    public static LegalChangeEventService NewLegalChangeEventService(
        CnasDbContext db,
        IAuditService audit)
        => new(
            db: db,
            clock: new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: NewCallerMock(),
            audit: audit,
            registerValidator: new LegalChangeEventRegisterInputValidator(),
            modifyValidator: new LegalChangeEventModifyInputValidator(),
            reasonValidator: new LegalChangeEventReasonInputValidator(),
            filterValidator: new LegalChangeEventFilterValidator());

    /// <summary>Builds the orchestrator with an explicit strategy collection.</summary>
    /// <param name="db">DB context.</param>
    /// <param name="strategies">Strategies; pass empty array for the NO_STRATEGY_REGISTERED path.</param>
    /// <returns>Orchestrator instance.</returns>
    public static MassRecalculationOrchestrator NewOrchestrator(
        CnasDbContext db,
        IEnumerable<IBenefitRecalculationStrategy> strategies)
        => new(
            db: db,
            readDb: db,
            clock: new StubClock(ClockNow),
            caller: NewCallerMock(),
            strategies: strategies,
            logger: NullLogger<MassRecalculationOrchestrator>.Instance);

    /// <summary>Builds a fully-wired <see cref="MassRecalculationService"/>.</summary>
    /// <param name="db">DB context.</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="strategies">Strategies registered.</param>
    /// <param name="gateAllows">When true, the gate returns Allow; when false, Skip.</param>
    /// <returns>Ready-to-use service.</returns>
    public static MassRecalculationService NewMassRecalcService(
        CnasDbContext db,
        IAuditService audit,
        IEnumerable<IBenefitRecalculationStrategy> strategies,
        bool gateAllows = true)
    {
        var gate = gateAllows ? (Cnas.Ps.Application.Scheduling.IPeakHourGate)new AllowAllPeakHourGate()
            : new AlwaysSkipPeakHourGate();
        return new MassRecalculationService(
            db: db,
            clock: new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: NewCallerMock(),
            audit: audit,
            peakHourGate: gate,
            orchestrator: NewOrchestrator(db, strategies),
            rejectValidator: new RecalculationResultRejectInputValidator(),
            runFilterValidator: new RecalculationRunFilterValidator(),
            resultFilterValidator: new RecalculationResultFilterValidator());
    }

    /// <summary>
    /// Test-only <see cref="IBenefitRecalculationStrategy"/> that fakes a
    /// recompute by returning a 100 MDL bump for a fixed list of decision
    /// ids. Used to exercise the orchestrator end-to-end without depending
    /// on the still-pending real benefit-decision pipeline.
    /// </summary>
    public sealed class FakeBenefitRecalculationStrategy : IBenefitRecalculationStrategy
    {
        private readonly IReadOnlyList<long> _decisionIds;
        private readonly decimal _oldAmount;
        private readonly decimal _newAmount;
        private readonly string _benefitType;
        private readonly List<long> _appliedIds = new();

        /// <summary>Constructs the strategy.</summary>
        /// <param name="benefitType">Stable enum-name string this strategy handles.</param>
        /// <param name="decisionIds">Decision ids the strategy will enumerate.</param>
        /// <param name="oldAmount">Projected old MDL amount (default 3000).</param>
        /// <param name="newAmount">Projected new MDL amount (default 3200).</param>
        public FakeBenefitRecalculationStrategy(
            string benefitType,
            IReadOnlyList<long> decisionIds,
            decimal oldAmount = 3000m,
            decimal newAmount = 3200m)
        {
            _benefitType = benefitType;
            _decisionIds = decisionIds;
            _oldAmount = oldAmount;
            _newAmount = newAmount;
        }

        /// <summary>Decision ids that received <c>ApplyAsync</c>.</summary>
        public IReadOnlyList<long> AppliedDecisionIds => _appliedIds;

        /// <inheritdoc />
        public string BenefitType => _benefitType;

        /// <inheritdoc />
        public Task<IReadOnlyList<long>> EnumerateInScopeDecisionIdsAsync(
            LegalChangeEvent evt,
            IReadOnlyCnasDbContext db,
            CancellationToken cancellationToken)
            => Task.FromResult(_decisionIds);

        /// <inheritdoc />
        public Task<BenefitRecalculationOutcome> RecomputeAsync(
            long decisionId,
            LegalChangeEvent evt,
            IReadOnlyCnasDbContext db,
            CancellationToken cancellationToken)
            => Task.FromResult(new BenefitRecalculationOutcome
            {
                Status = RecalculationResultStatus.Computed,
                OldAmountMdl = _oldAmount,
                NewAmountMdl = _newAmount,
                BeneficiaryIdnpHash = "HASH-" + decisionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Reason = null,
                RecalculationContextJson = "{\"sample\":true}",
            });

        /// <inheritdoc />
        public Task<Result> ApplyAsync(
            RecalculationDecisionResult result,
            ICnasDbContext db,
            CancellationToken cancellationToken)
        {
            _appliedIds.Add(result.BenefitDecisionId);
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>Seeds a Ready legal-change event scoped to a single benefit kind.</summary>
    /// <param name="db">Context.</param>
    /// <param name="benefitType">Stable enum-name string in scope.</param>
    /// <param name="status">Initial status (default Ready).</param>
    /// <returns>Persisted event row.</returns>
    public static async Task<LegalChangeEvent> SeedReadyEventAsync(
        CnasDbContext db,
        string benefitType = "OldAgePension",
        LegalChangeEventStatus status = LegalChangeEventStatus.Ready)
    {
        var evt = new LegalChangeEvent
        {
            Code = "LCE-TEST-001",
            Title = "Test pension floor raise",
            Description = "Test",
            EffectiveFrom = new DateOnly(2026, 1, 1),
            Scope = LegalChangeScope.Pension,
            BenefitTypesInScope = new List<string> { benefitType },
            ChangePayloadJson = "{\"minimumPensionMdl\":3200}",
            Status = status,
            RegisteredByUserId = 1,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        };
        db.LegalChangeEvents.Add(evt);
        await db.SaveChangesAsync();
        return evt;
    }
}
