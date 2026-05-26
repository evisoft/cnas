using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0537 / CF 04.10 — unit tests for <see cref="AdminDashboardService"/>.
/// Pins four invariants:
/// <list type="bullet">
///   <item>KPIs are pulled verbatim from <c>IKpiSnapshotService.GetLatestAsync</c>.</item>
///   <item>OpenAdminActionsCount counts only pending + active rows.</item>
///   <item>AuditSummary groups by severity over the rolling 24-hour window.</item>
///   <item>RecentAlerts is empty when no <c>SECURITY_ALERT.FIRED</c> audit rows exist.</item>
/// </list>
/// </summary>
public sealed class AdminDashboardServiceTests
{
    /// <summary>Deterministic UTC clock instant — pinned at the centre of the 24h window.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetSnapshot_ReturnsKpisFromSnapshotService()
    {
        var harness = Harness.Create();
        harness.Kpis.GetLatestAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                ["Applications.Pending"] = 42m,
                ["Tasks.Overdue"] = 7m,
            });

        var result = await harness.Service.GetSnapshotAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.Kpis.Should().ContainKey("Applications.Pending");
        result.Value.Kpis["Applications.Pending"].Should().Be(42m);
        result.Value.Kpis["Tasks.Overdue"].Should().Be(7m);
    }

    [Fact]
    public async Task GetSnapshot_CountsOpenPendingAdminActions()
    {
        var harness = Harness.Create();

        // Seed: 2 pending + active, 1 pending + inactive, 1 approved + active.
        harness.Db.PendingAdminActions.AddRange(
            BuildAction(PendingAdminActionStatus.Pending, active: true),
            BuildAction(PendingAdminActionStatus.Pending, active: true),
            BuildAction(PendingAdminActionStatus.Pending, active: false),
            BuildAction(PendingAdminActionStatus.Approved, active: true));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.GetSnapshotAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.OpenAdminActionsCount.Should().Be(2,
            "only Pending AND IsActive=true rows count as the open backlog.");
    }

    [Fact]
    public async Task GetSnapshot_AuditSummary_GroupedBySeverity()
    {
        var harness = Harness.Create();
        // Seed 3 Information + 1 Critical inside the 24h window, plus 1 Notice outside.
        harness.Db.AuditLogs.AddRange(
            BuildAuditRow(AuditSeverity.Information, ClockNow.AddHours(-1)),
            BuildAuditRow(AuditSeverity.Information, ClockNow.AddHours(-5)),
            BuildAuditRow(AuditSeverity.Information, ClockNow.AddHours(-12)),
            BuildAuditRow(AuditSeverity.Critical, ClockNow.AddHours(-2)),
            BuildAuditRow(AuditSeverity.Notice, ClockNow.AddHours(-25)));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.GetSnapshotAsync();

        result.IsSuccess.Should().BeTrue();
        var summary = result.Value!.AuditSummary;
        summary.Should().Contain(s => s.Severity == nameof(AuditSeverity.Information) && s.Count == 3);
        summary.Should().Contain(s => s.Severity == nameof(AuditSeverity.Critical) && s.Count == 1);
        summary.Should().NotContain(s => s.Severity == nameof(AuditSeverity.Notice),
            "the rolling 24h window must exclude the older Notice row.");
    }

    [Fact]
    public async Task GetSnapshot_NoSecurityAlerts_ReturnsEmptyList()
    {
        var harness = Harness.Create();

        var result = await harness.Service.GetSnapshotAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.RecentAlerts.Should().BeEmpty(
            "missing SECURITY_ALERT.FIRED audit rows must return an empty list, never throw.");
    }

    [Fact]
    public async Task GetSnapshot_SecurityAlert_PopulatesRuleCodeFromDetailsJson()
    {
        // Arrange — seed one SECURITY_ALERT.FIRED row with the canonical detailsJson shape.
        var harness = Harness.Create();
        harness.Db.AuditLogs.Add(new AuditLog
        {
            EventCode = "SECURITY_ALERT.FIRED",
            ActorId = "system:r0189-evaluator",
            EventAtUtc = ClockNow.AddMinutes(-30),
            CreatedAtUtc = ClockNow.AddMinutes(-30),
            Severity = AuditSeverity.Notice,
            TargetEntity = nameof(SecurityAlertRule),
            TargetEntityId = 1,
            DetailsJson = JsonSerializer.Serialize(new { ruleCode = "FAILED_LOGIN_BURST", matchCount = 12 }),
            PrevHash = "GENESIS",
            RowHash = new string('0', 64),
            IsActive = true,
        });
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.GetSnapshotAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.RecentAlerts.Should().ContainSingle();
        result.Value.RecentAlerts[0].RuleCode.Should().Be("FAILED_LOGIN_BURST");
        result.Value.RecentAlerts[0].Summary.Should().Contain("FAILED_LOGIN_BURST");
    }

    // ─────────────────────── helpers ───────────────────────

    private static PendingAdminAction BuildAction(PendingAdminActionStatus status, bool active) => new()
    {
        Operation = "DEMO.NOOP",
        PayloadJson = "{}",
        MakerUserId = 1L,
        MakerRequestedAtUtc = ClockNow.AddHours(-1),
        Status = status,
        ExpiresAtUtc = ClockNow.AddHours(23),
        IsActive = active,
        CreatedAtUtc = ClockNow.AddHours(-1),
    };

    private static AuditLog BuildAuditRow(AuditSeverity severity, DateTime when) => new()
    {
        EventCode = $"TEST.AUDIT.{severity}",
        ActorId = "test",
        EventAtUtc = when,
        CreatedAtUtc = when,
        Severity = severity,
        DetailsJson = "{}",
        PrevHash = "GENESIS",
        RowHash = new string('0', 64),
        IsActive = true,
    };

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-dash-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required AdminDashboardService Service { get; init; }
        public required IKpiSnapshotService Kpis { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            // The CnasDbContext implements BOTH ICnasDbContext + IReadOnlyCnasDbContext
            // (see CnasDbContext class declaration), so the same instance backs the
            // service's read path in tests; in production DI binds two distinct
            // DbContextOptions but the InMemory provider has no replica concept.
            IReadOnlyCnasDbContext readDb = db;

            var kpis = Substitute.For<IKpiSnapshotService>();
            kpis.GetLatestAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, decimal>(StringComparer.Ordinal));

            var sqids = new SqidService(Options.Create(new SqidOptions
            {
                Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                MinLength = 6,
            }));
            var clock = new StubClock(ClockNow);
            var service = new AdminDashboardService(
                readDb, kpis, sqids, clock, NullLogger<AdminDashboardService>.Instance);

            return new Harness { Db = db, Service = service, Kpis = kpis };
        }
    }
}
