using Cnas.Ps.Application.Etl;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Etl;

/// <summary>
/// R0153 / TOR CF 19.05 — tests for <see cref="ContributorPeriodProjectionJob"/>.
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> — the job touches the static
/// <c>cnas.etl.contributor_projection_run</c> counter so cross-test parallelism
/// is suppressed.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public sealed class ContributorPeriodProjectionJobTests
{
    [Fact]
    public async Task Execute_CallsRebuildAllOnce()
    {
        var harness = Harness.Create();
        harness.Service.RebuildAllAsync(Arg.Any<CancellationToken>())
            .Returns(Result<ContributorPeriodProjectionRunDto>.Success(
                new ContributorPeriodProjectionRunDto(null, 5, 2, 42)));

        await harness.Job.Execute(FakeContext());

        await harness.Service.Received(1).RebuildAllAsync(Arg.Any<CancellationToken>());
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

    private sealed class Harness
    {
        public required ContributorPeriodProjectionJob Job { get; init; }
        public required IContributorPeriodProjectionService Service { get; init; }

        public static Harness Create()
        {
            var service = Substitute.For<IContributorPeriodProjectionService>();

            var sp = Substitute.For<IServiceProvider>();
            sp.GetService(typeof(IContributorPeriodProjectionService)).Returns(service);
            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(sp);
            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            scopeFactory.CreateScope().Returns(scope);

            var job = new ContributorPeriodProjectionJob(
                scopeFactory,
                new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
                NullLogger<ContributorPeriodProjectionJob>.Instance);

            return new Harness { Job = job, Service = service };
        }
    }
}
