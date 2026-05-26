using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// Unit tests for <see cref="FailedJobListener"/> — the Quartz <see cref="IJobListener"/>
/// that captures failed job executions into the dead-letter queue (CLAUDE.md §6.2).
/// </summary>
/// <remarks>
/// The listener never records on success; it always records on failure (even for
/// recurring jobs — every failed fire is independently inspectable). The PII-scrubbing
/// surface is exercised separately so a regression to the scrub list shows up as a
/// targeted test failure rather than a vague "DLQ row contained too much".
/// </remarks>
public class FailedJobListenerTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 20, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task JobSucceeded_NoFailureRecorded()
    {
        var harness = Harness.Create();
        var ctx = FakeContext(jobName: "mpay-dispatcher");

        await harness.Listener.JobWasExecuted(ctx, jobException: null, CancellationToken.None);

        await harness.Store.DidNotReceive().RecordFailureAsync(
            Arg.Any<FailedJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JobFailed_RecordsFailedJob()
    {
        var harness = Harness.Create();
        FailedJob? captured = null;
        harness.Store
            .RecordFailureAsync(Arg.Do<FailedJob>(e => captured = e), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var thrown = MakeException("upstream 503 from MPay");
        var ctx = FakeContext(jobName: "mpay-dispatcher", refireCount: 0);
        await harness.Listener.JobWasExecuted(ctx, new JobExecutionException(thrown), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.JobName.Should().Be("mpay-dispatcher");
        captured.JobGroup.Should().Be("DEFAULT");
        captured.FailedAtUtc.Should().Be(ClockNow);
        // Quartz wraps the original exception in JobExecutionException; the listener
        // unwraps via InnerException so the operator sees the real cause.
        captured.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        captured.ExceptionMessage.Should().Contain("upstream 503");
        captured.RefireCount.Should().Be(0);
        captured.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task JobFailed_ScrubsPiiFromJobData()
    {
        var harness = Harness.Create();
        FailedJob? captured = null;
        harness.Store
            .RecordFailureAsync(Arg.Do<FailedJob>(e => captured = e), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        // Seed JobDataMap with one PII key, one secret key, and one benign key. The
        // listener must redact the first two and pass the third through verbatim.
        var dataMap = new JobDataMap
        {
            ["idnp"] = "2000000000007",
            ["rspToken"] = "secret-bearer-AAA",
            ["dossierNumber"] = "D-2026-AAAAA009",
        };
        var ctx = FakeContext(jobName: "mconnect-sync", mergedDataMap: dataMap);
        var thrown = MakeException("MConnect timed out");
        await harness.Listener.JobWasExecuted(ctx, new JobExecutionException(thrown), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.JobDataJson.Should().NotBeNullOrEmpty();
        captured.JobDataJson.Should().Contain("\"dossierNumber\":\"D-2026-AAAAA009\"");
        captured.JobDataJson.Should().Contain("<redacted>");
        captured.JobDataJson.Should().NotContain("2000000000007");
        captured.JobDataJson.Should().NotContain("secret-bearer-AAA");
    }

    [Fact]
    public async Task JobFailed_TruncatesLongStackTrace()
    {
        var harness = Harness.Create();
        FailedJob? captured = null;
        harness.Store
            .RecordFailureAsync(Arg.Do<FailedJob>(e => captured = e), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        // Synthesise an exception with a >50k char stack trace via a custom subclass so
        // we don't depend on a particular runtime's actual stack depth.
        var thrown = new HugeStackTraceException(new string('S', 50_000));
        var ctx = FakeContext(jobName: "dossier-sla-monitor");
        await harness.Listener.JobWasExecuted(ctx, new JobExecutionException(thrown), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.StackTrace.Should().NotBeNull();
        captured.StackTrace!.Length.Should().BeLessThanOrEqualTo(FailedJobListener.MaxStackTraceLength);
        captured.StackTrace.Length.Should().Be(FailedJobListener.MaxStackTraceLength);
    }

    // ─────────────────────── Test plumbing ───────────────────────

    /// <summary>
    /// Builds a minimal <see cref="IJobExecutionContext"/> double with the supplied
    /// <paramref name="jobName"/> and an empty (or supplied) merged data map.
    /// </summary>
    private static IJobExecutionContext FakeContext(
        string jobName = "test-job",
        string jobGroup = "DEFAULT",
        int refireCount = 0,
        JobDataMap? mergedDataMap = null)
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        var detail = Substitute.For<IJobDetail>();
        detail.Key.Returns(new JobKey(jobName, jobGroup));
        ctx.JobDetail.Returns(detail);
        ctx.RefireCount.Returns(refireCount);
        ctx.MergedJobDataMap.Returns(mergedDataMap ?? new JobDataMap());
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    /// <summary>
    /// Returns a <see cref="InvalidOperationException"/> instance whose stack trace has
    /// been populated by an actual <c>throw</c> + <c>catch</c> — Exception.StackTrace is
    /// otherwise null when the exception is merely constructed.
    /// </summary>
    private static Exception MakeException(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// Custom exception whose <see cref="Exception.StackTrace"/> property is overridden
    /// to a supplied long string so the truncation path can be exercised deterministically
    /// (real exceptions rarely produce 50k-char stacks).
    /// </summary>
    private sealed class HugeStackTraceException(string stack) : Exception("synthetic")
    {
        private readonly string _stack = stack;

        /// <inheritdoc />
        public override string StackTrace => _stack;
    }

    private sealed class Harness
    {
        public required FailedJobListener Listener { get; init; }
        public required IFailedJobStore Store { get; init; }

        public static Harness Create()
        {
            var store = Substitute.For<IFailedJobStore>();
            store.RecordFailureAsync(Arg.Any<FailedJob>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Result.Success()));
            var listener = new FailedJobListener(
                store, new StubClock(ClockNow), NullLogger<FailedJobListener>.Instance);
            return new Harness { Listener = listener, Store = store };
        }
    }
}
