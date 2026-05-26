using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ExecutoryDocuments;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.ExecutoryDocuments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.ExecutoryDocuments;

/// <summary>
/// R1600 / R1406 / TOR Annex 3.8 / §3.6-G — service-level tests for the
/// executory-documents registry. Exercises the register / modify / cancel
/// lifecycle plus the running-tally accumulation that drives the
/// auto-complete transition.
/// </summary>
public sealed class ExecutoryDocumentServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical Moldovan IBAN reused by the tests.</summary>
    private const string Iban = "MD24AG000225100013104168";

    /// <summary>Canonical IDNP reused by the tests.</summary>
    private const string Idnp = "2002000000007";

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-exec-docs-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Sqid stub that round-trips "EXE-{id}" strings.</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"EXE-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("EXE-", StringComparison.Ordinal)
                && long.TryParse(s["EXE-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>Captures audit invocations for assertion.</summary>
    private static (IAuditService Audit, Func<List<(string Code, AuditSeverity Severity, long? TargetId)>> Calls)
        NewAuditCapture()
    {
        var calls = new List<(string Code, AuditSeverity Severity, long? TargetId)>();
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
                calls.Add((
                    call.ArgAt<string>(0),
                    call.ArgAt<AuditSeverity>(1),
                    call.ArgAt<long?>(4)));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => calls);
    }

    /// <summary>Authenticated caller stub.</summary>
    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(7L);
        caller.UserSqid.Returns("USR-7");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-exec");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-user"]);
        return caller;
    }

    /// <summary>Builds the SUT.</summary>
    private static ExecutoryDocumentService NewService(CnasDbContext db, IAuditService audit)
    {
        var clock = new StubClock(ClockNow);
        return new(
            db,
            clock,
            NewSqidMock(),
            NewCaller(),
            audit,
            IdHashHelper.Instance,
            new ExecutoryDocumentRegisterInputValidator(clock),
            new ExecutoryDocumentModifyInputValidator(),
            new ExecutoryDocumentReasonInputValidator());
    }

    /// <summary>Builds a canonical register-input DTO.</summary>
    private static ExecutoryDocumentRegisterInputDto BuildRegisterInput(
        string? series = null,
        string mode = nameof(ExecutoryDocumentWithholdingMode.FixedAmount),
        decimal? amount = 1_000m,
        decimal? percentage = null,
        decimal? totalOwed = 5_000m) => new(
            DocumentSeriesNumber: series,
            DebtorIdnp: Idnp,
            Kind: nameof(ExecutoryDocumentKind.CourtOrder),
            IssuedBy: "Judecătoria Chișinău",
            IssuedDate: new DateOnly(2026, 5, 1),
            EffectiveFrom: new DateOnly(2026, 5, 15),
            EffectiveUntil: new DateOnly(2027, 5, 15),
            WithholdingMode: mode,
            WithholdingAmountMdl: amount,
            WithholdingPercentage: percentage,
            PriorityRank: 1,
            CreditorAccountIban: Iban,
            CreditorName: "Direcția Asistență Socială",
            TotalOwedMdl: totalOwed);

    // ───────── R1600 — RegisterAsync ─────────

    /// <summary>R1600 — happy-path register persists Active + emits Critical audit + counter.</summary>
    [Fact]
    public async Task RegisterAsync_HappyPath_PersistsActiveAndAudits()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RegisterAsync(BuildRegisterInput());

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ExecutoryDocumentStatus.Active));
        result.Value.TotalWithheldMdl.Should().Be(0m);
        (await db.ExecutoryDocuments.CountAsync()).Should().Be(1);
        calls().Should().ContainSingle(c =>
            c.Code == ExecutoryDocumentService.AuditRegistered
            && c.Severity == AuditSeverity.Critical);
    }

    /// <summary>R1600 — register auto-generates DocumentSeriesNumber when not supplied.</summary>
    [Fact]
    public async Task RegisterAsync_AutoGeneratesSeriesNumber()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var first = await sut.RegisterAsync(BuildRegisterInput(series: null));
        var second = await sut.RegisterAsync(BuildRegisterInput(series: null));

        first.Value.DocumentSeriesNumber.Should().Be("EXE-2026-000001");
        second.Value.DocumentSeriesNumber.Should().Be("EXE-2026-000002");
    }

    /// <summary>R1600 — explicit series collision → Conflict.</summary>
    [Fact]
    public async Task RegisterAsync_DuplicateSeriesNumber_ReturnsConflict()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.RegisterAsync(BuildRegisterInput(series: "OE-2026-1234"));
        var second = await sut.RegisterAsync(BuildRegisterInput(series: "OE-2026-1234"));

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    // ───────── R1600 — ModifyAsync ─────────

    /// <summary>R1600 — modify after Completed → Result.Conflict.</summary>
    [Fact]
    public async Task ModifyAsync_AfterCompleted_ReturnsConflict()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var reg = await sut.RegisterAsync(BuildRegisterInput(totalOwed: 1_000m));
        await sut.RecordWithholdingAsync(reg.Value.Id, 1_000m, "TEST.REF.1");

        var modify = await sut.ModifyAsync(
            reg.Value.Id,
            new ExecutoryDocumentModifyInputDto(
                IssuedBy: null,
                EffectiveUntil: null,
                WithholdingMode: null,
                WithholdingAmountMdl: 5_000m,
                WithholdingPercentage: null,
                PriorityRank: null,
                CreditorAccountIban: null,
                CreditorName: null,
                TotalOwedMdl: null,
                ChangeReason: "test reason"));

        modify.IsFailure.Should().BeTrue();
        modify.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    // ───────── R1600 — CancelAsync ─────────

    /// <summary>R1600 — cancel transitions Active → Cancelled and populates the reason.</summary>
    [Fact]
    public async Task CancelAsync_FlipsStatusAndStoresReason()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var reg = await sut.RegisterAsync(BuildRegisterInput());

        var result = await sut.CancelAsync(
            reg.Value.Id,
            new ExecutoryDocumentReasonInputDto("Court reversed judgment"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ExecutoryDocumentStatus.Cancelled));
        result.Value.CancellationReason.Should().Be("Court reversed judgment");
        calls().Should().ContainSingle(c =>
            c.Code == ExecutoryDocumentService.AuditCancelled
            && c.Severity == AuditSeverity.Critical);
    }

    // ───────── R1600 — RecordWithholdingAsync ─────────

    /// <summary>R1600 — RecordWithholding accumulates the running total.</summary>
    [Fact]
    public async Task RecordWithholdingAsync_AccumulatesTotal()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var reg = await sut.RegisterAsync(BuildRegisterInput(totalOwed: 5_000m));

        await sut.RecordWithholdingAsync(reg.Value.Id, 300m, "PAY.001");
        var second = await sut.RecordWithholdingAsync(reg.Value.Id, 700m, "PAY.002");

        second.IsSuccess.Should().BeTrue();
        second.Value.TotalWithheldMdl.Should().Be(1_000m);
        second.Value.Status.Should().Be(nameof(ExecutoryDocumentStatus.Active));
    }

    /// <summary>R1600 — auto-complete fires when TotalWithheldMdl reaches TotalOwedMdl.</summary>
    [Fact]
    public async Task RecordWithholdingAsync_AutoCompletesWhenThresholdReached()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var reg = await sut.RegisterAsync(BuildRegisterInput(totalOwed: 1_000m));

        var result = await sut.RecordWithholdingAsync(reg.Value.Id, 1_000m, "PAY.FULL");

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ExecutoryDocumentStatus.Completed));
        result.Value.CompletedDate.Should().NotBeNull();
        calls().Should().Contain(c =>
            c.Code == ExecutoryDocumentService.AuditCompleted
            && c.Severity == AuditSeverity.Critical);
    }
}
