using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R1000..R1034 / TOR §3.2-AB..AD — tests for
/// <see cref="VoucherQuotaService"/>. Validates the configure / check /
/// reserve / release primitives that gate the spa / rehabilitation /
/// sanatorium passports.
/// </summary>
public sealed class VoucherQuotaServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 4, 0, 0, DateTimeKind.Utc);

    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow { get; }

        public StubClock(DateTime now) { UtcNow = now; }
    }

    private static CnasDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-voucher-{Guid.NewGuid():N}")
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
        c.CorrelationId.Returns("corr-vq");
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

    private static VoucherQuotaService NewSvc(CnasDbContext db, IAuditService audit)
        => new(db, db, new StubClock(ClockNow), NewSqids(), NewCaller(), audit);

    [Fact]
    public async Task CheckAvailability_HappyPath_ReturnsRemaining()
    {
        using var db = CreateDb();
        var svc = NewSvc(db, NewAudit(out _));
        await svc.ConfigureQuotaAsync("3.2-AB", 2026, monthlyQuota: 5, annualQuota: 50, CancellationToken.None);

        var result = await svc.CheckAvailabilityAsync("3.2-AB", 2026, 5, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.MonthlyRemaining.Should().Be(5);
        result.Value.AnnualRemaining.Should().Be(50);
        result.Value.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Reserve_WhenAvailable_DecrementsAndAudits()
    {
        using var db = CreateDb();
        var svc = NewSvc(db, NewAudit(out var codes));
        await svc.ConfigureQuotaAsync("3.2-AB", 2026, monthlyQuota: 5, annualQuota: 50, CancellationToken.None);

        var reserve = await svc.ReserveAsync("3.2-AB", 2026, 5, CancellationToken.None);

        reserve.IsSuccess.Should().BeTrue();
        codes.Should().Contain(IVoucherQuotaService.AuditReserved);
        var check = await svc.CheckAvailabilityAsync("3.2-AB", 2026, 5, CancellationToken.None);
        check.Value.MonthlyRemaining.Should().Be(4);
        check.Value.AnnualRemaining.Should().Be(49);
    }

    [Fact]
    public async Task Reserve_WhenMonthlyExhausted_FailsWithQuotaExhaustedCode()
    {
        using var db = CreateDb();
        var svc = NewSvc(db, NewAudit(out _));
        await svc.ConfigureQuotaAsync("3.2-AB", 2026, monthlyQuota: 1, annualQuota: 50, CancellationToken.None);

        var first = await svc.ReserveAsync("3.2-AB", 2026, 5, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var second = await svc.ReserveAsync("3.2-AB", 2026, 5, CancellationToken.None);

        second.IsSuccess.Should().BeFalse();
        second.ErrorCode.Should().Be(IVoucherQuotaService.QuotaExhaustedCode);
    }

    [Fact]
    public async Task Reserve_WhenAnnualExhausted_FailsWithQuotaExhaustedCode()
    {
        using var db = CreateDb();
        var svc = NewSvc(db, NewAudit(out _));
        await svc.ConfigureQuotaAsync("3.2-AB", 2026, monthlyQuota: 0, annualQuota: 1, CancellationToken.None);

        var first = await svc.ReserveAsync("3.2-AB", 2026, 5, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var second = await svc.ReserveAsync("3.2-AB", 2026, 5, CancellationToken.None);

        second.IsSuccess.Should().BeFalse();
        second.ErrorCode.Should().Be(IVoucherQuotaService.QuotaExhaustedCode);
    }

    [Fact]
    public async Task Release_AfterReserve_RestoresSlotAndAudits()
    {
        using var db = CreateDb();
        var svc = NewSvc(db, NewAudit(out var codes));
        await svc.ConfigureQuotaAsync("3.2-AB", 2026, monthlyQuota: 2, annualQuota: 20, CancellationToken.None);

        await svc.ReserveAsync("3.2-AB", 2026, 5, CancellationToken.None);
        var release = await svc.ReleaseAsync("3.2-AB", 2026, 5, CancellationToken.None);

        release.IsSuccess.Should().BeTrue();
        codes.Should().Contain(IVoucherQuotaService.AuditReleased);
        var check = await svc.CheckAvailabilityAsync("3.2-AB", 2026, 5, CancellationToken.None);
        check.Value.MonthlyRemaining.Should().Be(2);
        check.Value.AnnualRemaining.Should().Be(20);
    }

    [Fact]
    public async Task ConfigureQuota_Idempotent_UpdatesCapsOnSecondCall()
    {
        using var db = CreateDb();
        var svc = NewSvc(db, NewAudit(out _));

        var first = await svc.ConfigureQuotaAsync("3.2-AB", 2026, monthlyQuota: 5, annualQuota: 50, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        first.Value.MonthlyQuota.Should().Be(5);

        var second = await svc.ConfigureQuotaAsync("3.2-AB", 2026, monthlyQuota: 10, annualQuota: 100, CancellationToken.None);
        second.IsSuccess.Should().BeTrue();
        second.Value.MonthlyQuota.Should().Be(10);
        second.Value.AnnualQuota.Should().Be(100);

        db.VoucherQuotas.Should().HaveCount(1);
    }

    [Fact]
    public async Task Reserve_MonthRollover_ResetsMonthlyCounterIndependently()
    {
        using var db = CreateDb();
        var svc = NewSvc(db, NewAudit(out _));
        await svc.ConfigureQuotaAsync("3.2-AB", 2026, monthlyQuota: 2, annualQuota: 50, CancellationToken.None);

        // Reserve in month 5 (May)
        await svc.ReserveAsync("3.2-AB", 2026, 5, CancellationToken.None);
        await svc.ReserveAsync("3.2-AB", 2026, 5, CancellationToken.None);
        var thirdInMay = await svc.ReserveAsync("3.2-AB", 2026, 5, CancellationToken.None);
        thirdInMay.IsSuccess.Should().BeFalse();
        thirdInMay.ErrorCode.Should().Be(IVoucherQuotaService.QuotaExhaustedCode);

        // Month rollover to month 6 (June) — monthly cap resets
        var firstInJune = await svc.ReserveAsync("3.2-AB", 2026, 6, CancellationToken.None);
        firstInJune.IsSuccess.Should().BeTrue();
        var checkJune = await svc.CheckAvailabilityAsync("3.2-AB", 2026, 6, CancellationToken.None);
        checkJune.Value.MonthlyRemaining.Should().Be(1);
        // Annual counter accumulated across both months.
        checkJune.Value.AnnualRemaining.Should().Be(50 - 3);
    }

    [Fact]
    public async Task CheckAvailability_WhenQuotaNotConfigured_FailsWithNotConfigured()
    {
        using var db = CreateDb();
        var svc = NewSvc(db, NewAudit(out _));

        var result = await svc.CheckAvailabilityAsync("3.2-AB", 2026, 5, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(IVoucherQuotaService.QuotaNotConfiguredCode);
    }

    /// <summary>
    /// R1000..R1034 — concurrency contract. A single transient
    /// <see cref="DbUpdateConcurrencyException"/> on the reserve save is
    /// absorbed by the bounded retry loop: the second attempt reloads the row
    /// and re-evaluates the cap, then succeeds. Pins the fix for the
    /// "concurrent reserve aborts the API call" failure mode.
    /// </summary>
    [Fact]
    public async Task Reserve_TransientConcurrencyConflict_RetriesAndSucceeds()
    {
        using var db = new TransientConcurrencyDbContext(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-voucher-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            throwOnNextSaves: 1);
        var svc = NewSvc(db, NewAudit(out _));
        await svc.ConfigureQuotaAsync("3.2-AB", 2026, monthlyQuota: 5, annualQuota: 50, CancellationToken.None);
        db.ArmThrows();

        var result = await svc.ReserveAsync("3.2-AB", 2026, 5, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "the retry loop must absorb a single transient concurrency conflict");
    }

    /// <summary>
    /// R1000..R1034 — exhausted retry budget. Two consecutive transient
    /// concurrency conflicts must surface a structured
    /// <see cref="ErrorCodes.Conflict"/> failure rather than bubble an
    /// unhandled <see cref="DbUpdateConcurrencyException"/>.
    /// </summary>
    [Fact]
    public async Task Reserve_ExhaustedRetries_ReturnsConflict()
    {
        using var db = new TransientConcurrencyDbContext(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-voucher-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            throwOnNextSaves: 99);
        var svc = NewSvc(db, NewAudit(out _));
        await svc.ConfigureQuotaAsync("3.2-AB", 2026, monthlyQuota: 5, annualQuota: 50, CancellationToken.None);
        db.ArmThrows();

        Result result = default;
        var act = async () => result = await svc.ReserveAsync("3.2-AB", 2026, 5, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "the service must surface exhausted contention as a Result, never an unhandled DbUpdateConcurrencyException");
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>
    /// <see cref="CnasDbContext"/> subclass that throws
    /// <see cref="DbUpdateConcurrencyException"/> from the first N
    /// SaveChangesAsync calls AFTER <see cref="ArmThrows"/> is invoked — used
    /// to simulate xmin contention on the voucher-quota row without needing
    /// a real Postgres instance.
    /// </summary>
    private sealed class TransientConcurrencyDbContext : CnasDbContext
    {
        private readonly int _initialArm;
        private int _remainingThrows;

        public TransientConcurrencyDbContext(
            DbContextOptions<CnasDbContext> options,
            int throwOnNextSaves)
            : base(options)
        {
            _initialArm = throwOnNextSaves;
        }

        public void ArmThrows() => _remainingThrows = _initialArm;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_remainingThrows > 0)
            {
                _remainingThrows -= 1;
                throw new DbUpdateConcurrencyException("Simulated quota xmin contention (test).");
            }
            return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
