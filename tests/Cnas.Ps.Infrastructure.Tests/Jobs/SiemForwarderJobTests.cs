using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// Tests for <see cref="SiemForwarderJob"/> — R0190 / SEC 049 Quartz poll job that scans
/// new <see cref="AuditLog"/> rows past the singleton-row checkpoint, hands them to
/// <see cref="ISiemExporter"/>, and either advances the checkpoint (on success) or
/// pins it (on failure).
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> — the job increments
/// <c>cnas.audit.siem_forwarded</c> on the process-static meter, so cross-test
/// parallelism must be suppressed to keep increment-count assertions stable.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class SiemForwarderJobTests
{
    private static readonly DateTime FixedNow = new(2026, 5, 21, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Execute_Disabled_NoOp()
    {
        // When Enabled=false the job must return immediately — no DB scan, no exporter
        // call. We verify by passing the no-op exporter from the harness and asserting
        // it was never invoked.
        var harness = await Harness.CreateAsync(new SiemExporterOptions { Enabled = false });
        // Seed rows so the predicate query, if run, would surface them.
        await harness.SeedAuditRowsAsync(count: 2);

        await harness.Job.Execute(FakeContext());

        await harness.Exporter.DidNotReceiveWithAnyArgs().ForwardAsync(default!, default);
    }

    [Fact]
    public async Task Execute_StateMissing_LogsAndReturns()
    {
        // The migration seeds the singleton row; if it's missing (operator soft-deleted
        // it, or the migration was reverted) the job must not crash — it logs a warning
        // and returns without forwarding.
        var harness = await Harness.CreateAsync(new SiemExporterOptions { Enabled = true });
        // Remove the seeded row so the FirstOrDefaultAsync returns null.
        var state = await harness.Db.SiemForwarderStates.SingleAsync();
        harness.Db.SiemForwarderStates.Remove(state);
        await harness.Db.SaveChangesAsync();

        await harness.SeedAuditRowsAsync(count: 1);

        await harness.Job.Execute(FakeContext());

        await harness.Exporter
            .DidNotReceiveWithAnyArgs()
            .ForwardAsync(default!, default);
    }

    [Fact]
    public async Task Execute_NoNewRows_NoForward()
    {
        // Empty audit table → no forwarding. The exporter must not be called with an
        // empty list; the job filters that case out before it gets that far.
        var harness = await Harness.CreateAsync(new SiemExporterOptions { Enabled = true });

        await harness.Job.Execute(FakeContext());

        await harness.Exporter.DidNotReceiveWithAnyArgs().ForwardAsync(default!, default);
        var state = await harness.Db.SiemForwarderStates.SingleAsync();
        state.LastForwardedAuditId.Should().Be(0);
    }

    [Fact]
    public async Task Execute_NewRows_ForwardedAndCheckpointAdvances()
    {
        using var capture = new MetricCapture("cnas.audit.siem_forwarded");
        var harness = await Harness.CreateAsync(new SiemExporterOptions { Enabled = true });
        harness.Exporter
            .ForwardAsync(Arg.Any<IReadOnlyList<AuditLog>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var seededIds = await harness.SeedAuditRowsAsync(count: 3);

        await harness.Job.Execute(FakeContext());

        await harness.Exporter.Received(1).ForwardAsync(
            Arg.Is<IReadOnlyList<AuditLog>>(rows => rows.Count == 3),
            Arg.Any<CancellationToken>());

        // Checkpoint advanced to the highest id seen this fire.
        var state = await harness.Db.SiemForwarderStates.SingleAsync();
        state.LastForwardedAuditId.Should().Be(seededIds.Max());
        state.LastForwardedAtUtc.Should().NotBeNull();

        // Counter incremented by the row count.
        capture.TotalIncrement.Should().Be(3);
    }

    [Fact]
    public async Task Execute_ExporterFailure_CheckpointDoesNotAdvance()
    {
        // Transport failure → checkpoint pinned so the next iteration retries the same
        // range. Counter must NOT increment because no rows were successfully forwarded.
        using var capture = new MetricCapture("cnas.audit.siem_forwarded");
        var harness = await Harness.CreateAsync(new SiemExporterOptions { Enabled = true });
        harness.Exporter
            .ForwardAsync(Arg.Any<IReadOnlyList<AuditLog>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure(ErrorCodes.Internal, "transport down")));

        await harness.SeedAuditRowsAsync(count: 3);

        await harness.Job.Execute(FakeContext());

        var state = await harness.Db.SiemForwarderStates.SingleAsync();
        state.LastForwardedAuditId.Should().Be(0);
        state.LastForwardedAtUtc.Should().BeNull();
        capture.TotalIncrement.Should().Be(0);
    }

    [Fact]
    public async Task Execute_BatchSize_LimitsScan()
    {
        // BatchSize=2 must cap a 5-row backlog to 2 rows forwarded in a single fire.
        // The checkpoint advances to the second row's id, NOT the last row's id, so the
        // next fire picks up where this one stopped.
        var harness = await Harness.CreateAsync(new SiemExporterOptions
        {
            Enabled = true,
            BatchSize = 2,
        });
        harness.Exporter
            .ForwardAsync(Arg.Any<IReadOnlyList<AuditLog>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var seededIds = await harness.SeedAuditRowsAsync(count: 5);

        await harness.Job.Execute(FakeContext());

        await harness.Exporter.Received(1).ForwardAsync(
            Arg.Is<IReadOnlyList<AuditLog>>(rows => rows.Count == 2),
            Arg.Any<CancellationToken>());

        var state = await harness.Db.SiemForwarderStates.SingleAsync();
        // Checkpoint advanced to the SECOND id (the cap), not the fifth.
        state.LastForwardedAuditId.Should().Be(seededIds.OrderBy(i => i).ElementAt(1));
    }

    [Fact]
    public async Task Execute_Counter_cnas_audit_siem_forwarded_IncrementsByForwardedCount()
    {
        // A focused counter test — the previous tests confirm the increment in passing,
        // this one nails the contract explicitly.
        using var capture = new MetricCapture("cnas.audit.siem_forwarded");
        var harness = await Harness.CreateAsync(new SiemExporterOptions { Enabled = true });
        harness.Exporter
            .ForwardAsync(Arg.Any<IReadOnlyList<AuditLog>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        await harness.SeedAuditRowsAsync(count: 4);

        await harness.Job.Execute(FakeContext());

        capture.TotalIncrement.Should().Be(4);
    }

    [Fact]
    public async Task Execute_AlreadyForwardedRows_NotReEmitted()
    {
        // Idempotency anchor — a second fire after a successful first must NOT
        // re-forward rows already past the checkpoint.
        var harness = await Harness.CreateAsync(new SiemExporterOptions { Enabled = true });
        harness.Exporter
            .ForwardAsync(Arg.Any<IReadOnlyList<AuditLog>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        await harness.SeedAuditRowsAsync(count: 2);
        await harness.Job.Execute(FakeContext());
        harness.Exporter.ClearReceivedCalls();

        // Second fire — no new rows, so no exporter call.
        await harness.Job.Execute(FakeContext());

        await harness.Exporter.DidNotReceiveWithAnyArgs().ForwardAsync(default!, default);
    }

    // ─────────────────────── helpers ───────────────────────

    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    /// <summary>
    /// MeterListener-based capture for a single instrument on
    /// <see cref="CnasMeter.MeterName"/>. Disposed at the end of the test so the next
    /// case starts clean.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly List<long> _measurements = new();
        private readonly object _gate = new();

        public long TotalIncrement
        {
            get { lock (_gate) return _measurements.Sum(); }
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
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (_gate) { _measurements.Add(value); }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Deterministic clock used by the job under test.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required SiemForwarderJob Job { get; init; }
        public required ISiemExporter Exporter { get; init; }

        public static async Task<Harness> CreateAsync(SiemExporterOptions options)
        {
            var dbOpts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-siem-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(dbOpts);

            // Seed the singleton state row (mirroring the migration's seed INSERT).
            db.SiemForwarderStates.Add(new SiemForwarderState
            {
                CreatedAtUtc = FixedNow,
                Key = SiemForwarderJob.SingletonKey,
                LastForwardedAuditId = 0,
                IsActive = true,
            });
            await db.SaveChangesAsync();

            var exporter = Substitute.For<ISiemExporter>();
            exporter
                .ForwardAsync(Arg.Any<IReadOnlyList<AuditLog>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            var scope = Substitute.For<IServiceScope>();
            var sp = Substitute.For<IServiceProvider>();
            sp.GetService(typeof(ICnasDbContext)).Returns(db);
            sp.GetService(typeof(ISiemExporter)).Returns(exporter);
            sp.GetService(typeof(ICnasTimeProvider)).Returns(new StubClock(FixedNow));
            scope.ServiceProvider.Returns(sp);
            scopeFactory.CreateScope().Returns(scope);

            var job = new SiemForwarderJob(
                scopeFactory,
                new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
                Options.Create(options),
                NullLogger<SiemForwarderJob>.Instance);

            return new Harness { Db = db, Job = job, Exporter = exporter };
        }

        /// <summary>
        /// Seeds the audit table with <paramref name="count"/> rows. Returns the
        /// persisted ids in insertion order so individual tests can assert on
        /// checkpoint advancement.
        /// </summary>
        public async Task<List<long>> SeedAuditRowsAsync(int count)
        {
            var ids = new List<long>(count);
            for (var i = 0; i < count; i++)
            {
                var row = new AuditLog
                {
                    CreatedAtUtc = FixedNow.AddMinutes(i),
                    EventAtUtc = FixedNow.AddMinutes(i),
                    EventCode = $"TEST.EVENT.{i}",
                    Severity = AuditSeverity.Notice,
                    ActorId = "test-actor",
                    DetailsJson = "{}",
                    PrevHash = "GENESIS",
                    RowHash = new string('0', 64),
                };
                Db.AuditLogs.Add(row);
                await Db.SaveChangesAsync();
                ids.Add(row.Id);
            }
            return ids;
        }
    }
}
