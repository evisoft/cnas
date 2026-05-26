using System;
using System.Collections.Generic;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — shared test helpers for the backup-orchestration
/// test suite. Mirrors the patterns established by the migration test
/// helpers so reviewers can spot-check by analogy.
/// </summary>
internal static class BackupTestHelpers
{
    /// <summary>Canonical "now" used across the backup tests.</summary>
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
            .UseInMemoryDatabase($"cnas-backups-{Guid.NewGuid():N}")
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
        c.CorrelationId.Returns("corr-bkp");
        return c;
    }

    /// <summary>Builds and persists a populated <see cref="BackupPolicy"/> entity.</summary>
    /// <param name="db">Context.</param>
    /// <param name="policyCode">Policy code (default DB_FULL).</param>
    /// <param name="isActive">IsActive flag (default true).</param>
    /// <param name="isArchived">Archive flag (default false).</param>
    /// <param name="scope">Scope (default PrimaryDatabase).</param>
    /// <param name="strategy">Strategy (default Full).</param>
    /// <param name="targetKind">Target kind (default InMemoryTest).</param>
    /// <param name="retentionDays">Retention days (default 30).</param>
    /// <returns>Persisted policy entity.</returns>
    public static async Task<BackupPolicy> SeedPolicyAsync(
        CnasDbContext db,
        string policyCode = "DB_FULL",
        bool isActive = true,
        bool isArchived = false,
        BackupScope scope = BackupScope.PrimaryDatabase,
        BackupStrategy strategy = BackupStrategy.Full,
        BackupTargetKind targetKind = BackupTargetKind.InMemoryTest,
        int retentionDays = 30)
    {
        var policy = new BackupPolicy
        {
            PolicyCode = policyCode,
            DisplayName = $"Test policy {policyCode}",
            Description = null,
            Scope = scope,
            Strategy = strategy,
            CronSchedule = "0 0 2 * * ?",
            RetentionDays = retentionDays,
            TargetKind = targetKind,
            TargetReference = "test-bucket",
            RegisteredByUserId = 1,
            IsActive = isActive,
            IsArchived = isArchived,
            CreatedAtUtc = ClockNow,
            CreatedBy = "USR-1",
        };
        db.BackupPolicies.Add(policy);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return policy;
    }

    /// <summary>Builds the policy service with sensible defaults.</summary>
    /// <param name="db">Context.</param>
    /// <param name="audit">Audit service.</param>
    /// <returns>Service instance.</returns>
    public static BackupPolicyService NewPolicyService(CnasDbContext db, IAuditService audit)
        => new(
            db: db,
            read: db,
            clock: new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            createValidator: new BackupPolicyCreateInputValidator(),
            modifyValidator: new BackupPolicyModifyInputValidator(),
            reasonValidator: new BackupPolicyReasonInputValidator(),
            filterValidator: new BackupPolicyFilterValidator());

    /// <summary>Builds the orchestrator with sensible defaults.</summary>
    /// <param name="db">Context.</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="targets">Targets registered with the orchestrator.</param>
    /// <param name="providers">Optional providers; defaults to the four in-memory providers.</param>
    /// <param name="clock">Optional clock override (defaults to <see cref="ClockNow"/>).</param>
    /// <returns>Orchestrator instance.</returns>
    public static BackupOrchestrator NewOrchestrator(
        CnasDbContext db,
        IAuditService audit,
        IEnumerable<IBackupTarget> targets,
        IEnumerable<IBackupPayloadProvider>? providers = null,
        ICnasTimeProvider? clock = null)
        => new(
            db: db,
            read: db,
            clock: clock ?? new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            providers: providers ?? DefaultProviders(),
            targets: targets,
            filterValidator: new BackupRunFilterValidator(),
            logger: NullLogger<BackupOrchestrator>.Instance);

    /// <summary>Returns the canonical set of in-memory providers (one per scope).</summary>
    /// <returns>Enumerable of providers.</returns>
    public static IEnumerable<IBackupPayloadProvider> DefaultProviders()
        => new IBackupPayloadProvider[]
        {
            new InMemoryBackupPayloadProvider(BackupScope.PrimaryDatabase),
            new InMemoryBackupPayloadProvider(BackupScope.FileStorage),
            new InMemoryBackupPayloadProvider(BackupScope.Logs),
            new InMemoryBackupPayloadProvider(BackupScope.EncryptionKeys),
        };
}
