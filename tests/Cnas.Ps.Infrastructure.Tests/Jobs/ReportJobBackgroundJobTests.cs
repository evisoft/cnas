using Cnas.Ps.Application.Reports;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — Quartz wrapper tests for
/// <see cref="ReportJobBackgroundJob"/>. Asserts the job delegates to
/// <see cref="IReportJobRunner.RunBatchAsync"/> exactly once per fire and
/// emits the success-tagged counter.
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> — the job writes to the static
/// <c>cnas.report_job.run</c> counter so cross-test parallelism is suppressed.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public sealed class ReportJobBackgroundJobTests
{
    [Fact]
    public async Task Execute_CallsRunBatchAsyncOnce_PerQuartzTrigger()
    {
        var harness = Harness.Create();
        harness.Runner.RunBatchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(0);

        await harness.Job.Execute(FakeContext());

        await harness.Runner.Received(1).RunBatchAsync(
            ReportJobBackgroundJob.BatchSize,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_OnSuccess_IncrementsCounterWithOutcomeSuccessTag()
    {
        using var capture = new MetricCapture("cnas.report_job.run");
        var harness = Harness.Create();
        harness.Runner.RunBatchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(3);

        await harness.Job.Execute(FakeContext());

        capture.TotalIncrement.Should().Be(1);
        capture.Tags.Should().Contain(t =>
            t.Any(kv => kv.Key == "outcome" && kv.Value as string == "success"));
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
        public required ReportJobBackgroundJob Job { get; init; }
        public required IReportJobRunner Runner { get; init; }

        public static Harness Create()
        {
            var runner = Substitute.For<IReportJobRunner>();
            var sp = Substitute.For<IServiceProvider>();
            sp.GetService(typeof(IReportJobRunner)).Returns(runner);
            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(sp);
            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            scopeFactory.CreateScope().Returns(scope);

            var job = new ReportJobBackgroundJob(
                scopeFactory,
                new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
                NullLogger<ReportJobBackgroundJob>.Instance);

            return new Harness { Job = job, Runner = runner };
        }
    }
}
