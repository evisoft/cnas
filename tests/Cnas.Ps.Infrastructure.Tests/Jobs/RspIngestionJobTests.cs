using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ExternalSources;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// R0203 / TOR CF 20.06 — tests for <see cref="RspIngestionJob"/>. Verifies
/// the job defers to the ingestion service with the canonical RSP source
/// code and the today-UTC as-of date when the gate allows.
/// </summary>
public sealed class RspIngestionJobTests
{
    /// <summary>Stub clock returning a fixed UTC instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Returns a no-op Quartz job-execution context.</summary>
    /// <returns>Configured context.</returns>
    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-rsp");
        return ctx;
    }

    /// <summary>
    /// Happy-path: the gate allows, the job resolves the ingestion service
    /// from a fresh DI scope, and invokes
    /// <c>TriggerScheduledRunAsync("RSP", today)</c>.
    /// </summary>
    [Fact]
    public async Task Execute_GateAllows_InvokesScheduledIngestion()
    {
        var clockNow = new DateTime(2026, 5, 24, 2, 0, 0, DateTimeKind.Utc);
        var ingestion = Substitute.For<IExternalSourceIngestionService>();
        ingestion.TriggerScheduledRunAsync(
                Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ExternalSourceIngestionRunDto>.Success(
                new ExternalSourceIngestionRunDto(
                    Id: "SQID-1",
                    RunNumber: "ESI-2026-000001",
                    SourceCode: "RSP",
                    Status: "Completed",
                    TriggerKind: "Scheduled",
                    StartedAtUtc: clockNow,
                    CompletedAtUtc: clockNow,
                    TotalRecordsPulled: 0,
                    TotalRecordsApplied: 0,
                    TotalRecordsSkipped: 0,
                    TotalRecordsFailed: 0,
                    FailureReason: null,
                    UpstreamPullId: null))));

        var services = new ServiceCollection();
        services.AddSingleton<ICnasTimeProvider>(new StubClock(clockNow));
        services.AddSingleton(ingestion);
        await using var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var job = new RspIngestionJob(scopeFactory, new AllowAllPeakHourGate(),
            NullLogger<RspIngestionJob>.Instance);
        await job.Execute(FakeContext());

        await ingestion.Received(1).TriggerScheduledRunAsync(
            "RSP",
            new DateOnly(2026, 5, 24),
            Arg.Any<CancellationToken>());
    }
}
