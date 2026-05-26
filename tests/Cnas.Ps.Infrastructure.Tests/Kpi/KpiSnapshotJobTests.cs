using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Kpi;

/// <summary>
/// R0201 / TOR CF 20.02 — tests for <see cref="KpiSnapshotJob"/>.
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> — the job emits on the static
/// <c>cnas.kpi.snapshot_run</c> counter so cross-test parallelism is suppressed.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public sealed class KpiSnapshotJobTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 22, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Execute_DelegatesToServiceForYesterday()
    {
        var harness = Harness.Create();
        harness.Service.RunForDateAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Result<KpiSnapshotRunDto>.Success(
                new KpiSnapshotRunDto("abc123", new(2026, 5, 21), 5, 10, 42)));

        await harness.Job.Execute(FakeContext());

        await harness.Service.Received(1).RunForDateAsync(
            new DateOnly(2026, 5, 21),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_OnSuccess_IncrementsCounterWithStatusSuccessTag()
    {
        using var capture = new MetricCapture("cnas.kpi.snapshot_run");
        var harness = Harness.Create();
        harness.Service.RunForDateAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Result<KpiSnapshotRunDto>.Success(
                new KpiSnapshotRunDto("abc123", new(2026, 5, 21), 5, 10, 42)));

        await harness.Job.Execute(FakeContext());

        capture.TotalIncrement.Should().Be(1);
        capture.Tags.Should().Contain(t =>
            t.Any(kv => kv.Key == "status" && kv.Value as string == "success"));
    }

    /// <summary>
    /// R2173 / TOR PSR 004 — when the gate refuses the fire the job MUST
    /// return without invoking the service. Verifies the early-return
    /// contract for an OffPeakOnly job during peak hours.
    /// </summary>
    [Fact]
    public async Task Execute_PeakHourGateSkip_DoesNotInvokeService()
    {
        var harness = Harness.CreateWithSkippingGate();

        await harness.Job.Execute(FakeContext());

        await harness.Service.DidNotReceiveWithAnyArgs().RunForDateAsync(
            default, default);
    }

    /// <summary>
    /// R2173 — when the gate allows the fire the job MUST invoke the service
    /// path (matches the existing happy-path expectation).
    /// </summary>
    [Fact]
    public async Task Execute_PeakHourGateAllow_InvokesService()
    {
        var harness = Harness.Create();
        harness.Service.RunForDateAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Result<KpiSnapshotRunDto>.Success(
                new KpiSnapshotRunDto("abc123", new(2026, 5, 21), 5, 10, 42)));

        await harness.Job.Execute(FakeContext());

        await harness.Service.Received(1).RunForDateAsync(
            Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── helpers ───────────────────────

    /// <summary>Returns a no-op Quartz job-execution context.</summary>
    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    /// <summary>Deterministic clock helper.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>MeterListener-based capture for a single instrument name.</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly List<long> _measurements = new();
        private readonly List<IReadOnlyList<KeyValuePair<string, object?>>> _tags = new();
        private readonly object _gate = new();

        public long TotalIncrement
        {
            get { lock (_gate) return _measurements.Sum(); }
        }

        public IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> Tags
        {
            get { lock (_gate) return _tags.ToList(); }
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
                lock (_gate)
                {
                    _measurements.Add(value);
                    _tags.Add(tags.ToArray());
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class Harness
    {
        public required KpiSnapshotJob Job { get; init; }
        public required IKpiSnapshotService Service { get; init; }

        public static Harness Create()
        {
            var service = Substitute.For<IKpiSnapshotService>();

            var sp = Substitute.For<IServiceProvider>();
            sp.GetService(typeof(IKpiSnapshotService)).Returns(service);
            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(sp);
            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            scopeFactory.CreateScope().Returns(scope);

            var clock = new StubClock(ClockNow);
            var job = new KpiSnapshotJob(scopeFactory, clock,
                new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
                NullLogger<KpiSnapshotJob>.Instance);

            return new Harness { Job = job, Service = service };
        }

        /// <summary>R2173 — harness that wires an always-Skip gate so the job's early-return path is exercised.</summary>
        public static Harness CreateWithSkippingGate()
        {
            var service = Substitute.For<IKpiSnapshotService>();

            var sp = Substitute.For<IServiceProvider>();
            sp.GetService(typeof(IKpiSnapshotService)).Returns(service);
            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(sp);
            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            scopeFactory.CreateScope().Returns(scope);

            var clock = new StubClock(ClockNow);
            var job = new KpiSnapshotJob(scopeFactory, clock,
                new Cnas.Ps.Infrastructure.Tests.Common.AlwaysSkipPeakHourGate(),
                NullLogger<KpiSnapshotJob>.Instance);

            return new Harness { Job = job, Service = service };
        }
    }
}
