using System.Diagnostics.Metrics;
using Cnas.Ps.Application.SensitiveActions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.SensitiveActions;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.SensitiveActions;

/// <summary>
/// R2273 / TOR SEC 027 — tests for <see cref="SensitiveAdminActionExpirySweepJob"/>.
/// Verifies the service-dispatch happy-path and the metric emission.
/// </summary>
public sealed class SensitiveAdminActionExpirySweepJobTests
{
    private static IServiceScopeFactory ScopeFactoryFor(ISensitiveAdminActionService service)
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ISensitiveAdminActionService)).Returns(service);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    private static SensitiveAdminActionService NewService(CnasDbContext db)
        => SensitiveActionsTestHelpers.NewService(
            db,
            SensitiveActionsTestHelpers.NewCaller(999L));

    [Fact]
    public async Task Execute_HappyPath_InvokesServiceAndFlipsRow()
    {
        using var db = SensitiveActionsTestHelpers.CreateContext();
        db.SensitiveAdminActions.Add(new SensitiveAdminAction
        {
            ActionCode = "TEST.OP",
            Status = SensitiveAdminActionStatus.PendingApproval,
            RequestedByUserId = 1L,
            RequestedAt = SensitiveActionsTestHelpers.ClockNow.AddDays(-5),
            RequestReason = "Old request",
            RequestPayloadJson = "{}",
            ExpiresAt = SensitiveActionsTestHelpers.ClockNow.AddDays(-2),
            CreatedAtUtc = SensitiveActionsTestHelpers.ClockNow.AddDays(-5),
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var svc = NewService(db);
        var scopes = ScopeFactoryFor(svc);
        var job = new SensitiveAdminActionExpirySweepJob(
            scopes, new AllowAllPeakHourGate(), NullLogger<SensitiveAdminActionExpirySweepJob>.Instance);

        await job.Execute(NewExecCtx());

        var expired = await db.SensitiveAdminActions
            .CountAsync(r => r.Status == SensitiveAdminActionStatus.Expired);
        expired.Should().Be(1);
    }

    [Fact]
    public async Task Execute_EmitsExpiredCounter()
    {
        var observed = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CnasMeter.MeterName && instr.Name == "cnas.sensitive_admin_action.expired")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref observed, value));
        listener.Start();

        using var db = SensitiveActionsTestHelpers.CreateContext();
        db.SensitiveAdminActions.Add(new SensitiveAdminAction
        {
            ActionCode = "TEST.OP",
            Status = SensitiveAdminActionStatus.PendingApproval,
            RequestedByUserId = 1L,
            RequestedAt = SensitiveActionsTestHelpers.ClockNow.AddDays(-3),
            RequestReason = "Stale row",
            RequestPayloadJson = "{}",
            ExpiresAt = SensitiveActionsTestHelpers.ClockNow.AddHours(-1),
            CreatedAtUtc = SensitiveActionsTestHelpers.ClockNow.AddDays(-3),
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var svc = NewService(db);
        var scopes = ScopeFactoryFor(svc);
        var job = new SensitiveAdminActionExpirySweepJob(
            scopes, new AllowAllPeakHourGate(), NullLogger<SensitiveAdminActionExpirySweepJob>.Instance);

        await job.Execute(NewExecCtx());

        observed.Should().BeGreaterThan(0);
    }
}
