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
/// R0531 / CF 04.02 — unit tests for <see cref="WorkflowUpdatesTileProducer"/>.
/// Pins three behaviours that the iter-115 acceptance gates depend on:
/// <list type="bullet">
///   <item>The category surfaced on the emitted widget is exactly
///         <see cref="DashboardCategory.WorkflowUpdates"/>.</item>
///   <item>Only step-history rows whose actor is NOT the caller are counted.</item>
///   <item>The rolling 24-hour window excludes older rows.</item>
/// </list>
/// </summary>
public sealed class WorkflowUpdatesTileProducerTests
{
    /// <summary>Deterministic UTC clock — pinned to keep windows reproducible.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ProduceAsync_CallerActed_DoesNotCountOwnEvents()
    {
        const long callerId = 7L;
        var harness = Harness.Create();

        // Seed a task assigned to the caller and a history row authored BY the caller.
        var task = SeedTask(harness.Db, assignedUserId: callerId);
        harness.Db.WorkflowTaskStepHistories.Add(new WorkflowTaskStepHistory
        {
            WorkflowTaskId = task.Id,
            StepCode = "review",
            EventKind = WorkflowTaskStepEventKind.Entered,
            OccurredAt = ClockNow.AddHours(-1),
            ActorUserId = callerId,
            CreatedAtUtc = ClockNow.AddHours(-1),
            IsActive = true,
        });
        await harness.Db.SaveChangesAsync();

        var result = await harness.Producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Single().Value.Should().Be(0m,
            "actions performed BY the caller are not workflow updates ABOUT the caller");
    }

    [Fact]
    public async Task ProduceAsync_SystemEvent_CountsTowardWindow()
    {
        const long callerId = 7L;
        var harness = Harness.Create();
        var task = SeedTask(harness.Db, assignedUserId: callerId);

        // System event (ActorUserId == null) inside the 24h window — must count.
        harness.Db.WorkflowTaskStepHistories.Add(new WorkflowTaskStepHistory
        {
            WorkflowTaskId = task.Id,
            StepCode = "review",
            EventKind = WorkflowTaskStepEventKind.SlaBreached,
            OccurredAt = ClockNow.AddHours(-2),
            ActorUserId = null,
            CreatedAtUtc = ClockNow.AddHours(-2),
            IsActive = true,
        });
        // Sibling user moved one of the caller's tasks — must count.
        harness.Db.WorkflowTaskStepHistories.Add(new WorkflowTaskStepHistory
        {
            WorkflowTaskId = task.Id,
            StepCode = "review",
            EventKind = WorkflowTaskStepEventKind.Reassigned,
            OccurredAt = ClockNow.AddHours(-3),
            ActorUserId = 999L,
            CreatedAtUtc = ClockNow.AddHours(-3),
            IsActive = true,
        });
        // Older than 24h — must drop out.
        harness.Db.WorkflowTaskStepHistories.Add(new WorkflowTaskStepHistory
        {
            WorkflowTaskId = task.Id,
            StepCode = "review",
            EventKind = WorkflowTaskStepEventKind.Entered,
            OccurredAt = ClockNow.AddHours(-48),
            ActorUserId = 999L,
            CreatedAtUtc = ClockNow.AddHours(-48),
            IsActive = true,
        });
        await harness.Db.SaveChangesAsync();

        var result = await harness.Producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        var widget = result.Value!.Single();
        widget.Value.Should().Be(2m,
            "two qualifying rows are inside the 24h window; the 48h-old row is excluded");
        widget.Code.Should().Be("WORKFLOW_UPDATES_LAST24H");
        widget.Category.Should().Be(nameof(DashboardCategory.WorkflowUpdates));
    }

    [Fact]
    public async Task ProduceAsync_NoHistoryRows_ReturnsZeroValueWidget()
    {
        const long callerId = 7L;
        var harness = Harness.Create();
        // No history rows seeded — producer must NOT throw; instead emit a zero widget.

        var result = await harness.Producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().ContainSingle();
        result.Value.Single().Value.Should().Be(0m);
    }

    // ─────────── helpers ───────────

    private static WorkflowTask SeedTask(CnasDbContext db, long? assignedUserId)
    {
        var task = new WorkflowTask
        {
            DossierId = 1L,
            Title = "Review",
            Status = WorkflowTaskStatus.InProgress,
            AssignedUserId = assignedUserId,
            CreatedAtUtc = ClockNow.AddDays(-2),
            IsActive = true,
        };
        db.WorkflowTasks.Add(task);
        db.SaveChanges();
        return task;
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required WorkflowUpdatesTileProducer Producer { get; init; }

        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-wfupd-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            IReadOnlyCnasDbContext readDb = db;
            var producer = new WorkflowUpdatesTileProducer(readDb, new StubClock(ClockNow));
            return new Harness { Db = db, Producer = producer };
        }
    }
}
