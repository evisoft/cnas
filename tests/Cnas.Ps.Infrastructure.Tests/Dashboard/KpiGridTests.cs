using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Dashboard;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Dashboard;

/// <summary>
/// R0533 / TOR CF 04.04 — unit tests for the aggregate KPI grid producers.
/// Each producer emits a single <see cref="KpiGridCellDto"/> with a stable code
/// and a deep-link URL (R0534). Tests pin: each producer's happy-path counter,
/// the cell carries a non-empty deep-link, and the empty-data case returns zero.
/// </summary>
public sealed class KpiGridTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-kpi-grid-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    [Fact]
    public async Task UnreadNotifications_CountsOnlyUnreadActiveForCaller()
    {
        const long callerId = 7L;
        await using var db = CreateContext();
        db.Notifications.AddRange(
            new Notification
            {
                RecipientUserId = callerId,
                Channel = NotificationChannel.InApp,
                Subject = "subj-1",
                Body = "body-1",
                DeliveryStatus = NotificationDeliveryStatus.Delivered,
                CreatedAtUtc = ClockNow.AddMinutes(-10),
                IsActive = true,
            },
            new Notification
            {
                RecipientUserId = callerId,
                Channel = NotificationChannel.InApp,
                Subject = "subj-2",
                Body = "body-2",
                DeliveryStatus = NotificationDeliveryStatus.Delivered,
                ReadAtUtc = ClockNow.AddMinutes(-5),
                CreatedAtUtc = ClockNow.AddMinutes(-10),
                IsActive = true,
            });
        await db.SaveChangesAsync();
        IReadOnlyCnasDbContext read = db;
        var producer = new UnreadNotificationsKpiGridProducer(read);

        var result = await producer.ProduceAsync(callerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var cell = result.Value!.Single();
        cell.Code.Should().Be(UnreadNotificationsKpiGridProducer.CellCode);
        cell.Value.Should().Be(1m);
        cell.DeepLinkUrl.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DocsPendingApproval_CountsPendingActionsExcludingMaker()
    {
        const long callerId = 7L;
        await using var db = CreateContext();
        db.PendingAdminActions.AddRange(
            new PendingAdminAction
            {
                Operation = "DEMO.NOOP",
                PayloadJson = "{}",
                MakerUserId = 99L,
                MakerRequestedAtUtc = ClockNow.AddMinutes(-15),
                ExpiresAtUtc = ClockNow.AddHours(24),
                Status = PendingAdminActionStatus.Pending,
                CreatedAtUtc = ClockNow.AddMinutes(-15),
                IsActive = true,
            },
            new PendingAdminAction
            {
                Operation = "DEMO.NOOP",
                PayloadJson = "{}",
                MakerUserId = callerId,
                MakerRequestedAtUtc = ClockNow.AddMinutes(-10),
                ExpiresAtUtc = ClockNow.AddHours(24),
                Status = PendingAdminActionStatus.Pending,
                CreatedAtUtc = ClockNow.AddMinutes(-10),
                IsActive = true,
            });
        await db.SaveChangesAsync();
        IReadOnlyCnasDbContext read = db;
        var producer = new DocsPendingApprovalKpiGridProducer(read);

        var result = await producer.ProduceAsync(callerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var cell = result.Value!.Single();
        cell.Code.Should().Be(DocsPendingApprovalKpiGridProducer.CellCode);
        cell.Value.Should().Be(1m);
        cell.DeepLinkUrl.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ApplicationsByStatus_CountsSubmittedApplications()
    {
        await using var db = CreateContext();
        db.Applications.AddRange(
            new ServiceApplication
            {
                ServicePassportId = 1L,
                SolicitantId = 1L,
                ReferenceNumber = "APP-1",
                Status = ApplicationStatus.Submitted,
                CreatedAtUtc = ClockNow.AddMinutes(-20),
                IsActive = true,
            },
            new ServiceApplication
            {
                ServicePassportId = 1L,
                SolicitantId = 1L,
                ReferenceNumber = "APP-2",
                Status = ApplicationStatus.Draft,
                CreatedAtUtc = ClockNow.AddMinutes(-15),
                IsActive = true,
            });
        await db.SaveChangesAsync();
        IReadOnlyCnasDbContext read = db;
        var producer = new ApplicationsByStatusKpiGridProducer(read);

        var result = await producer.ProduceAsync(userId: 1L, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var cell = result.Value!.Single();
        cell.Code.Should().Be(ApplicationsByStatusKpiGridProducer.CellCode);
        cell.Value.Should().Be(1m);
        cell.DeepLinkUrl.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task OverdueTasks_CountsAssignedTasksPastDeadline()
    {
        const long callerId = 7L;
        await using var db = CreateContext();
        db.WorkflowTasks.AddRange(
            new WorkflowTask
            {
                Title = "Overdue task",
                AssignedUserId = callerId,
                Status = WorkflowTaskStatus.InProgress,
                DueAtUtc = ClockNow.AddHours(-2),
                CreatedAtUtc = ClockNow.AddDays(-1),
                IsActive = true,
            },
            new WorkflowTask
            {
                Title = "Future task",
                AssignedUserId = callerId,
                Status = WorkflowTaskStatus.InProgress,
                DueAtUtc = ClockNow.AddHours(2),
                CreatedAtUtc = ClockNow.AddDays(-1),
                IsActive = true,
            });
        await db.SaveChangesAsync();
        IReadOnlyCnasDbContext read = db;
        var producer = new OverdueTasksKpiGridProducer(read, new StubClock());

        var result = await producer.ProduceAsync(callerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var cell = result.Value!.Single();
        cell.Code.Should().Be(OverdueTasksKpiGridProducer.CellCode);
        cell.Value.Should().Be(1m);
        cell.DeepLinkUrl.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Composing every producer with empty data yields zero-value cells across the
    /// whole grid (no NPE / no missing rows).
    /// </summary>
    [Fact]
    public async Task EveryGridProducer_OnEmptyData_ReturnsZeroValueCell()
    {
        await using var db = CreateContext();
        IReadOnlyCnasDbContext read = db;

        var producers = new IKpiGridProducer[]
        {
            new UnreadNotificationsKpiGridProducer(read),
            new DocsPendingApprovalKpiGridProducer(read),
            new ApplicationsByStatusKpiGridProducer(read),
            new OverdueTasksKpiGridProducer(read, new StubClock()),
        };

        foreach (var producer in producers)
        {
            var result = await producer.ProduceAsync(userId: 1L, CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            result.Value!.Single().Value.Should().Be(0m);
        }
    }
}
