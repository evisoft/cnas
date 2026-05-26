using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Integrity;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Integrity;

/// <summary>
/// R2282 / TOR SEC 036 — tests for <see cref="IntegrityCheckJob"/>.
/// Verifies the peak-hour gate skip path, the happy-path persistence, and
/// the counter emission.
/// </summary>
public sealed class IntegrityCheckJobTests
{
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(_ => Result<long>.Failure("INVALID_SQID", "n/a"));
        return sqids;
    }

    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns((long?)null);
        caller.UserSqid.Returns((string?)null);
        return caller;
    }

    private static IAuditService NewAudit()
    {
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        return audit;
    }

    private static IServiceScopeFactory NewScopeFactory(
        CnasDbContext db,
        IIntegrityCheckService service)
    {
        // Extract the audit substitute up-front — NSubstitute requires the
        // Returns() value to be a stored reference, not an inline call that
        // would itself touch the substitute machinery.
        var audit = NewAudit();
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IIntegrityCheckService)).Returns(service);
        sp.GetService(typeof(IAuditService)).Returns(audit);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    private static IntegrityCheckService NewService(CnasDbContext db, IEnumerable<IIntegrityCheck> checks)
        => new(
            db: db,
            checkContext: IntegrityTestHelpers.WrapContext(db),
            checks: checks,
            audit: NewAudit(),
            sqids: NewSqidMock(),
            clock: new IntegrityTestHelpers.StubClock(IntegrityTestHelpers.ClockNow),
            caller: NewCaller(),
            filterValidator: new IntegrityFindingFilterValidator(),
            ackValidator: new IntegrityFindingAcknowledgeInputValidator());

    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    [Fact]
    public async Task Execute_PeakHourGateSkips_DoesNothing()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var svc = NewService(db, Array.Empty<IIntegrityCheck>());
        var scopes = NewScopeFactory(db, svc);
        var job = new IntegrityCheckJob(scopes, new AlwaysSkipPeakHourGate(), NullLogger<IntegrityCheckJob>.Instance);

        await job.Execute(NewExecCtx());

        var rows = await db.IntegrityCheckRuns.ToListAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_HappyPath_PersistsCompletedRun()
    {
        // Snapshot the counter via MeterListener — process-static state.
        var observed = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CnasMeter.MeterName && instr.Name == "cnas.integrity_check.run_completed")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref observed, value));
        listener.Start();

        using var db = IntegrityTestHelpers.CreateContext();
        db.UserProfiles.Add(new UserProfile
        {
            DisplayName = "Carol",
            NationalId = "2000000000009",
            NationalIdHash = null,
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        });
        await db.SaveChangesAsync();

        var svc = NewService(db, new IIntegrityCheck[]
        {
            new Cnas.Ps.Infrastructure.Services.Integrity.Checks.UserProfileNationalIdHashSyncCheck(),
        });
        var scopes = NewScopeFactory(db, svc);
        var job = new IntegrityCheckJob(scopes, new AllowAllPeakHourGate(), NullLogger<IntegrityCheckJob>.Instance);

        await job.Execute(NewExecCtx());

        var runs = await db.IntegrityCheckRuns.ToListAsync();
        runs.Should().ContainSingle();
        runs[0].Status.Should().Be(IntegrityCheckRunStatus.Completed);
        runs[0].TriggerKind.Should().Be(IntegrityCheckTriggerKind.Scheduled);
        runs[0].TotalFindings.Should().Be(1);

        listener.RecordObservableInstruments();
        observed.Should().BeGreaterThan(0);
    }
}
