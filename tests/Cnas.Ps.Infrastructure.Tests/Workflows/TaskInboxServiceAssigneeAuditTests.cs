using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Workflows;

/// <summary>
/// R0192 / TOR SEC 051 — verifies that
/// <see cref="TaskInboxService.ReassignAsync"/> emits a dedicated
/// <c>WORKFLOWTASK.ASSIGNEE_CHANGED</c> audit row (alongside the existing
/// <c>WORKFLOWTASK.REASSIGNED</c> event) when the assignee actually changes,
/// carrying the old and new sqids in the payload.
/// </summary>
public sealed class TaskInboxServiceAssigneeAuditTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc);

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record Captured(
        string EventCode,
        AuditSeverity Severity,
        string DetailsJson);

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required TaskInboxService Service { get; init; }
        public required List<Captured> AuditCalls { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            await Task.Yield();
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-assignee-audit-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(1L);
            caller.UserSqid.Returns("SQID-1");
            caller.SourceIp.Returns("203.0.113.7");
            caller.CorrelationId.Returns("corr-assignee");

            var calls = new List<Captured>();
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(c =>
                {
                    calls.Add(new Captured(
                        EventCode: c.ArgAt<string>(0),
                        Severity: c.ArgAt<AuditSeverity>(1),
                        DetailsJson: c.ArgAt<string>(5)));
                    return Task.FromResult(Result.Success());
                });

            var notify = Substitute.For<IWorkflowNotificationOrchestrator>();
            notify.DispatchAsync(
                    Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(),
                    Arg.Any<IDictionary<string, string>?>(), Arg.Any<CancellationToken>())
                .Returns(Result.Success());

            var svc = new TaskInboxService(db, sqids, new StubClock(ClockNow), caller, audit, notify);
            return new Harness { Db = db, Service = svc, AuditCalls = calls };
        }

        public async Task SeedUserAsync(long id)
        {
            Db.UserProfiles.Add(new UserProfile
            {
                Id = id,
                CreatedAtUtc = ClockNow,
                DisplayName = $"User {id}",
                State = UserAccountState.Active,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }

        public async Task<WorkflowTask> SeedTaskAsync(long? currentAssignee)
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = "2000000000007",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Test",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP-X",
                NameRo = "x",
                DescriptionRo = "x",
                FormSchemaJson = "{}",
                WorkflowCode = "WF",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow,
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid().ToString("N")[..8]}",
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            var task = new WorkflowTask
            {
                CreatedAtUtc = ClockNow,
                DossierId = dossier.Id,
                Title = "Examinare cerere",
                Status = WorkflowTaskStatus.InProgress,
                AssignedUserId = currentAssignee,
                GroupCode = "cnas-examiner",
                IsActive = true,
            };
            Db.WorkflowTasks.Add(task);
            await Db.SaveChangesAsync();
            return task;
        }
    }

    [Fact]
    public async Task Reassign_DistinctAssignee_FiresAssigneeChangedAudit()
    {
        var h = await Harness.CreateAsync();
        await h.SeedUserAsync(100L);
        await h.SeedUserAsync(200L);
        var task = await h.SeedTaskAsync(currentAssignee: 100L);

        var result = await h.Service.ReassignAsync(task.Id, 200L, "Concediu medical", absenceId: null);
        result.IsSuccess.Should().BeTrue();

        var changed = h.AuditCalls.Find(c => c.EventCode == "WORKFLOWTASK.ASSIGNEE_CHANGED");
        changed.Should().NotBeNull();
        changed!.Severity.Should().Be(AuditSeverity.Critical);
        changed.DetailsJson.Should().Contain("SQID-100");
        changed.DetailsJson.Should().Contain("SQID-200");
        changed.DetailsJson.Should().Contain("oldAssigneeSqid");
        changed.DetailsJson.Should().Contain("newAssigneeSqid");
    }

    [Fact]
    public async Task Reassign_SameAssignee_IsNoOpAndEmitsNoAudit()
    {
        // iter-149 — short-circuit. Reassigning to the SAME user is a degenerate
        // call: the counter must NOT bump, the SLA clock must NOT reset, and
        // neither the broader REASSIGNED nor the dedicated ASSIGNEE_CHANGED
        // audit row may be written. The service returns Success carrying the
        // current task projection so callers see no error.
        var h = await Harness.CreateAsync();
        await h.SeedUserAsync(100L);
        var task = await h.SeedTaskAsync(currentAssignee: 100L);
        var originalCount = task.ReassignmentCount;

        var result = await h.Service.ReassignAsync(task.Id, 100L, "no-op rebind", absenceId: null);
        result.IsSuccess.Should().BeTrue();

        // No audit rows of any kind were emitted by the no-op path.
        h.AuditCalls.Should().NotContain(c => c.EventCode == "WORKFLOWTASK.REASSIGNED");
        h.AuditCalls.Should().NotContain(c => c.EventCode == "WORKFLOWTASK.ASSIGNEE_CHANGED");

        // Task row is pristine — counter unchanged, no reassignment reason recorded.
        var reloaded = await h.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        reloaded.AssignedUserId.Should().Be(100L);
        reloaded.ReassignmentCount.Should().Be(originalCount);
        reloaded.ReassignmentReason.Should().BeNull();
    }
}
