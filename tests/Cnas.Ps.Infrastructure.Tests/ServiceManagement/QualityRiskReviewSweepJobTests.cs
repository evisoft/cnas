using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.ServiceManagement;

/// <summary>
/// R2506 / TOR PIR 037-040 — tests for
/// <see cref="QualityRiskReviewSweepJob"/>.
/// </summary>
public sealed class QualityRiskReviewSweepJobTests
{
    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static IServiceScopeFactory NewScopeFactory(
        IQualityRiskService service,
        IAuditService audit)
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IQualityRiskService)).Returns(service);
        sp.GetService(typeof(IAuditService)).Returns(audit);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    [Fact]
    public async Task Execute_PeakHourGateSkips_NoOps()
    {
        var svc = Substitute.For<IQualityRiskService>();
        var audit = Substitute.For<IAuditService>();
        var job = new QualityRiskReviewSweepJob(
            NewScopeFactory(svc, audit),
            new AlwaysSkipPeakHourGate(),
            NullLogger<QualityRiskReviewSweepJob>.Instance);

        await job.Execute(NewExecCtx());

        await svc.DidNotReceive().ListOverdueForReviewAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await audit.DidNotReceive().RecordAsync(
            Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_HappyPath_EmitsOneAuditPerOverdueRisk()
    {
        var svc = Substitute.For<IQualityRiskService>();
        IReadOnlyList<QualityRiskDto> overdue = new List<QualityRiskDto>
        {
            new(
                Id: "SQID-1",
                RiskCode: "NEVER_REVIEWED",
                Title: "t",
                Description: "d",
                Category: QualityRiskCategory.Technical.ToString(),
                Likelihood: QualityRiskLikelihood.Possible.ToString(),
                Impact: QualityRiskImpact.Major.ToString(),
                Status: QualityRiskStatus.Open.ToString(),
                OwnerSqid: "SQID-99",
                IdentifiedAt: DateTime.UtcNow,
                LastReviewedAt: null,
                ClosedAt: null,
                ClosureReason: null),
            new(
                Id: "SQID-2",
                RiskCode: "STALE_REVIEW",
                Title: "t",
                Description: "d",
                Category: QualityRiskCategory.Process.ToString(),
                Likelihood: QualityRiskLikelihood.Possible.ToString(),
                Impact: QualityRiskImpact.Moderate.ToString(),
                Status: QualityRiskStatus.Mitigating.ToString(),
                OwnerSqid: "SQID-99",
                IdentifiedAt: DateTime.UtcNow,
                LastReviewedAt: DateTime.UtcNow.AddDays(-400),
                ClosedAt: null,
                ClosureReason: null),
        };
        svc.ListOverdueForReviewAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<QualityRiskDto>>.Success(overdue)));

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var job = new QualityRiskReviewSweepJob(
            NewScopeFactory(svc, audit),
            new AllowAllPeakHourGate(),
            NullLogger<QualityRiskReviewSweepJob>.Instance);

        await job.Execute(NewExecCtx());

        await audit.Received(2).RecordAsync(
            IQualityRiskService.AuditRiskReviewOverdue,
            AuditSeverity.Information,
            "system",
            nameof(QualityRisk),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
