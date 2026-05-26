using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Templates;

/// <summary>
/// R2003 / R0133 — tests for <see cref="TemplateLanguageCoverageScanJob"/>.
/// Verifies the peak-hour-gate skip path and the happy-path invocation of
/// <see cref="ITemplateLanguageCoverageService.RecordCoverageRunAsync"/>.
/// </summary>
public sealed class TemplateLanguageCoverageScanJobTests
{
    /// <summary>CA1861 — hoisted to a static field to avoid per-call allocation.</summary>
    private static readonly string[] EnRoRu = ["en", "ro", "ru"];

    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    private static IServiceScopeFactory NewScopeFactory(
        ITemplateLanguageCoverageService service,
        IAuditService audit)
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ITemplateLanguageCoverageService)).Returns(service);
        sp.GetService(typeof(IAuditService)).Returns(audit);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    [Fact]
    public async Task Execute_PeakHourGateSkips_DoesNotInvokeService()
    {
        var service = Substitute.For<ITemplateLanguageCoverageService>();
        var audit = Substitute.For<IAuditService>();
        var scopes = NewScopeFactory(service, audit);
        var job = new TemplateLanguageCoverageScanJob(
            scopes,
            new AlwaysSkipPeakHourGate(),
            NullLogger<TemplateLanguageCoverageScanJob>.Instance);

        await job.Execute(NewExecCtx());

        await service.DidNotReceive().RecordCoverageRunAsync(
            Arg.Any<TemplateLanguageCoverageFilterDto>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_HappyPath_InvokesRecordCoverageRunAsync()
    {
        var emptyReport = new TemplateLanguageCoverageReportDto(
            TotalTemplatesScanned: 0,
            TotalTemplatesFullyCovered: 0,
            TotalTemplatesWithGaps: 0,
            RequiredLanguages: EnRoRu,
            Gaps: System.Array.Empty<TemplateLanguageCoverageGapDto>(),
            Total: 0,
            Skip: 0,
            Take: 100,
            ComputedAtUtc: new System.DateTime(2026, 5, 23, 3, 45, 0, System.DateTimeKind.Utc));

        var service = Substitute.For<ITemplateLanguageCoverageService>();
        service.RecordCoverageRunAsync(
                Arg.Any<TemplateLanguageCoverageFilterDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TemplateLanguageCoverageReportDto>.Success(emptyReport)));
        var audit = Substitute.For<IAuditService>();
        var scopes = NewScopeFactory(service, audit);
        var job = new TemplateLanguageCoverageScanJob(
            scopes,
            new AllowAllPeakHourGate(),
            NullLogger<TemplateLanguageCoverageScanJob>.Instance);

        await job.Execute(NewExecCtx());

        await service.Received(1).RecordCoverageRunAsync(
            Arg.Is<TemplateLanguageCoverageFilterDto>(f =>
                f.OnlyApproved == true && f.IncludeRetiredTemplates == false),
            Arg.Any<CancellationToken>());
    }
}
