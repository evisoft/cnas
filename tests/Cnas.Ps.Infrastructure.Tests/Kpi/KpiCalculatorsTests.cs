using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Kpi.Calculators;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Kpi;

/// <summary>
/// R0201 / TOR CF 20.02 — unit tests for the seed KPI calculators. Each
/// calculator is a pure read-only projection over the read-only DbContext;
/// the tests seed an InMemory DB and assert the emitted
/// <see cref="KpiSnapshotEntry"/> list shape and contents.
/// </summary>
public sealed class KpiCalculatorsTests
{
    /// <summary>Snapshot date used by every test as the SI day boundary.</summary>
    private static readonly DateOnly SnapshotDate = new(2026, 5, 22);

    /// <summary>Convenience UTC instant inside <see cref="SnapshotDate"/>.</summary>
    private static readonly DateTime InsideDate = new(2026, 5, 22, 13, 0, 0, DateTimeKind.Utc);

    /// <summary>Convenience UTC instant the day BEFORE <see cref="SnapshotDate"/>.</summary>
    private static readonly DateTime DayBefore = new(2026, 5, 21, 13, 0, 0, DateTimeKind.Utc);

    /// <summary>Convenience UTC instant the day AFTER <see cref="SnapshotDate"/>.</summary>
    private static readonly DateTime DayAfter = new(2026, 5, 23, 13, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── ApplicationsPendingCalculator ───────────────────────

    [Theory]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.UnderExamination)]
    [InlineData(ApplicationStatus.PendingApproval)]
    public async Task ApplicationsPending_CountsRowsInPendingStatuses(ApplicationStatus status)
    {
        await using var db = NewDb();
        SeedApplication(db, status, createdAt: InsideDate.AddDays(-1));
        await db.SaveChangesAsync();

        var calc = new ApplicationsPendingCalculator(db);
        var entries = await calc.ComputeAsync(SnapshotDate);

        entries.Should().ContainSingle()
            .Which.Should().Match<KpiSnapshotEntry>(e =>
                e.KpiCode == "Applications.Pending"
                && e.Value == 1m
                && e.ValueUnit == KpiValueUnits.Count
                && e.Dimension1 == string.Empty
                && e.Dimension2 == string.Empty);
    }

    [Theory]
    [InlineData(ApplicationStatus.Draft)]
    [InlineData(ApplicationStatus.RejectedIncomplete)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Closed)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task ApplicationsPending_IgnoresRowsOutsidePendingStatuses(ApplicationStatus status)
    {
        await using var db = NewDb();
        SeedApplication(db, status, createdAt: InsideDate.AddDays(-1));
        await db.SaveChangesAsync();

        var calc = new ApplicationsPendingCalculator(db);
        var entries = await calc.ComputeAsync(SnapshotDate);

        entries.Should().ContainSingle()
            .Which.Value.Should().Be(0m);
    }

    [Fact]
    public async Task ApplicationsPending_IgnoresSoftDeleted()
    {
        await using var db = NewDb();
        var app = SeedApplication(db, ApplicationStatus.Submitted, createdAt: InsideDate.AddDays(-1));
        app.IsActive = false;
        await db.SaveChangesAsync();

        var calc = new ApplicationsPendingCalculator(db);
        var entries = await calc.ComputeAsync(SnapshotDate);

        entries.Should().ContainSingle().Which.Value.Should().Be(0m);
    }

    // ─────────────────────── ApplicationsClosedYesterdayCalculator ───────────────────────

    [Fact]
    public async Task ApplicationsClosedYesterday_OnlyCountsRowsClosedWithinUtcSnapshotDate()
    {
        await using var db = NewDb();
        // Inside snapshot date — counted.
        var inside = SeedApplication(db, ApplicationStatus.Closed, createdAt: DayBefore);
        inside.UpdatedAtUtc = InsideDate;
        // Day BEFORE — excluded.
        var before = SeedApplication(db, ApplicationStatus.Closed, createdAt: DayBefore);
        before.UpdatedAtUtc = DayBefore;
        // Day AFTER — excluded.
        var after = SeedApplication(db, ApplicationStatus.Closed, createdAt: DayBefore);
        after.UpdatedAtUtc = DayAfter;
        // Different status, inside the window — excluded.
        var wrongStatus = SeedApplication(db, ApplicationStatus.Submitted, createdAt: DayBefore);
        wrongStatus.UpdatedAtUtc = InsideDate;
        await db.SaveChangesAsync();

        var calc = new ApplicationsClosedYesterdayCalculator(db);
        var entries = await calc.ComputeAsync(SnapshotDate);

        entries.Should().ContainSingle()
            .Which.Should().Match<KpiSnapshotEntry>(e =>
                e.KpiCode == "Applications.ClosedYesterday"
                && e.Value == 1m
                && e.ValueUnit == KpiValueUnits.Count);
    }

    // ─────────────────────── TasksOverdueCalculator ───────────────────────

    [Fact]
    public async Task TasksOverdue_ReturnsZeroWhenNoOverdueExist_StillEmitsAnEntry()
    {
        await using var db = NewDb();
        SeedTask(db, WorkflowTaskStatus.Pending);
        SeedTask(db, WorkflowTaskStatus.InProgress);
        await db.SaveChangesAsync();

        var calc = new TasksOverdueCalculator(db);
        var entries = await calc.ComputeAsync(SnapshotDate);

        entries.Should().ContainSingle()
            .Which.Should().Match<KpiSnapshotEntry>(e =>
                e.KpiCode == "Tasks.Overdue"
                && e.Value == 0m
                && e.ValueUnit == KpiValueUnits.Count);
    }

    [Fact]
    public async Task TasksOverdue_CountsOnlyOverdueRows()
    {
        await using var db = NewDb();
        SeedTask(db, WorkflowTaskStatus.Overdue);
        SeedTask(db, WorkflowTaskStatus.Overdue);
        SeedTask(db, WorkflowTaskStatus.Pending);
        SeedTask(db, WorkflowTaskStatus.Completed);
        await db.SaveChangesAsync();

        var calc = new TasksOverdueCalculator(db);
        var entries = await calc.ComputeAsync(SnapshotDate);

        entries.Should().ContainSingle().Which.Value.Should().Be(2m);
    }

    // ─────────────────────── TasksAverageHandlingTimeCalculator ───────────────────────

    [Fact]
    public async Task TasksAvgHandlingHours_ExcludesIncompleteTasks()
    {
        await using var db = NewDb();
        // Completed in the last 7 days — counted: 10h, 20h → avg 15h
        var t1 = SeedTask(db, WorkflowTaskStatus.Completed);
        t1.CreatedAtUtc = InsideDate.AddDays(-1).AddHours(-10);
        t1.CompletedAtUtc = InsideDate.AddDays(-1);
        var t2 = SeedTask(db, WorkflowTaskStatus.Completed);
        t2.CreatedAtUtc = InsideDate.AddDays(-2).AddHours(-20);
        t2.CompletedAtUtc = InsideDate.AddDays(-2);
        // Still-running task with CompletedAtUtc==null — excluded.
        var open = SeedTask(db, WorkflowTaskStatus.InProgress);
        open.CreatedAtUtc = InsideDate.AddDays(-3).AddHours(-100);
        open.CompletedAtUtc = null;
        await db.SaveChangesAsync();

        var calc = new TasksAverageHandlingTimeCalculator(db);
        var entries = await calc.ComputeAsync(SnapshotDate);

        entries.Should().ContainSingle()
            .Which.Should().Match<KpiSnapshotEntry>(e =>
                e.KpiCode == "Tasks.AvgHandlingHours"
                && e.Value == 15m
                && e.ValueUnit == KpiValueUnits.Hours);
    }

    [Fact]
    public async Task TasksAvgHandlingHours_ReturnsZeroWhenNoCompletedTasksInWindow()
    {
        await using var db = NewDb();

        var calc = new TasksAverageHandlingTimeCalculator(db);
        var entries = await calc.ComputeAsync(SnapshotDate);

        entries.Should().ContainSingle().Which.Value.Should().Be(0m);
    }

    // ─────────────────────── NotificationsDeliveredYesterdayCalculator ───────────────────────

    [Fact]
    public async Task NotificationsDelivered_OnlyCountsDeliveredRowsWithinSnapshotDate()
    {
        await using var db = NewDb();
        // Delivered inside the window — counted.
        SeedNotification(db, NotificationDeliveryStatus.Delivered, createdAt: InsideDate);
        // Delivered outside the window — excluded.
        SeedNotification(db, NotificationDeliveryStatus.Delivered, createdAt: DayBefore);
        // Wrong status — excluded.
        SeedNotification(db, NotificationDeliveryStatus.Failed, createdAt: InsideDate);
        SeedNotification(db, NotificationDeliveryStatus.Pending, createdAt: InsideDate);
        SeedNotification(db, NotificationDeliveryStatus.Suppressed, createdAt: InsideDate);
        await db.SaveChangesAsync();

        var calc = new NotificationsDeliveredYesterdayCalculator(db);
        var entries = await calc.ComputeAsync(SnapshotDate);

        entries.Should().ContainSingle()
            .Which.Should().Match<KpiSnapshotEntry>(e =>
                e.KpiCode == "Notifications.DeliveredYesterday"
                && e.Value == 1m
                && e.ValueUnit == KpiValueUnits.Count);
    }

    // ─────────────────────── helpers ───────────────────────

    /// <summary>Builds a fresh InMemory CnasDbContext per test.</summary>
    private static CnasDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-kpi-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Seeds a <see cref="ServiceApplication"/> row with the supplied status.</summary>
    private static ServiceApplication SeedApplication(
        ICnasDbContext db, ApplicationStatus status, DateTime createdAt)
    {
        var app = new ServiceApplication
        {
            CreatedAtUtc = createdAt,
            SolicitantId = 1L,
            ServicePassportId = 1L,
            Status = status,
            FormPayloadJson = "{}",
            IsActive = true,
        };
        db.Applications.Add(app);
        return app;
    }

    /// <summary>Seeds a <see cref="WorkflowTask"/> row with the supplied status.</summary>
    private static WorkflowTask SeedTask(ICnasDbContext db, WorkflowTaskStatus status)
    {
        var task = new WorkflowTask
        {
            CreatedAtUtc = InsideDate.AddDays(-1),
            DossierId = 1L,
            Title = $"task-{Guid.NewGuid():N}",
            Status = status,
            IsActive = true,
        };
        db.WorkflowTasks.Add(task);
        return task;
    }

    /// <summary>Seeds a <see cref="Notification"/> row with the supplied delivery status.</summary>
    private static Notification SeedNotification(
        ICnasDbContext db, NotificationDeliveryStatus status, DateTime createdAt)
    {
        var n = new Notification
        {
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
            RecipientUserId = 1L,
            Channel = NotificationChannel.InApp,
            DeliveryStatus = status,
            Subject = "s",
            Body = "b",
            IsActive = true,
        };
        db.Notifications.Add(n);
        return n;
    }
}
