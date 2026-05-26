using System;
using System.Collections.Generic;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Migration;
using Cnas.Ps.Infrastructure.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Migration;

/// <summary>
/// R2430 / R2431 / R2433 / TOR M4 — shared helpers for the migration
/// framework test suite.
/// </summary>
internal static class MigrationTestHelpers
{
    /// <summary>Canonical "now" used across the migration tests.</summary>
    public static readonly DateTime ClockNow = new(2026, 5, 23, 4, 0, 0, DateTimeKind.Utc);

    /// <summary>Test-only fixed UTC clock.</summary>
    public sealed class StubClock : ICnasTimeProvider
    {
        /// <summary>Constructs the clock.</summary>
        /// <param name="now">Instant returned from <see cref="UtcNow"/>.</param>
        public StubClock(DateTime now) { UtcNow = now; }

        /// <inheritdoc />
        public DateTime UtcNow { get; }
    }

    /// <summary>Builds a fresh EF Core InMemory context backed by a unique store.</summary>
    /// <returns>A new context.</returns>
    public static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-migration-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Returns a Sqid mock that round-trips "SQID-{id}".</summary>
    /// <returns>Configured mock.</returns>
    public static ISqidService NewSqidMock()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        s.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return s;
    }

    /// <summary>Audit mock that captures every event code written.</summary>
    /// <param name="codes">Out parameter — captured codes list.</param>
    /// <returns>Configured mock.</returns>
    public static IAuditService NewAuditCapturing(out List<string> codes)
    {
        var list = new List<string>();
        codes = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => { list.Add(c.ArgAt<string>(0)); return Task.FromResult(Result.Success()); });
        return a;
    }

    /// <summary>Caller-context mock returning sqid USR-1.</summary>
    /// <returns>Configured mock.</returns>
    public static ICallerContext NewCaller()
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(1L);
        c.UserSqid.Returns("USR-1");
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-mig");
        return c;
    }

    /// <summary>Builds a populated <see cref="MigrationPlan"/> entity and persists it.</summary>
    /// <param name="db">Context.</param>
    /// <param name="planCode">Plan code (default LEGACY_PENSIONS_2026).</param>
    /// <param name="status">Initial status (default Active).</param>
    /// <param name="sourceKind">Source kind (default InMemoryTest).</param>
    /// <param name="targetEntityName">Target name (default Pension).</param>
    /// <param name="batchSize">Batch size knob (default 1000).</param>
    /// <returns>Persisted plan entity.</returns>
    public static async Task<MigrationPlan> SeedPlanAsync(
        CnasDbContext db,
        string planCode = "LEGACY_PENSIONS_2026",
        MigrationPlanStatus status = MigrationPlanStatus.Active,
        MigrationSourceKind sourceKind = MigrationSourceKind.InMemoryTest,
        string targetEntityName = "Pension",
        int batchSize = 1000)
    {
        var plan = new MigrationPlan
        {
            PlanCode = planCode,
            Title = "Test plan",
            SourceKind = sourceKind,
            TargetEntityName = targetEntityName,
            BatchSize = batchSize,
            Status = status,
            RegisteredByUserId = 1,
            CreatedAtUtc = ClockNow,
            CreatedBy = "USR-1",
            IsActive = true,
        };
        db.MigrationPlans.Add(plan);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return plan;
    }

    /// <summary>Builds the importer with sensible defaults plus an in-memory source override.</summary>
    /// <param name="db">Context.</param>
    /// <param name="source">In-memory source.</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="peakGate">Optional peak-hour gate; defaults to AllowAll.</param>
    /// <returns>Importer instance.</returns>
    public static MigrationImporter NewImporter(
        CnasDbContext db,
        InMemoryMigrationSource source,
        IAuditService audit,
        Cnas.Ps.Application.Scheduling.IPeakHourGate? peakGate = null)
    {
        var sqids = NewSqidMock();
        var caller = NewCaller();
        var clock = new StubClock(ClockNow);
        var reconciler = new MigrationReconciler(db, clock, sqids, caller, audit, new[] { (IMigrationSource)source });
        return new MigrationImporter(
            db: db,
            clock: clock,
            sqids: sqids,
            caller: caller,
            audit: audit,
            sources: new[] { (IMigrationSource)source },
            mappers: new IMigrationRecordMapper[] { new IdentityMigrationRecordMapper() },
            peakHourGate: peakGate ?? new AllowAllPeakHourGate(),
            reconciler: reconciler,
            logger: NullLogger<MigrationImporter>.Instance);
    }

    /// <summary>Builds the reconciler with sensible defaults.</summary>
    /// <param name="db">Context.</param>
    /// <param name="source">In-memory source.</param>
    /// <param name="audit">Audit service.</param>
    /// <returns>Reconciler instance.</returns>
    public static MigrationReconciler NewReconciler(
        CnasDbContext db,
        InMemoryMigrationSource source,
        IAuditService audit)
    {
        var sqids = NewSqidMock();
        var caller = NewCaller();
        var clock = new StubClock(ClockNow);
        return new MigrationReconciler(db, clock, sqids, caller, audit, new[] { (IMigrationSource)source });
    }

    /// <summary>Builds the plan service with sensible defaults.</summary>
    /// <param name="db">Context.</param>
    /// <param name="audit">Audit service.</param>
    /// <returns>Plan service instance.</returns>
    public static MigrationPlanService NewPlanService(
        CnasDbContext db,
        IAuditService audit)
        => new(
            db: db,
            read: db,
            clock: new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            createValidator: new MigrationPlanCreateInputValidator(),
            modifyValidator: new MigrationPlanModifyInputValidator(),
            reasonValidator: new MigrationPlanReasonInputValidator(),
            filterValidator: new MigrationPlanFilterValidator());

    /// <summary>Builds a small dictionary fixture seeded as a single source record.</summary>
    /// <param name="fingerprint">Source fingerprint.</param>
    /// <param name="extraFields">Additional column kvs.</param>
    /// <returns>Materialised record.</returns>
    public static MigrationSourceRecord NewRecord(
        string fingerprint,
        params (string Key, object? Value)[] extraFields)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in extraFields)
        {
            dict[k] = v;
        }
        return new MigrationSourceRecord(fingerprint, dict);
    }
}
