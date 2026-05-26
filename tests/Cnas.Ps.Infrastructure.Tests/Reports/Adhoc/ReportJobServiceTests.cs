using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports.Adhoc;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — service-level tests for
/// <see cref="ReportJobService"/>. Uses EF Core InMemory + NSubstitute,
/// mirroring the harness shape used by <c>ReportTemplateServiceTests</c>.
/// </summary>
public class ReportJobServiceTests
{
    /// <summary>Deterministic clock anchor.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Roles seed (passed to NSubstitute).</summary>
    private static readonly string[] CallerRoles = ["cnas-user"];

    [Fact]
    public async Task EnqueueAsync_ValidInput_CreatesQueuedRow_AndEmitsAuditInformation()
    {
        var h = Harness.Create();
        var template = await h.SeedTemplateAsync(ownerId: Harness.CallerId);

        var result = await h.Service.EnqueueAsync(new ReportJobEnqueueDto(
            ReportTemplateSqid: $"SQID-{template.Id}",
            Format: ExportFormat.Csv.ToString()));

        result.IsSuccess.Should().BeTrue();
        var row = await h.Db.ReportJobs.SingleAsync();
        row.Status.Should().Be(ReportJobStatus.Queued);
        row.ReportTemplateId.Should().Be(template.Id);
        row.RequestedByUserId.Should().Be(Harness.CallerId);
        row.QueuedAtUtc.Should().Be(ClockNow);

        await h.Audit.Received().RecordAsync(
            "REPORT.JOB.ENQUEUED",
            AuditSeverity.Information,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_UnknownTemplate_ReturnsNotFound()
    {
        var h = Harness.Create();

        var result = await h.Service.EnqueueAsync(new ReportJobEnqueueDto(
            ReportTemplateSqid: "SQID-9999",
            Format: ExportFormat.Csv.ToString()));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task CancelAsync_OnQueuedRow_FlipsToCancelled_AndEmitsAudit()
    {
        var h = Harness.Create();
        var template = await h.SeedTemplateAsync(ownerId: Harness.CallerId);
        var enqueued = await h.Service.EnqueueAsync(new ReportJobEnqueueDto(
            $"SQID-{template.Id}", ExportFormat.Csv.ToString()));
        enqueued.IsSuccess.Should().BeTrue();
        var rowId = (await h.Db.ReportJobs.SingleAsync()).Id;

        var cancel = await h.Service.CancelAsync(rowId);

        cancel.IsSuccess.Should().BeTrue();
        var row = await h.Db.ReportJobs.SingleAsync();
        row.Status.Should().Be(ReportJobStatus.Cancelled);
        row.CompletedAtUtc.Should().NotBeNull();

        await h.Audit.Received().RecordAsync(
            "REPORT.JOB.CANCELLED",
            AuditSeverity.Information,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelAsync_RowAlreadyRunning_ReturnsValidationFailed_WithJobNotCancellableMessage()
    {
        var h = Harness.Create();
        var template = await h.SeedTemplateAsync(ownerId: Harness.CallerId);
        var enqueued = await h.Service.EnqueueAsync(new ReportJobEnqueueDto(
            $"SQID-{template.Id}", ExportFormat.Csv.ToString()));
        enqueued.IsSuccess.Should().BeTrue();
        var row = await h.Db.ReportJobs.SingleAsync();
        row.Status = ReportJobStatus.Running;
        await h.Db.SaveChangesAsync();

        var cancel = await h.Service.CancelAsync(row.Id);

        cancel.IsFailure.Should().BeTrue();
        cancel.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        cancel.ErrorMessage.Should().Be(ReportJobService.JobNotCancellableMessage);
    }

    [Fact]
    public async Task ListForCurrentUserAsync_ReturnsOnlyCallersRows()
    {
        var h = Harness.Create();
        var template = await h.SeedTemplateAsync(ownerId: Harness.CallerId, isShared: true);
        // Caller enqueues one.
        await h.Service.EnqueueAsync(new ReportJobEnqueueDto(
            $"SQID-{template.Id}", ExportFormat.Csv.ToString()));

        // A second user enqueues their own job against the same shared template.
        var other = h.WithCaller(9999L, "SQID-9999");
        await other.Service.EnqueueAsync(new ReportJobEnqueueDto(
            $"SQID-{template.Id}", ExportFormat.Csv.ToString()));

        var list = await h.Service.ListForCurrentUserAsync(take: 20);

        list.Should().HaveCount(1);
        list[0].RequestedByUserSqid.Should().Be($"SQID-{Harness.CallerId}");
    }

    // ─────────────────────── Harness ───────────────────────

    /// <summary>Deterministic clock implementation.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Shared NSubstitute-driven harness.</summary>
    private sealed class Harness
    {
        public const long CallerId = 7777L;

        public required CnasDbContext Db { get; init; }
        public required ReportJobService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-reportjob-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            return Build(db, CallerId, $"SQID-{CallerId}");
        }

        public Harness WithCaller(long callerId, string callerSqid) =>
            Build(Db, callerId, callerSqid, Sqids, Audit);

        private static Harness Build(
            CnasDbContext db,
            long callerId,
            string callerSqid,
            ISqidService? sharedSqids = null,
            IAuditService? sharedAudit = null)
        {
            var sqids = sharedSqids ?? Substitute.For<ISqidService>();
            if (sharedSqids is null)
            {
                sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
                sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
                {
                    var arg = call.Arg<string?>();
                    if (!string.IsNullOrEmpty(arg)
                        && arg.StartsWith("SQID-", StringComparison.Ordinal)
                        && long.TryParse(arg.AsSpan(5), out var n))
                    {
                        return Result<long>.Success(n);
                    }
                    return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
                });
            }

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(callerId);
            caller.UserSqid.Returns(callerSqid);
            caller.Roles.Returns(CallerRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns($"corr-{callerId}");

            var audit = sharedAudit ?? Substitute.For<IAuditService>();
            if (sharedAudit is null)
            {
                audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(Result.Success()));
            }

            var clock = new StubClock(ClockNow);
            var service = new ReportJobService(db, caller, sqids, clock, audit);
            return new Harness { Db = db, Service = service, Sqids = sqids, Audit = audit };
        }

        public async Task<ReportTemplate> SeedTemplateAsync(long ownerId, bool isShared = false)
        {
            var template = new ReportTemplate
            {
                Code = $"report.t.{Guid.NewGuid():N}".Substring(0, 28),
                Name = "T",
                Description = null,
                Registry = QueryBudgetRegistries.Solicitant,
                SelectedFieldsJson = "[\"Id\"]",
                FilterJson = System.Text.Json.JsonSerializer.Serialize(
                    new QbeFilterDto(QbeFilter.CombinatorAnd, Array.Empty<QbeConditionDto>())),
                OrderingJson = "[]",
                GroupByField = null,
                OwnerUserId = ownerId,
                IsShared = isShared,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.ReportTemplates.Add(template);
            await Db.SaveChangesAsync();
            return template;
        }
    }
}
