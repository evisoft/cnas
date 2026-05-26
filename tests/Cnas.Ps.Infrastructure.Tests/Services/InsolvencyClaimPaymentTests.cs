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
/// R0834 / TOR Annex 1 §8.1.4.5 — sub-table tests covering the claims +
/// payments surface exposed by <see cref="InsolvencyLifecycleService"/>.
/// Each test isolates one branch of the validator + state-machine + audit
/// emission contract.
/// </summary>
public sealed class InsolvencyClaimPaymentTests
{
    /// <summary>Deterministic clock anchor.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Today's date corresponding to the clock anchor.</summary>
    private static readonly DateOnly Today = DateOnly.FromDateTime(ClockNow);

    [Fact]
    public async Task AddClaimAsync_HappyPath_PersistsRow_AndAuditsNotice()
    {
        var harness = await Harness.CreateAsync();
        var caseSqid = await harness.OpenCaseAsync();

        var result = await harness.Service.AddClaimAsync(caseSqid, new InsolvencyClaimInputDto(
            Amount: 1500.50m,
            Currency: "MDL",
            Description: "Unpaid contributions Mar-2024",
            IncurredOn: Today.AddDays(-30)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().StartWith("SQID-");
        result.Value.Amount.Should().Be(1500.50m);
        result.Value.Currency.Should().Be("MDL");

        (await harness.Db.InsolvencyClaims.CountAsync()).Should().Be(1);

        await harness.Audit.Received(1).RecordAsync(
            "INSOLVENCY.CLAIM_ADDED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(InsolvencyClaim),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddClaimAsync_ResolvedCase_ReturnsConflict()
    {
        var harness = await Harness.CreateAsync();
        var caseSqid = await harness.OpenCaseAsync();
        (await harness.Service.ResolveAsync(caseSqid, "Closed out")).IsSuccess.Should().BeTrue();

        var result = await harness.Service.AddClaimAsync(caseSqid, new InsolvencyClaimInputDto(
            Amount: 100m,
            Currency: "MDL",
            Description: "Posthumous claim",
            IncurredOn: Today));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task AddPaymentAsync_HappyPath_PersistsRow_AndAuditsNotice()
    {
        var harness = await Harness.CreateAsync();
        var caseSqid = await harness.OpenCaseAsync();

        var result = await harness.Service.AddPaymentAsync(caseSqid, new InsolvencyPaymentInputDto(
            Amount: 750m,
            PaymentDate: Today,
            Reference: "DIST-2026-001"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Amount.Should().Be(750m);
        result.Value.Reference.Should().Be("DIST-2026-001");

        (await harness.Db.InsolvencyPayments.CountAsync()).Should().Be(1);

        await harness.Audit.Received(1).RecordAsync(
            "INSOLVENCY.PAYMENT_ADDED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(InsolvencyPayment),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListClaimsAndPayments_OrderByDateAscending()
    {
        var harness = await Harness.CreateAsync();
        var caseSqid = await harness.OpenCaseAsync();

        (await harness.Service.AddClaimAsync(caseSqid, new InsolvencyClaimInputDto(
            200m, "MDL", "Second incurred", Today.AddDays(-5)))).IsSuccess.Should().BeTrue();
        (await harness.Service.AddClaimAsync(caseSqid, new InsolvencyClaimInputDto(
            300m, "MDL", "First incurred", Today.AddDays(-20)))).IsSuccess.Should().BeTrue();

        var claims = await harness.Service.ListClaimsAsync(caseSqid);
        claims.IsSuccess.Should().BeTrue();
        claims.Value.Should().HaveCount(2);
        claims.Value.Should().BeInAscendingOrder(c => c.IncurredOn);

        (await harness.Service.AddPaymentAsync(caseSqid, new InsolvencyPaymentInputDto(
            100m, Today, null))).IsSuccess.Should().BeTrue();
        (await harness.Service.AddPaymentAsync(caseSqid, new InsolvencyPaymentInputDto(
            50m, Today.AddDays(-10), null))).IsSuccess.Should().BeTrue();

        var payments = await harness.Service.ListPaymentsAsync(caseSqid);
        payments.IsSuccess.Should().BeTrue();
        payments.Value.Should().HaveCount(2);
        payments.Value.Should().BeInAscendingOrder(p => p.PaymentDate);
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-insolvency-sub-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public const long ContributorId = 5001L;

        public required CnasDbContext Db { get; init; }
        public required InsolvencyLifecycleService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public string ContributorSqid => $"SQID-{ContributorId}";

        public static async Task<Harness> CreateAsync()
        {
            var db = CreateContext();
            db.Contributors.Add(new Contributor
            {
                Id = ContributorId,
                Idno = "2000000000007",
                IdnoHash = "hash-1",
                Denumire = "Acme SRL",
                RegisteredAtUtc = ClockNow.AddYears(-1),
                IsActive = true,
                CreatedAtUtc = ClockNow.AddYears(-1),
            });
            await db.SaveChangesAsync();

            return Build(db, new StubClock(ClockNow));
        }

        public async Task<string> OpenCaseAsync()
        {
            var open = await Service.OpenAsync(
                ContributorSqid, "Hotărâre judecătorească", Today);
            open.IsSuccess.Should().BeTrue();
            return open.Value.Id;
        }

        private static Harness Build(CnasDbContext db, ICnasTimeProvider clock)
        {
            var sqids = Substitute.For<ISqidService>();
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

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(101L);
            caller.UserSqid.Returns("SQID-101");
            caller.Roles.Returns(["cnas-admin"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-test");

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Result.Success());

            var service = new InsolvencyLifecycleService(db, sqids, clock, caller, audit);
            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
            };
        }
    }
}
