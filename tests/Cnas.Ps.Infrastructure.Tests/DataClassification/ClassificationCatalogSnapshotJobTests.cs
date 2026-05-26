using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.DataClassification;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.DataClassification;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.DataClassification;

/// <summary>
/// R2279 / TOR SEC 033 — tests for
/// <see cref="ClassificationCatalogSnapshotJob"/>. Verifies the peak-hour
/// gate skip path and the happy-path persistence with metric emission.
/// </summary>
public sealed class ClassificationCatalogSnapshotJobTests
{
    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    private static IServiceScopeFactory NewScopeFactory(
        CnasDbContext db,
        IClassificationCatalogService service)
    {
        // Build the dependent substitutes outside the Returns(...) calls —
        // NSubstitute's call-recorder forbids nested substitute construction
        // inside its argument lists.
        var auditSub = ClassificationCatalogTestHelpers.NewAudit(out _);
        var sqidsSub = ClassificationCatalogTestHelpers.NewSqidMock();

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IClassificationCatalogService)).Returns(service);
        sp.GetService(typeof(IReadOnlyCnasDbContext)).Returns(db);
        sp.GetService(typeof(IAuditService)).Returns(auditSub);
        sp.GetService(typeof(ISqidService)).Returns(sqidsSub);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    [Fact]
    public async Task Execute_PeakHourGateSkips_NoSnapshotPersisted()
    {
        using var db = ClassificationCatalogTestHelpers.CreateContext();
        var svc = Substitute.For<IClassificationCatalogService>();
        var scopes = NewScopeFactory(db, svc);
        var job = new ClassificationCatalogSnapshotJob(
            scopes,
            new AlwaysSkipPeakHourGate(),
            NullLogger<ClassificationCatalogSnapshotJob>.Instance);

        await job.Execute(NewExecCtx());

        var snapshots = await db.ClassificationCatalogSnapshots.ToListAsync();
        snapshots.Should().BeEmpty();
        await svc.DidNotReceive().CaptureScheduledSnapshotAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_HappyPath_InvokesServiceAndEmitsCounter()
    {
        // Snapshot the counter via MeterListener — process-static state.
        var observed = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CnasMeter.MeterName
                && instr.Name == "cnas.classification.snapshot_captured")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref observed, value));
        listener.Start();

        using var db = ClassificationCatalogTestHelpers.CreateContext();
        var scanner = ClassificationCatalogTestHelpers.NewStubScanner(new[]
        {
            new Cnas.Ps.Contracts.ScannedPropertyDto(
                TypeFullName: "Cnas.Ps.Contracts.SampleDto",
                PropertyName: "Code",
                Label: "Public",
                IsExplicit: true,
                DeclaringAssembly: "Cnas.Ps.Contracts"),
        });
        var audit = ClassificationCatalogTestHelpers.NewAudit(out _);
        var realService = ClassificationCatalogTestHelpers.NewService(db, scanner, audit);
        var scopes = NewScopeFactory(db, realService);
        var job = new ClassificationCatalogSnapshotJob(
            scopes,
            new AllowAllPeakHourGate(),
            NullLogger<ClassificationCatalogSnapshotJob>.Instance);

        await job.Execute(NewExecCtx());

        var snapshots = await db.ClassificationCatalogSnapshots.ToListAsync();
        snapshots.Should().ContainSingle();
        snapshots[0].Status.Should().Be(ClassificationSnapshotStatus.Captured);
        snapshots[0].TriggerKind.Should().Be(ClassificationSnapshotTriggerKind.Scheduled);

        listener.RecordObservableInstruments();
        observed.Should().BeGreaterThan(0);
    }
}
