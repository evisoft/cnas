using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Penalties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0820 / TOR BP 1.2-K — service-level tests for
/// <see cref="ManagementPeriodService"/>. Exercises CloseAsync /
/// ReopenAsync lifecycle, the singleton-per-month rule, the
/// IsMonthClosedAsync probe (including the re-opened pass-through), and the
/// Critical audit emissions.
/// </summary>
public sealed class ManagementPeriodServiceTests
{
    /// <summary>Fixed UTC clock used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical first-of-month anchor (April 2026).</summary>
    private static readonly DateOnly Month = new(2026, 4, 1);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-management-period-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Sqid mock — encodes "SQID-{id}".</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return sqids;
    }

    /// <summary>Audit capture — exposes the most-recent invocation arguments.</summary>
    private static (IAuditService Audit, Func<(string Code, AuditSeverity Severity)?> Last)
        NewAuditCapture()
    {
        (string Code, AuditSeverity Severity)? slot = null;
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                slot = (call.ArgAt<string>(0), call.ArgAt<AuditSeverity>(1));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => slot);
    }

    /// <summary>Caller stub.</summary>
    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(7L);
        caller.UserSqid.Returns("USR-7");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-period");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-admin"]);
        return caller;
    }

    /// <summary>Builds the SUT around the supplied context + audit collaborator.</summary>
    private static ManagementPeriodService NewService(CnasDbContext db, IAuditService audit)
        => new(
            db,
            db,
            new StubClock(ClockNow),
            NewSqidMock(),
            NewCaller(),
            audit,
            new ManagementPeriodCloseInputDtoValidator(),
            new ManagementPeriodReopenInputDtoValidator());

    /// <summary>R0820 — CloseAsync persists the row + audits Critical.</summary>
    [Fact]
    public async Task CloseAsync_HappyPath_PersistsAndAuditsCritical()
    {
        var db = CreateContext();
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CloseAsync(Month, "End of April", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Month.Should().Be(Month);
        result.Value.IsReopened.Should().BeFalse();
        last()!.Value.Code.Should().Be(ManagementPeriodService.AuditClosed);
        last()!.Value.Severity.Should().Be(AuditSeverity.Critical);

        var rows = await db.ManagementPeriodCloses.ToListAsync();
        rows.Should().HaveCount(1);
    }

    /// <summary>R0820 — second CloseAsync against the same month returns Conflict.</summary>
    [Fact]
    public async Task CloseAsync_AlreadyClosed_ReturnsConflict()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var first = await sut.CloseAsync(Month, null, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await sut.CloseAsync(Month, null, CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
        second.ErrorMessage.Should().Be(ManagementPeriodService.MonthAlreadyClosedMessage);
    }

    /// <summary>R0820 — ReopenAsync flips IsReopened + emits Critical audit.</summary>
    [Fact]
    public async Task ReopenAsync_HappyPath_FlipsIsReopenedAndAuditsCritical()
    {
        var db = CreateContext();
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.CloseAsync(Month, null, CancellationToken.None);
        var result = await sut.ReopenAsync(Month, "Adjustment found late", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var row = await db.ManagementPeriodCloses.SingleAsync();
        row.IsReopened.Should().BeTrue();
        row.ReopenReason.Should().Be("Adjustment found late");
        last()!.Value.Code.Should().Be(ManagementPeriodService.AuditReopened);
        last()!.Value.Severity.Should().Be(AuditSeverity.Critical);
    }

    /// <summary>R0820 — IsMonthClosedAsync returns true for a closed month.</summary>
    [Fact]
    public async Task IsMonthClosedAsync_ClosedMonth_ReturnsTrue()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.CloseAsync(Month, null, CancellationToken.None);

        var isClosed = await sut.IsMonthClosedAsync(Month, CancellationToken.None);
        isClosed.Should().BeTrue();
    }

    /// <summary>R0820 — IsMonthClosedAsync returns false for an unclosed month.</summary>
    [Fact]
    public async Task IsMonthClosedAsync_UnclosedMonth_ReturnsFalse()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var isClosed = await sut.IsMonthClosedAsync(Month, CancellationToken.None);
        isClosed.Should().BeFalse();
    }

    /// <summary>R0820 — re-opened month is treated as open by IsMonthClosedAsync.</summary>
    [Fact]
    public async Task IsMonthClosedAsync_ReopenedMonth_ReturnsFalse()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.CloseAsync(Month, null, CancellationToken.None);
        await sut.ReopenAsync(Month, "Need to fix data", CancellationToken.None);

        var isClosed = await sut.IsMonthClosedAsync(Month, CancellationToken.None);
        isClosed.Should().BeFalse();
    }
}
