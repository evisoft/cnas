using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Application.BulkActions.Operations;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0527 / TOR CF 03.11 — tests for the two NEW <see cref="IBulkOperation"/>
/// implementations shipped in this batch
/// (<see cref="CerereChangeStatusBulkOperation"/> and
/// <see cref="WorkflowTaskMarkCompleteBulkOperation"/>).
/// </summary>
public sealed class NewBulkOperationsTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Fresh InMemory <see cref="CnasDbContext"/> with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-newbulkops-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Builds a deterministic clock anchored at <see cref="ClockNow"/>.</summary>
    private static ICnasTimeProvider BuildClock()
    {
        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(ClockNow);
        return clock;
    }

    /// <summary>Builds an audit-service stub that returns <see cref="Result.Success"/> for every call.</summary>
    private static IAuditService BuildAuditCapturing(List<string> sink)
    {
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Do<string>(c => sink.Add(c)),
                Arg.Any<AuditSeverity>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<long?>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        return audit;
    }

    /// <summary>Builds a stub <see cref="ICallerContext"/> for actor attribution.</summary>
    private static ICallerContext BuildCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-CALLER");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-test");
        return caller;
    }

    // ─────────────────────── CerereChangeStatusBulkOperation ───────────────────────

    [Fact]
    public async Task CerereChangeStatus_RejectsInvalidTransition_FromClosed()
    {
        await using var db = CreateContext();
        db.Applications.Add(new ServiceApplication
        {
            Id = 7,
            SolicitantId = 1,
            ServicePassportId = 1,
            Status = ApplicationStatus.Closed,
            CreatedAtUtc = ClockNow,
            IsActive = true,
            FormPayloadJson = "{}",
        });
        await db.SaveChangesAsync();

        var audit = BuildAuditCapturing(new List<string>());
        var op = new CerereChangeStatusBulkOperation(db, BuildClock(), audit);
        var parameters = JsonSerializer.Serialize(new { newStatus = "Submitted" });

        var outcome = await op.ExecuteAsync(7, parameters, BuildCaller(), CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("INVALID_TRANSITION");
    }

    [Fact]
    public async Task CerereChangeStatus_HappyPath_TransitionsThreeRowsAndAuditsEach()
    {
        await using var db = CreateContext();
        for (long i = 1; i <= 3; i++)
        {
            db.Applications.Add(new ServiceApplication
            {
                Id = i,
                SolicitantId = 1,
                ServicePassportId = 1,
                Status = ApplicationStatus.PendingApproval,
                CreatedAtUtc = ClockNow,
                IsActive = true,
                FormPayloadJson = "{}",
            });
        }
        await db.SaveChangesAsync();

        var auditEvents = new List<string>();
        var audit = BuildAuditCapturing(auditEvents);
        var op = new CerereChangeStatusBulkOperation(db, BuildClock(), audit);
        var parameters = JsonSerializer.Serialize(new { newStatus = "Approved" });
        var caller = BuildCaller();

        for (long i = 1; i <= 3; i++)
        {
            var outcome = await op.ExecuteAsync(i, parameters, caller, CancellationToken.None);
            outcome.Success.Should().BeTrue();
        }

        var rows = await db.Applications.OrderBy(a => a.Id).ToListAsync();
        rows.Should().OnlyContain(a => a.Status == ApplicationStatus.Approved);
        auditEvents.Should().HaveCount(3);
        auditEvents.Should().AllSatisfy(c => c.Should().Be("CERERE.STATUS_CHANGED"));
    }

    // ─────────────────────── WorkflowTaskMarkCompleteBulkOperation ───────────────────────

    [Fact]
    public async Task WorkflowTaskMarkComplete_HappyPath_TransitionsTwoRowsAndStampsCompletedUtc()
    {
        await using var db = CreateContext();
        for (long i = 1; i <= 2; i++)
        {
            db.WorkflowTasks.Add(new WorkflowTask
            {
                Id = i,
                Title = $"Task {i}",
                Status = WorkflowTaskStatus.InProgress,
                DossierId = 1,
                CreatedAtUtc = ClockNow.AddDays(-1),
                IsActive = true,
            });
        }
        await db.SaveChangesAsync();

        var auditEvents = new List<string>();
        var audit = BuildAuditCapturing(auditEvents);
        var op = new WorkflowTaskMarkCompleteBulkOperation(db, BuildClock(), audit);
        var caller = BuildCaller();

        for (long i = 1; i <= 2; i++)
        {
            var outcome = await op.ExecuteAsync(i, parametersJson: null, caller, CancellationToken.None);
            outcome.Success.Should().BeTrue();
        }

        var rows = await db.WorkflowTasks.OrderBy(t => t.Id).ToListAsync();
        rows.Should().OnlyContain(t => t.Status == WorkflowTaskStatus.Completed);
        rows.Should().OnlyContain(t => t.CompletedAtUtc == ClockNow);
        auditEvents.Should().HaveCount(2);
        auditEvents.Should().AllSatisfy(c => c.Should().Be("WORKFLOWTASK.COMPLETED"));
    }
}
