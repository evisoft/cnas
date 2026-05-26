using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Dashboard;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Dashboard;

/// <summary>
/// R0531 / CF 04.02 — unit tests for <see cref="AwaitingApprovalTileProducer"/>.
/// Pins:
/// <list type="bullet">
///   <item>Only <see cref="PendingAdminActionStatus.Pending"/> AND active rows count.</item>
///   <item>Rows the caller submitted themselves (maker == caller) are excluded
///         so the dashboard doesn't show a self-approval queue (4-eyes contract).</item>
///   <item>The producer declares the decider/admin role allow-list.</item>
/// </list>
/// </summary>
public sealed class AwaitingApprovalTileProducerTests
{
    /// <summary>Deterministic UTC clock — pinned for reproducible counts.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Expected approver allow-list — pre-allocated to satisfy CA1861.</summary>
    private static readonly string[] ExpectedApproverRoles =
        ["cnas-decider", "cnas-admin", "seful-directiei", "seful-cnas"];

    [Fact]
    public async Task ProduceAsync_OnlyCountsPendingAndActiveRows()
    {
        const long callerId = 5L;
        var harness = Harness.Create();

        // 2 pending + active by another maker → counted.
        harness.Db.PendingAdminActions.AddRange(
            BuildAction(maker: 99L, status: PendingAdminActionStatus.Pending, active: true),
            BuildAction(maker: 99L, status: PendingAdminActionStatus.Pending, active: true),
            // Pending but inactive (soft-deleted) → excluded.
            BuildAction(maker: 99L, status: PendingAdminActionStatus.Pending, active: false),
            // Already approved → excluded.
            BuildAction(maker: 99L, status: PendingAdminActionStatus.Approved, active: true));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        var widget = result.Value!.Single();
        widget.Code.Should().Be("APPROVAL_QUEUE");
        widget.Value.Should().Be(2m);
        widget.Category.Should().Be(nameof(DashboardCategory.ItemsAwaitingApproval));
    }

    [Fact]
    public async Task ProduceAsync_ExcludesActionsSubmittedByCaller()
    {
        const long callerId = 5L;
        var harness = Harness.Create();

        // Maker == caller → the 4-eyes contract forbids self-approval, so this row
        // MUST NOT appear in the caller's approval queue tile.
        harness.Db.PendingAdminActions.Add(BuildAction(
            maker: callerId,
            status: PendingAdminActionStatus.Pending,
            active: true));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Single().Value.Should().Be(0m,
            "the maker MUST NOT see their own pending requests in their approval tile");
    }

    [Fact]
    public void Producer_DeclaresApproverRoleAllowList()
    {
        var harness = Harness.Create();

        harness.Producer.Category.Should().Be(DashboardCategory.ItemsAwaitingApproval);
        harness.Producer.SupportedRoles.Should().Contain(ExpectedApproverRoles,
            "approval is by design a decider / admin activity");
        harness.Producer.SupportedRoles.Should().NotContain("*",
            "approval tile MUST NOT be wildcard-visible — that would leak the backlog to citizens");
    }

    // ─────────── helpers ───────────

    private static PendingAdminAction BuildAction(long maker, PendingAdminActionStatus status, bool active) => new()
    {
        Operation = "DEMO.NOOP",
        PayloadJson = "{}",
        MakerUserId = maker,
        MakerRequestedAtUtc = ClockNow.AddHours(-1),
        Status = status,
        ExpiresAtUtc = ClockNow.AddHours(23),
        IsActive = active,
        CreatedAtUtc = ClockNow.AddHours(-1),
    };

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required AwaitingApprovalTileProducer Producer { get; init; }

        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-approval-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            IReadOnlyCnasDbContext readDb = db;
            var producer = new AwaitingApprovalTileProducer(readDb, new StubClock(ClockNow));
            return new Harness { Db = db, Producer = producer };
        }
    }
}
