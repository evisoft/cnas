using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Workflows;

/// <summary>
/// R0125 / CF 16.09 — service-level tests for the workflow-task step history projection.
/// Backed by an EF Core InMemory store so the write→read round trip is deterministic.
/// </summary>
public sealed class WorkflowTaskHistoryServiceTests
{
    private static readonly DateTime BaseUtc = new(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-wfhist-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (WorkflowTaskHistoryService Svc, CnasDbContext Db) Build(CnasDbContext db)
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
        {
            var s = call.Arg<string?>();
            if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s.AsSpan(5), out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad");
        });

        var clock = Substitute.For<ICnasTimeProvider>();
        var nextOffset = 0;
        clock.UtcNow.Returns(_ => BaseUtc.AddSeconds(Interlocked.Increment(ref nextOffset)));

        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-99");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-1");

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var validator = new WorkflowTaskHistoryFilterDtoValidator();

        var svc = new WorkflowTaskHistoryService(
            db, sqids, clock, caller, audit, validator,
            NullLogger<WorkflowTaskHistoryService>.Instance);
        return (svc, db);
    }

    [Fact]
    public async Task RecordEventAsync_Entered_Exited_Reassigned_AllPersistWithExpectedFields()
    {
        using var db = CreateContext();
        var (svc, _) = Build(db);

        var entered = await svc.RecordEventAsync(
            workflowTaskId: 100,
            eventKind: WorkflowTaskStepEventKind.Entered,
            stepCode: "intake",
            actorUserId: 7,
            decisionCode: null,
            note: "Task created");
        var exited = await svc.RecordEventAsync(
            workflowTaskId: 100,
            eventKind: WorkflowTaskStepEventKind.Exited,
            stepCode: "intake",
            actorUserId: 7,
            decisionCode: "APPROVE",
            note: null);
        var reassigned = await svc.RecordEventAsync(
            workflowTaskId: 100,
            eventKind: WorkflowTaskStepEventKind.Reassigned,
            stepCode: "examination",
            actorUserId: 8,
            decisionCode: null,
            note: "Out on leave");

        entered.IsSuccess.Should().BeTrue();
        exited.IsSuccess.Should().BeTrue();
        reassigned.IsSuccess.Should().BeTrue();

        var rows = await db.WorkflowTaskStepHistories.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(3);
        rows.Should().ContainSingle(r => r.EventKind == WorkflowTaskStepEventKind.Entered
            && r.StepCode == "intake" && r.ActorUserId == 7);
        rows.Should().ContainSingle(r => r.EventKind == WorkflowTaskStepEventKind.Exited
            && r.DecisionCode == "APPROVE");
        rows.Should().ContainSingle(r => r.EventKind == WorkflowTaskStepEventKind.Reassigned
            && r.StepCode == "examination" && r.ActorUserId == 8);
    }

    [Fact]
    public async Task GetHistoryAsync_OrdersChronologicallyAscending()
    {
        using var db = CreateContext();
        var (svc, _) = Build(db);

        // Recorded in non-chronological order to verify the query sorts ascending.
        await svc.RecordEventAsync(100, WorkflowTaskStepEventKind.Entered, "intake", 1, null, null);
        await svc.RecordEventAsync(100, WorkflowTaskStepEventKind.Exited, "intake", 1, "APPROVE", null);
        await svc.RecordEventAsync(100, WorkflowTaskStepEventKind.Completed, "approval", 2, null, null);

        var result = await svc.GetHistoryAsync("SQID-100", new WorkflowTaskHistoryFilterDto());

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(3);
        result.Value.Items.Should().HaveCount(3);
        // Ascending by OccurredAt — clock substitute advances on each call.
        result.Value.Items[0].EventKind.Should().Be("Entered");
        result.Value.Items[1].EventKind.Should().Be("Exited");
        result.Value.Items[2].EventKind.Should().Be("Completed");
    }

    [Fact]
    public async Task GetHistoryAsync_WithEventKindFilter_OnlyMatchingRowsReturned()
    {
        using var db = CreateContext();
        var (svc, _) = Build(db);

        await svc.RecordEventAsync(100, WorkflowTaskStepEventKind.Entered, "intake", 1, null, null);
        await svc.RecordEventAsync(100, WorkflowTaskStepEventKind.Reassigned, "intake", 2, null, null);
        await svc.RecordEventAsync(100, WorkflowTaskStepEventKind.Reassigned, "intake", 3, null, null);
        await svc.RecordEventAsync(100, WorkflowTaskStepEventKind.Completed, "intake", 3, null, null);

        var result = await svc.GetHistoryAsync(
            "SQID-100",
            new WorkflowTaskHistoryFilterDto(EventKind: "Reassigned"));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Should().OnlyContain(i => i.EventKind == "Reassigned");
    }
}
