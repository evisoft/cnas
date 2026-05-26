using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Scheduling;

/// <summary>
/// R2173 / TOR PSR 004 — unit tests for the <see cref="PeakHourGate"/>
/// production implementation. Validates the profile/window math, the
/// global-override toggle, the audit emission on Skip, and the metric
/// emission on every evaluation.
/// </summary>
/// <remarks>
/// Europe/Chisinau is the canonical local timezone. In May the offset is
/// UTC+3, so a UTC time of <c>00:00</c> corresponds to local <c>03:00</c>
/// (off-peak) and a UTC time of <c>11:00</c> corresponds to local <c>14:00</c>
/// (peak). Tests construct their clock instants in UTC and pick offsets that
/// land cleanly on the intended local hour regardless of DST drift.
/// </remarks>
public sealed class PeakHourGateTests
{
    /// <summary>UTC instant whose Europe/Chisinau equivalent is 03:00 — inside the 22..06 off-peak window.</summary>
    private static readonly DateTime OffPeakUtc = new(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>UTC instant whose Europe/Chisinau equivalent is 14:00 — peak hour.</summary>
    private static readonly DateTime PeakUtc = new(2026, 5, 22, 11, 0, 0, DateTimeKind.Utc);

    /// <summary>UTC instant whose Europe/Chisinau equivalent is 23:30 — inside the wrap-around window.</summary>
    private static readonly DateTime WrapEveningUtc = new(2026, 5, 22, 20, 30, 0, DateTimeKind.Utc);

    /// <summary>UTC instant whose Europe/Chisinau equivalent is 05:30 — inside the wrap-around window.</summary>
    private static readonly DateTime WrapMorningUtc = new(2026, 5, 22, 2, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task EvaluateAsync_OffPeakOnly_InsideWindow_ReturnsAllow()
    {
        var gate = BuildGate(now: OffPeakUtc).Gate;

        var decision = await gate.EvaluateAsync(JobScheduleProfileRegistry.KpiSnapshot, CancellationToken.None);

        decision.Should().Be(PeakHourGateDecision.Allow);
    }

    [Fact]
    public async Task EvaluateAsync_OffPeakOnly_OutsideWindow_ReturnsSkip()
    {
        var gate = BuildGate(now: PeakUtc).Gate;

        var decision = await gate.EvaluateAsync(JobScheduleProfileRegistry.KpiSnapshot, CancellationToken.None);

        decision.Should().Be(PeakHourGateDecision.Skip);
    }

    [Fact]
    public async Task EvaluateAsync_Anytime_DuringPeak_ReturnsAllow()
    {
        // AdminActionBacklogObserver is Anytime — should fire any hour.
        var gate = BuildGate(now: PeakUtc).Gate;

        var decision = await gate.EvaluateAsync(
            JobScheduleProfileRegistry.AdminActionBacklogObserver,
            CancellationToken.None);

        decision.Should().Be(PeakHourGateDecision.Allow);
    }

    [Fact]
    public async Task EvaluateAsync_Always_DuringPeak_ReturnsAllow()
    {
        // SiemForwarder is Always — should fire any hour.
        var gate = BuildGate(now: PeakUtc).Gate;

        var decision = await gate.EvaluateAsync(
            JobScheduleProfileRegistry.SiemForwarder,
            CancellationToken.None);

        decision.Should().Be(PeakHourGateDecision.Allow);
    }

    [Fact]
    public async Task EvaluateAsync_GlobalOverride_DuringPeak_OffPeakOnly_ReturnsAllow()
    {
        var harness = BuildGate(now: PeakUtc, globalOverride: true);

        var decision = await harness.Gate.EvaluateAsync(
            JobScheduleProfileRegistry.KpiSnapshot,
            CancellationToken.None);

        decision.Should().Be(PeakHourGateDecision.Allow);
    }

    [Fact]
    public async Task EvaluateAsync_UnknownJobCode_DefaultsToAllow()
    {
        // Unknown jobs default to Anytime — gate must never accidentally suppress.
        var gate = BuildGate(now: PeakUtc).Gate;

        var decision = await gate.EvaluateAsync("CompletelyUnknownJob", CancellationToken.None);

        decision.Should().Be(PeakHourGateDecision.Allow);
    }

    [Fact]
    public async Task EvaluateAsync_WrapAroundWindow_23h30_IsOffPeak()
    {
        // 23:30 local — inside the 22..06 wrap-around window.
        var gate = BuildGate(now: WrapEveningUtc).Gate;

        var decision = await gate.EvaluateAsync(
            JobScheduleProfileRegistry.KpiSnapshot,
            CancellationToken.None);

        decision.Should().Be(PeakHourGateDecision.Allow);
    }

    [Fact]
    public async Task EvaluateAsync_WrapAroundWindow_05h30_IsOffPeak()
    {
        // 05:30 local — inside the 22..06 wrap-around window.
        var gate = BuildGate(now: WrapMorningUtc).Gate;

        var decision = await gate.EvaluateAsync(
            JobScheduleProfileRegistry.KpiSnapshot,
            CancellationToken.None);

        decision.Should().Be(PeakHourGateDecision.Allow);
    }

    [Fact]
    public async Task EvaluateAsync_Skip_IncrementsSkipTaggedCounter()
    {
        using var capture = new MetricCapture("cnas.peak_hour.gate");
        var gate = BuildGate(now: PeakUtc).Gate;

        await gate.EvaluateAsync(JobScheduleProfileRegistry.KpiSnapshot, CancellationToken.None);

        capture.Tags.Should().Contain(t =>
            t.Any(kv => kv.Key == "decision" && kv.Value as string == "skip"));
    }

    [Fact]
    public async Task EvaluateAsync_Allow_IncrementsAllowTaggedCounter()
    {
        using var capture = new MetricCapture("cnas.peak_hour.gate");
        var gate = BuildGate(now: OffPeakUtc).Gate;

        await gate.EvaluateAsync(JobScheduleProfileRegistry.KpiSnapshot, CancellationToken.None);

        capture.Tags.Should().Contain(t =>
            t.Any(kv => kv.Key == "decision" && kv.Value as string == "allow"));
    }

    [Fact]
    public async Task EvaluateAsync_Skip_EmitsInformationAuditRow()
    {
        var harness = BuildGate(now: PeakUtc);

        await harness.Gate.EvaluateAsync(JobScheduleProfileRegistry.KpiSnapshot, CancellationToken.None);

        await harness.Audit.Received(1).RecordAsync(
            "JOB.SKIPPED_BY_PEAK_HOUR_GATE",
            AuditSeverity.Information,
            "system",
            "QuartzJob",
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsHourInsideWindow_NonWrap_InsideBounds_ReturnsTrue()
    {
        PeakHourGate.IsHourInsideWindow(hour: 12, start: 9, end: 17).Should().BeTrue();
    }

    [Fact]
    public void IsHourInsideWindow_NonWrap_OutsideBounds_ReturnsFalse()
    {
        PeakHourGate.IsHourInsideWindow(hour: 20, start: 9, end: 17).Should().BeFalse();
    }

    [Fact]
    public void IsHourInsideWindow_Wrap_PostMidnight_ReturnsTrue()
    {
        // 03:00 inside 22..06 wrap window.
        PeakHourGate.IsHourInsideWindow(hour: 3, start: 22, end: 6).Should().BeTrue();
    }

    [Fact]
    public void IsHourInsideWindow_Wrap_DuringDay_ReturnsFalse()
    {
        // 14:00 outside 22..06 wrap window.
        PeakHourGate.IsHourInsideWindow(hour: 14, start: 22, end: 6).Should().BeFalse();
    }

    // ─────────────────────── helpers ───────────────────────

    /// <summary>Deterministic clock helper.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Harness bundle returned by <see cref="BuildGate(DateTime, bool, int, int)"/>.</summary>
    private sealed class Harness
    {
        public required PeakHourGate Gate { get; init; }
        public required IAuditService Audit { get; init; }
    }

    /// <summary>Builds a fully wired gate for tests.</summary>
    private static Harness BuildGate(
        DateTime now,
        bool globalOverride = false,
        int offPeakStartLocalHour = 22,
        int offPeakEndLocalHour = 6)
    {
        var clock = new StubClock(now);
        var options = Options.Create(new PeakHourGateOptions
        {
            OffPeakStartLocalHour = offPeakStartLocalHour,
            OffPeakEndLocalHour = offPeakEndLocalHour,
            GlobalOverride = globalOverride,
        });
        var monitor = new StaticOptionsMonitor<PeakHourGateOptions>(options.Value);

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
            Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IAuditService)).Returns(audit);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var overrideStore = new PeakHourGateOverrideStore(globalOverride);
        var gate = new PeakHourGate(clock, monitor, overrideStore, scopeFactory, NullLogger<PeakHourGate>.Instance);
        return new Harness { Gate = gate, Audit = audit };
    }

    /// <summary>Static IOptionsMonitor that always returns the supplied snapshot.</summary>
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>MeterListener-based capture for a single instrument name.</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<long> _measurements = new();
        private readonly List<IReadOnlyList<KeyValuePair<string, object?>>> _tags = new();
        private readonly object _gate = new();

        public IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> Tags
        {
            get { lock (_gate) return _tags.ToList(); }
        }

        public MetricCapture(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName && instrument.Name == instrumentName)
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
}
