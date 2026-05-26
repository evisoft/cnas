using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R1000..R1034 / TOR §3.2-Z — tests for
/// <see cref="RecurrentPaymentSchedulerService"/>. Validates create, run-due
/// (Active+due picks, cadence advance, Inactive skip), suspend / resume.
/// </summary>
public sealed class RecurrentPaymentSchedulerServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(ClockNow);

    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow { get; }

        public StubClock(DateTime now) { UtcNow = now; }
    }

    private static CnasDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-rps-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static ISqidService NewSqids()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        s.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return s;
    }

    private static ICallerContext NewCaller()
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(1L);
        c.UserSqid.Returns("USR-1");
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-rps");
        return c;
    }

    private static IAuditService NewAudit(out List<string> codes)
    {
        var list = new List<string>();
        codes = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => { list.Add(c.ArgAt<string>(0)); return Task.FromResult(Result.Success()); });
        return a;
    }

    private static RecurrentPaymentSchedulerService NewSvc(
        CnasDbContext db,
        IAuditService audit,
        ICnasTimeProvider? clock = null)
        => new(db, db, clock ?? new StubClock(ClockNow), NewSqids(), NewCaller(), audit);

    /// <summary>
    /// Seeds a Solicitant whose Id matches the BeneficiaryId carried by
    /// schedule inputs (42). The scheduler's run-due path resolves the
    /// beneficiary's IDNP from this row. EF Core in-memory honours explicit
    /// surrogate-id assignment so we can target id=42 directly without
    /// padding rows.
    /// </summary>
    private static async Task SeedBeneficiaryAsync(CnasDbContext db, long id = 42, string idnp = "2000000000007")
    {
        if (await db.Solicitants.AnyAsync(s => s.Id == id))
        {
            return;
        }
        db.Solicitants.Add(new Solicitant
        {
            Id = id,
            CreatedAtUtc = ClockNow.AddDays(-1),
            NationalId = idnp,
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Beneficiary",
            PreferredLanguage = "ro",
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private static RecurrentPaymentScheduleCreateInputDto SampleInput(
        DateOnly? next = null,
        string cadence = "Monthly")
        => new(
            BeneficiarySqid: "SQID-42",
            ServiceCode: "3.2-Z",
            Amount: 500m,
            NextPaymentDate: next ?? Today,
            Cadence: cadence);

    [Fact]
    public async Task Create_HappyPath_PersistsRowAndAudits()
    {
        using var db = CreateDb();
        var svc = NewSvc(db, NewAudit(out var codes));

        var result = await svc.CreateAsync(SampleInput(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ServiceCode.Should().Be("3.2-Z");
        codes.Should().Contain(IRecurrentPaymentSchedulerService.AuditCreated);
        db.RecurrentPaymentSchedules.Should().HaveCount(1);
    }

    [Fact]
    public async Task RunDue_PicksActiveAndDue_GeneratesMPayOrders()
    {
        using var db = CreateDb();
        await SeedBeneficiaryAsync(db);
        var svc = NewSvc(db, NewAudit(out var codes));

        await svc.CreateAsync(SampleInput(next: Today), CancellationToken.None);
        await svc.CreateAsync(SampleInput(next: Today.AddDays(-1)), CancellationToken.None);

        var run = await svc.RunDueAsync(CancellationToken.None);

        run.IsSuccess.Should().BeTrue();
        run.Value.Should().Be(2);
        db.MPayOrders.Should().HaveCount(2);
        codes.Should().Contain(IRecurrentPaymentSchedulerService.AuditDispatched);
    }

    [Fact]
    public async Task RunDue_SkipsInactiveSchedules()
    {
        using var db = CreateDb();
        await SeedBeneficiaryAsync(db);
        var svc = NewSvc(db, NewAudit(out _));
        var created = await svc.CreateAsync(SampleInput(next: Today), CancellationToken.None);
        await svc.SuspendAsync(created.Value.Id, CancellationToken.None);

        var run = await svc.RunDueAsync(CancellationToken.None);

        run.IsSuccess.Should().BeTrue();
        run.Value.Should().Be(0);
        db.MPayOrders.Should().BeEmpty();
    }

    /// <summary>
    /// Post-fix: RunDueAsync NO LONGER advances NextPaymentDate. The schedule
    /// is only advanced by the callback advancer (invoked from the MPay
    /// callback handler when the order is confirmed). This pins that
    /// invariant — a successful RunDue leaves NextPaymentDate unchanged.
    /// </summary>
    [Fact]
    public async Task RunDue_DoesNotAdvanceNextPaymentDate_UntilCallbackConfirms()
    {
        using var db = CreateDb();
        await SeedBeneficiaryAsync(db);
        var svc = NewSvc(db, NewAudit(out _));
        await svc.CreateAsync(SampleInput(next: Today, cadence: "Monthly"), CancellationToken.None);

        var run = await svc.RunDueAsync(CancellationToken.None);
        run.IsSuccess.Should().BeTrue();

        var s = db.RecurrentPaymentSchedules.Single();
        s.NextPaymentDate.Should().Be(Today,
            "RunDue dispatches an MPay order but does NOT advance the schedule — only the callback advancer does, on confirmation");
        // LastDispatchedOrderId is back-filled so the callback advancer can
        // find this schedule when the order's confirmation lands.
        s.LastDispatchedOrderId.Should().NotBeNull();
    }

    /// <summary>
    /// Post-fix: a Monthly cadence schedule advances via the callback
    /// advancer (not RunDue). Pins the new dispatch→confirm→advance
    /// pipeline that prevents NextPaymentDate from running ahead of bank
    /// settlement.
    /// </summary>
    [Fact]
    public async Task CallbackAdvancer_OnConfirmation_AdvancesNextPaymentDateByCadenceMonthly()
    {
        using var db = CreateDb();
        await SeedBeneficiaryAsync(db);
        var svc = NewSvc(db, NewAudit(out _));
        await svc.CreateAsync(SampleInput(next: Today, cadence: "Monthly"), CancellationToken.None);
        var run = await svc.RunDueAsync(CancellationToken.None);
        run.IsSuccess.Should().BeTrue();

        var schedule = db.RecurrentPaymentSchedules.Single();
        var orderId = schedule.LastDispatchedOrderId!.Value;
        var advancer = new RecurrentPaymentAdvancer(db, new StubClock(ClockNow));
        var ack = await advancer.AdvanceOnConfirmationAsync(orderId, CancellationToken.None);

        ack.IsSuccess.Should().BeTrue();
        var refreshed = db.RecurrentPaymentSchedules.Single();
        refreshed.NextPaymentDate.Should().Be(Today.AddMonths(1));
        refreshed.LastPaymentAtUtc.Should().Be(ClockNow);
        refreshed.FailureCount.Should().Be(0);
    }

    /// <summary>
    /// Post-fix: Quarterly + Annual schedules advance per cadence via the
    /// callback advancer.
    /// </summary>
    [Fact]
    public async Task CallbackAdvancer_AdvancesByCadenceForQuarterlyAndAnnual()
    {
        using var db = CreateDb();
        await SeedBeneficiaryAsync(db);
        var svc = NewSvc(db, NewAudit(out _));
        await svc.CreateAsync(SampleInput(next: Today, cadence: "Quarterly"), CancellationToken.None);
        await svc.CreateAsync(SampleInput(next: Today, cadence: "Annual"), CancellationToken.None);

        await svc.RunDueAsync(CancellationToken.None);

        var rows = db.RecurrentPaymentSchedules.OrderBy(s => s.Id).ToList();
        var advancer = new RecurrentPaymentAdvancer(db, new StubClock(ClockNow));
        await advancer.AdvanceOnConfirmationAsync(rows[0].LastDispatchedOrderId!.Value, CancellationToken.None);
        await advancer.AdvanceOnConfirmationAsync(rows[1].LastDispatchedOrderId!.Value, CancellationToken.None);

        var refreshed = db.RecurrentPaymentSchedules.OrderBy(s => s.Id).ToList();
        refreshed[0].NextPaymentDate.Should().Be(Today.AddMonths(3));
        refreshed[1].NextPaymentDate.Should().Be(Today.AddMonths(12));
    }

    /// <summary>
    /// Post-fix: a schedule with no resolvable beneficiary (missing
    /// Solicitant row) is skipped — no MPay order is emitted, the
    /// FailureCount is incremented for the operator dashboard, and the
    /// service does not crash the whole batch.
    /// </summary>
    [Fact]
    public async Task RunDue_MissingBeneficiary_SkipsScheduleAndBumpsFailureCount()
    {
        using var db = CreateDb();
        // Intentionally NO SeedBeneficiaryAsync — the BeneficiaryId 42 has no
        // matching Solicitant row.
        var svc = NewSvc(db, NewAudit(out _));
        await svc.CreateAsync(SampleInput(next: Today), CancellationToken.None);

        var run = await svc.RunDueAsync(CancellationToken.None);

        run.IsSuccess.Should().BeTrue();
        run.Value.Should().Be(0, "the schedule with no resolvable beneficiary is skipped");
        db.MPayOrders.Should().BeEmpty();
        var s = db.RecurrentPaymentSchedules.Single();
        s.FailureCount.Should().BeGreaterThan(0);
        s.NextPaymentDate.Should().Be(Today, "the next sweep retries the same row");
    }

    [Fact]
    public async Task Suspend_FlipsIsActiveFalse_AndAudits()
    {
        using var db = CreateDb();
        var svc = NewSvc(db, NewAudit(out var codes));
        var created = await svc.CreateAsync(SampleInput(), CancellationToken.None);

        var suspend = await svc.SuspendAsync(created.Value.Id, CancellationToken.None);

        suspend.IsSuccess.Should().BeTrue();
        suspend.Value.IsActive.Should().BeFalse();
        codes.Should().Contain(IRecurrentPaymentSchedulerService.AuditSuspended);
    }

    [Fact]
    public async Task Resume_FlipsIsActiveTrue_AndAllowsRunDueToProcess()
    {
        using var db = CreateDb();
        await SeedBeneficiaryAsync(db);
        var svc = NewSvc(db, NewAudit(out var codes));
        var created = await svc.CreateAsync(SampleInput(next: Today), CancellationToken.None);
        await svc.SuspendAsync(created.Value.Id, CancellationToken.None);

        var resume = await svc.ResumeAsync(created.Value.Id, CancellationToken.None);

        resume.IsSuccess.Should().BeTrue();
        resume.Value.IsActive.Should().BeTrue();
        codes.Should().Contain(IRecurrentPaymentSchedulerService.AuditResumed);

        var run = await svc.RunDueAsync(CancellationToken.None);
        run.Value.Should().Be(1);
    }
}
