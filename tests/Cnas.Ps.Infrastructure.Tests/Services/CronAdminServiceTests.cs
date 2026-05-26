using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Scheduling;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0200 / TOR CF 20.01-03, MR 012 — tests for <see cref="CronAdminService"/>. Pins
/// the list / upsert / pause / resume contracts, the cron validation, and the audit
/// emission for each mutation.
/// </summary>
public sealed class CronAdminServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-cron-admin-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static ISqidService NewSqids()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        return s;
    }

    private static ICallerContext NewCaller()
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(1L);
        c.UserSqid.Returns("SQID-1");
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-cron");
        return c;
    }

    private static IAuditService NewAuditCapturing(out List<string> codes)
    {
        var list = new List<string>();
        codes = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                list.Add(c.ArgAt<string>(0));
                return Task.FromResult(Result.Success());
            });
        return a;
    }

    private static async Task<IScheduler> NewSchedulerWithProbeJobsAsync(params string[] jobCodes)
    {
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"cron-admin-test-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz",
        };
        var factory = new Quartz.Impl.StdSchedulerFactory(props);
        var scheduler = await factory.GetScheduler();
        foreach (var code in jobCodes)
        {
            var jobKey = new JobKey(code);
            var jobDetail = JobBuilder.Create<ProbeJob>()
                .WithIdentity(jobKey)
                .Build();
            var trigger = TriggerBuilder.Create()
                .WithIdentity($"{code}-trigger")
                .ForJob(jobKey)
                .WithCronSchedule("0 0 12 * * ?")
                .Build();
            await scheduler.ScheduleJob(jobDetail, trigger);
        }
        return scheduler;
    }

    private static CronAdminService NewService(
        CnasDbContext db,
        IScheduler scheduler,
        IAuditService audit)
    {
        var schedulerFactory = Substitute.For<ISchedulerFactory>();
        schedulerFactory.GetScheduler(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scheduler));
        return new CronAdminService(
            db: db,
            read: db,
            clock: new StubClock(),
            sqids: NewSqids(),
            caller: NewCaller(),
            audit: audit,
            schedulerFactory: schedulerFactory,
            cronValidator: new CronExpressionInputValidator());
    }

    /// <summary>
    /// <c>ListAsync</c> returns one row per registered Quartz job; jobs with no override
    /// surface as <c>IsOverridden=false</c> and rows with one mirror the override values.
    /// </summary>
    [Fact]
    public async Task ListAsync_MergesOverridesWithDefaults()
    {
        await using var db = CreateContext();
        var scheduler = await NewSchedulerWithProbeJobsAsync("alpha", "beta");
        // Override only alpha.
        db.JobScheduleOverrides.Add(new JobScheduleOverride
        {
            JobCode = "alpha",
            CronExpression = "0 0/5 * * * ?",
            IsPaused = false,
            CreatedAtUtc = ClockNow,
            CreatedBy = "admin",
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var svc = NewService(db, scheduler, NewAuditCapturing(out _));

        var result = await svc.ListAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var rows = result.Value!;
        rows.Should().HaveCount(2);
        rows[0].JobCode.Should().Be("alpha");
        rows[0].IsOverridden.Should().BeTrue();
        rows[0].CronExpression.Should().Be("0 0/5 * * * ?");
        rows[1].JobCode.Should().Be("beta");
        rows[1].IsOverridden.Should().BeFalse();
        rows[1].CronExpression.Should().Be(rows[1].DefaultCronExpression);
    }

    /// <summary>
    /// <c>UpsertAsync</c> with a valid cron creates an override row, emits the
    /// CRON.SCHEDULE.UPSERTED audit event, and applies the change to the scheduler.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_ValidCron_PersistsAndAuditsAndReschedules()
    {
        await using var db = CreateContext();
        var scheduler = await NewSchedulerWithProbeJobsAsync("alpha");
        var audit = NewAuditCapturing(out var codes);
        var svc = NewService(db, scheduler, audit);

        var result = await svc.UpsertAsync(
            "alpha",
            new CronExpressionInputDto("0 0/5 * * * ?"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CronExpression.Should().Be("0 0/5 * * * ?");
        result.Value.IsOverridden.Should().BeTrue();
        codes.Should().Contain(ICronAdminService.AuditCronUpserted);

        // Verify the scheduler trigger was rescheduled.
        var trigger = await scheduler.GetTrigger(new TriggerKey("alpha-trigger"));
        trigger.Should().BeAssignableTo<ICronTrigger>();
        ((ICronTrigger)trigger!).CronExpressionString.Should().Be("0 0/5 * * * ?");
    }

    /// <summary>
    /// <c>UpsertAsync</c> with an invalid cron expression returns the
    /// CRON.INVALID_EXPRESSION code without persisting or rescheduling anything.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_InvalidCron_FailsWithInvalidCronCode()
    {
        await using var db = CreateContext();
        var scheduler = await NewSchedulerWithProbeJobsAsync("alpha");
        var svc = NewService(db, scheduler, NewAuditCapturing(out _));

        var result = await svc.UpsertAsync(
            "alpha",
            new CronExpressionInputDto("not a cron"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ICronAdminService.InvalidCronCode);
        (await db.JobScheduleOverrides.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// <c>UpsertAsync</c> against a job that is not registered with the scheduler returns
    /// CRON.UNKNOWN_JOB_CODE; the route is the only protection against typos at the
    /// admin REST surface.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_UnknownJob_FailsWithUnknownJobCode()
    {
        await using var db = CreateContext();
        var scheduler = await NewSchedulerWithProbeJobsAsync("alpha");
        var svc = NewService(db, scheduler, NewAuditCapturing(out _));

        var result = await svc.UpsertAsync(
            "ghost",
            new CronExpressionInputDto("0 0/5 * * * ?"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ICronAdminService.UnknownJobCode);
    }

    /// <summary>
    /// <c>PauseAsync</c> sets <c>IsPaused=true</c> on the override row, emits the
    /// CRON.SCHEDULE.PAUSED audit event, and pauses the job in the live scheduler.
    /// </summary>
    [Fact]
    public async Task PauseAsync_SetsPausedFlagAndAudits()
    {
        await using var db = CreateContext();
        var scheduler = await NewSchedulerWithProbeJobsAsync("alpha");
        var audit = NewAuditCapturing(out var codes);
        var svc = NewService(db, scheduler, audit);
        await scheduler.Start();

        var result = await svc.PauseAsync("alpha", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsPaused.Should().BeTrue();
        codes.Should().Contain(ICronAdminService.AuditCronPaused);
    }

    /// <summary>
    /// <c>ResumeAsync</c> clears <c>IsPaused</c> on the override row and emits the
    /// CRON.SCHEDULE.RESUMED audit event.
    /// </summary>
    [Fact]
    public async Task ResumeAsync_ClearsPausedFlagAndAudits()
    {
        await using var db = CreateContext();
        var scheduler = await NewSchedulerWithProbeJobsAsync("alpha");
        var audit = NewAuditCapturing(out var codes);
        var svc = NewService(db, scheduler, audit);
        // Seed an already-paused row.
        db.JobScheduleOverrides.Add(new JobScheduleOverride
        {
            JobCode = "alpha",
            CronExpression = "0 0 12 * * ?",
            IsPaused = true,
            CreatedAtUtc = ClockNow.AddMinutes(-1),
            CreatedBy = "admin",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await svc.ResumeAsync("alpha", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsPaused.Should().BeFalse();
        codes.Should().Contain(ICronAdminService.AuditCronResumed);
    }

    /// <summary>Inert Quartz probe job used by the harness's scheduler.</summary>
    private sealed class ProbeJob : IJob
    {
        /// <inheritdoc />
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }
}
