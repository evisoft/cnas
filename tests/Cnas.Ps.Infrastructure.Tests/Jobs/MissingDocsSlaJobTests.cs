using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// Tests for <see cref="MissingDocsSlaJob"/> — the periodic auto-close of applications
/// that have been parked in <see cref="ApplicationStatus.RejectedIncomplete"/> for more
/// than 30 days without action by the citizen (R0934 / TOR §2.5.1).
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> — the job emits on the static meter
/// (<c>cnas.application.auto_closed</c>) so cross-test parallelism is suppressed.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class MissingDocsSlaJobTests
{
    /// <summary>Deterministic clock anchor for all tests.</summary>
    private static readonly DateTime ClockNow = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Execute_NoStaleApplications_DoesNothing()
    {
        var harness = await Harness.CreateAsync();

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceive().RecordAsync(
            Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await harness.Notify.DidNotReceive().EnqueueAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_ApplicationInRejectedIncomplete_LessThan30Days_NotTouched()
    {
        var harness = await Harness.CreateAsync();
        var appId = await harness.SeedApplicationAsync(
            ApplicationStatus.RejectedIncomplete,
            // 29 days ago — still within the 30-day window.
            rejectedIncompleteSinceUtc: ClockNow.AddDays(-29));

        await harness.Job.Execute(FakeContext());

        var reloaded = await harness.Db.Applications.SingleAsync(a => a.Id == appId);
        reloaded.Status.Should().Be(ApplicationStatus.RejectedIncomplete);
        reloaded.RejectedIncompleteSinceUtc.Should().NotBeNull();
        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task Execute_ApplicationInRejectedIncomplete_PastThreshold_FlippedToRejected_AndCleared()
    {
        var harness = await Harness.CreateAsync();
        var appId = await harness.SeedApplicationAsync(
            ApplicationStatus.RejectedIncomplete,
            rejectedIncompleteSinceUtc: ClockNow.AddDays(-31));

        await harness.Job.Execute(FakeContext());

        var reloaded = await harness.Db.Applications.SingleAsync(a => a.Id == appId);
        reloaded.Status.Should().Be(ApplicationStatus.Rejected);
        reloaded.RejectedIncompleteSinceUtc.Should().BeNull();
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);
    }

    [Fact]
    public async Task Execute_FlippedApplication_WritesAuditWithStableEventCode_AndNoPiiInDetails()
    {
        var harness = await Harness.CreateAsync();
        var appId = await harness.SeedApplicationAsync(
            ApplicationStatus.RejectedIncomplete,
            rejectedIncompleteSinceUtc: ClockNow.AddDays(-45));

        await harness.Job.Execute(FakeContext());

        // Exactly one audit row, with the stable event code and the entity id.
        await harness.Audit.Received(1).RecordAsync(
            "APPLICATION.AUTO_CLOSED",
            AuditSeverity.Information,
            Arg.Is<string>(a => a == "system:missing-docs-sla"),
            nameof(ServiceApplication),
            appId,
            Arg.Is<string>(d => DetailsAreSafe(d)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_FlippedApplication_QueuesCitizenNotification()
    {
        var harness = await Harness.CreateAsync();
        var appId = await harness.SeedApplicationAsync(
            ApplicationStatus.RejectedIncomplete,
            rejectedIncompleteSinceUtc: ClockNow.AddDays(-32),
            solicitantId: 4242L,
            referenceNumber: "PS-2026-AAA001");

        await harness.Job.Execute(FakeContext());

        await harness.Notify.Received(1).EnqueueAsync(
            4242L,
            Arg.Is<string>(s => s.Length > 0),
            Arg.Is<string>(b => b.Contains("PS-2026-AAA001") && !b.Contains("IDNP")),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        _ = appId;
    }

    [Fact]
    public async Task Execute_Counter_cnas_application_auto_closed_IncrementsByBatchCount()
    {
        using var capture = new MetricCapture("cnas.application.auto_closed");
        var harness = await Harness.CreateAsync();
        await harness.SeedApplicationAsync(
            ApplicationStatus.RejectedIncomplete, ClockNow.AddDays(-31));
        await harness.SeedApplicationAsync(
            ApplicationStatus.RejectedIncomplete, ClockNow.AddDays(-40));
        await harness.SeedApplicationAsync(
            ApplicationStatus.RejectedIncomplete, ClockNow.AddDays(-31));

        await harness.Job.Execute(FakeContext());

        capture.TotalIncrement.Should().Be(3,
            "the counter increments by the batch count on every successful run.");
    }

    [Theory]
    [InlineData(ApplicationStatus.Draft)]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.UnderExamination)]
    [InlineData(ApplicationStatus.PendingApproval)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Closed)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task Execute_ApplicationInDifferentStatus_NotTouched(ApplicationStatus status)
    {
        var harness = await Harness.CreateAsync();
        // Even if RejectedIncompleteSinceUtc happens to be set to ancient past, the
        // status filter MUST guard against transitions out of RejectedIncomplete.
        var appId = await harness.SeedApplicationAsync(
            status,
            rejectedIncompleteSinceUtc: ClockNow.AddDays(-90));

        await harness.Job.Execute(FakeContext());

        var reloaded = await harness.Db.Applications.SingleAsync(a => a.Id == appId);
        reloaded.Status.Should().Be(status);
    }

    [Fact]
    public async Task Execute_LargeStaleBatch_FlushesAtomically()
    {
        var harness = await Harness.CreateAsync();
        for (var i = 0; i < 100; i++)
        {
            await harness.SeedApplicationAsync(
                ApplicationStatus.RejectedIncomplete,
                rejectedIncompleteSinceUtc: ClockNow.AddDays(-(31 + i % 5)));
        }
        harness.SaveChangesCallCount.Should().BeGreaterThan(0);
        var saveCountBefore = harness.SaveChangesCallCount;

        await harness.Job.Execute(FakeContext());

        var rejectedCount = await harness.Db.Applications
            .CountAsync(a => a.Status == ApplicationStatus.Rejected);
        rejectedCount.Should().Be(100);
        // Exactly one additional flush for the entire batch.
        (harness.SaveChangesCallCount - saveCountBefore).Should().Be(1);
    }

    // ─────────────────────── helpers ───────────────────────

    /// <summary>Returns a no-op <see cref="IJobExecutionContext"/> with a cancellation token.</summary>
    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    /// <summary>
    /// Asserts the audit <c>detailsJson</c> blob carries a stable reason code only and
    /// contains no IDNP, IDNO, names, or other citizen-identifying data (R0185 / SEC 044).
    /// </summary>
    private static bool DetailsAreSafe(string detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson)) return false;
        if (!detailsJson.Contains("missing_docs_timeout", StringComparison.Ordinal)) return false;
        // No 13-digit IDNP/IDNO numeric runs in the payload.
        if (System.Text.RegularExpressions.Regex.IsMatch(detailsJson, @"\d{13}")) return false;
        // No citizen names.
        if (detailsJson.Contains("Popescu", StringComparison.OrdinalIgnoreCase)) return false;
        // Must parse as JSON.
        try
        {
            using var _ = JsonDocument.Parse(detailsJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Deterministic clock for tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// MeterListener-based capture for a single instrument name on
    /// <see cref="CnasMeter.MeterName"/>. Disposes the listener at the end of the test
    /// so the next test starts from a clean slate.
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

    /// <summary>
    /// CnasDbContext subclass that counts SaveChangesAsync calls so the "flushed
    /// atomically" assertion can verify a single round-trip.
    /// </summary>
    private sealed class CountingCnasDbContext : CnasDbContext
    {
        public int SaveChangesCallCount { get; private set; }

        public CountingCnasDbContext(DbContextOptions<CnasDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCallCount++;
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class Harness
    {
        public required CountingCnasDbContext Db { get; init; }
        public required MissingDocsSlaJob Job { get; init; }
        public required IAuditService Audit { get; init; }
        public required INotificationService Notify { get; init; }

        public int SaveChangesCallCount => Db.SaveChangesCallCount;

        public static Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-mdocs-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CountingCnasDbContext(opts);

            var clock = new StubClock(ClockNow);

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

            var job = new MissingDocsSlaJob(db, clock, audit, notify, NullLogger<MissingDocsSlaJob>.Instance);
            return Task.FromResult(new Harness
            {
                Db = db,
                Job = job,
                Audit = audit,
                Notify = notify,
            });
        }

        public async Task<long> SeedApplicationAsync(
            ApplicationStatus status,
            DateTime? rejectedIncompleteSinceUtc = null,
            long? solicitantId = null,
            string? referenceNumber = null)
        {
            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow.AddDays(-90),
                SolicitantId = solicitantId ?? 1000L,
                ServicePassportId = 1L,
                Status = status,
                FormPayloadJson = "{}",
                ReferenceNumber = referenceNumber ?? $"PS-{Guid.NewGuid():N}"[..16],
                RejectedIncompleteSinceUtc = rejectedIncompleteSinceUtc,
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();
            return app.Id;
        }
    }
}
