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
/// R0531 / CF 04.02 — unit tests for <see cref="InvolvementTileProducer"/>. Pins:
/// <list type="bullet">
///   <item>The emitted widget is tagged with the canonical
///         <see cref="DashboardCategory.ItemsRequiringInvolvement"/> name.</item>
///   <item>Only tasks the caller is currently holding (Status = InProgress) count.</item>
///   <item>Inactive tasks (soft-deleted) are excluded.</item>
/// </list>
/// </summary>
public sealed class InvolvementTileProducerTests
{
    /// <summary>Deterministic UTC clock — pinned for reproducible counts.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ProduceAsync_OnlyCountsInProgressTasksAssignedToCaller()
    {
        const long callerId = 11L;
        var harness = Harness.Create();

        harness.Db.WorkflowTasks.AddRange(
            BuildTask(assignedUserId: callerId, status: WorkflowTaskStatus.InProgress, active: true),
            BuildTask(assignedUserId: callerId, status: WorkflowTaskStatus.InProgress, active: true),
            // Pending — counted by TaskArrivals, not Involvement.
            BuildTask(assignedUserId: callerId, status: WorkflowTaskStatus.Pending, active: true),
            // Completed — not actionable any more.
            BuildTask(assignedUserId: callerId, status: WorkflowTaskStatus.Completed, active: true),
            // Different user — never counts.
            BuildTask(assignedUserId: 99L, status: WorkflowTaskStatus.InProgress, active: true));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        var widget = result.Value!.Single();
        widget.Code.Should().Be("INVOLVEMENT_ITEMS");
        widget.Value.Should().Be(2m, "two InProgress tasks belong to the caller");
        widget.Category.Should().Be(nameof(DashboardCategory.ItemsRequiringInvolvement));
    }

    [Fact]
    public async Task ProduceAsync_InactiveTasks_AreExcluded()
    {
        const long callerId = 11L;
        var harness = Harness.Create();

        harness.Db.WorkflowTasks.Add(BuildTask(
            assignedUserId: callerId,
            status: WorkflowTaskStatus.InProgress,
            active: false));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Single().Value.Should().Be(0m,
            "soft-deleted (IsActive=false) tasks MUST NOT contribute to the involvement count");
    }

    [Fact]
    public async Task ProduceAsync_NoTasks_ReturnsZeroValueWidget()
    {
        const long callerId = 11L;
        var harness = Harness.Create();

        var result = await harness.Producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        var widget = result.Value!.Single();
        widget.Value.Should().Be(0m);
        widget.Category.Should().Be(nameof(DashboardCategory.ItemsRequiringInvolvement));
    }

    [Fact]
    public void Producer_DeclaresCategoryAndWildcardRoles()
    {
        var harness = Harness.Create();

        harness.Producer.Category.Should().Be(DashboardCategory.ItemsRequiringInvolvement);
        harness.Producer.SupportedRoles.Should().Contain("*",
            "involvement applies to every authenticated caller (CF 04.02)");
    }

    // ─────────── helpers ───────────

    private static WorkflowTask BuildTask(long? assignedUserId, WorkflowTaskStatus status, bool active) => new()
    {
        DossierId = 1L,
        Title = "Step",
        Status = status,
        AssignedUserId = assignedUserId,
        CreatedAtUtc = ClockNow.AddDays(-1),
        IsActive = active,
    };

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required InvolvementTileProducer Producer { get; init; }

        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-involv-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            IReadOnlyCnasDbContext readDb = db;
            var producer = new InvolvementTileProducer(readDb, new StubClock(ClockNow));
            return new Harness { Db = db, Producer = producer };
        }
    }
}
