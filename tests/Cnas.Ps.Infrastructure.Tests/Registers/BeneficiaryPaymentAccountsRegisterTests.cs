using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Registers;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Registers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Registers;

/// <summary>
/// R1602 / TOR Annex 3.10 — pins the contract of the payment-accounts
/// register projection over <see cref="MPayOrder"/> rows.
/// </summary>
public sealed class BeneficiaryPaymentAccountsRegisterTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-reg-acc-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private static (BeneficiaryPaymentAccountsRegister Sut, CnasDbContext Db, IDeterministicHasher Hasher) Create()
    {
        var db = CreateContext();
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

        var hasher = Substitute.For<IDeterministicHasher>();
        hasher.ComputeHash(Arg.Any<string>())
            .Returns(call => "HASH:" + call.Arg<string>().Trim().ToUpperInvariant());

        return (new BeneficiaryPaymentAccountsRegister(db, sqids, hasher, new StubClock(ClockNow)), db, hasher);
    }

    private static MPayOrder SeedOrder(
        CnasDbContext db,
        string idnp,
        decimal amount,
        DateTime? confirmedAtUtc,
        string? paymentRef = null)
    {
        var order = new MPayOrder
        {
            CreatedAtUtc = ClockNow,
            OrderId = $"O-{Guid.NewGuid():N}",
            AmountMdl = amount,
            DescriptionRo = "Pensie lunară",
            BeneficiaryIdnp = idnp,
            PaymentRef = paymentRef,
            ConfirmedAtUtc = confirmedAtUtc,
            IsActive = true,
        };
        db.MPayOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>Happy path — every MPay row surfaces as a register row.</summary>
    [Fact]
    public async Task ListAsync_HappyPath_ReturnsActiveRows()
    {
        var (sut, db, _) = Create();
        SeedOrder(db, "2000000000001", 500m, ClockNow.AddDays(-1), "txn-1");
        SeedOrder(db, "2000000000002", 700m, null);

        var result = await sut.ListAsync(beneficiaryIdnp: null, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
    }

    /// <summary>Filter by raw IDNP — narrows by deterministic-hash shadow.</summary>
    [Fact]
    public async Task ListAsync_BeneficiaryFilter_NarrowsByIdnpHash()
    {
        var (sut, db, hasher) = Create();
        SeedOrder(db, "2000000000001", 500m, ClockNow.AddDays(-1), "txn-1");
        SeedOrder(db, "2000000000002", 700m, ClockNow);

        var result = await sut.ListAsync(beneficiaryIdnp: "2000000000001", page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        hasher.Received().ComputeHash("2000000000001");
    }

    /// <summary>IBAN — projected as a masked string per SEC 035.</summary>
    [Fact]
    public async Task ListAsync_IbanIsMasked()
    {
        var (sut, db, _) = Create();
        SeedOrder(db, "2000000000001", 100m, ClockNow);

        var result = await sut.ListAsync(beneficiaryIdnp: null, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        // Stub IBAN is built deterministically from the order id; the masker
        // MUST keep the country prefix (first 4 chars) + last 4 chars and replace
        // the middle with asterisks.
        result.Value.Items[0].Iban.Should().Contain("*", "the SEC 035 masker must redact the middle portion of the IBAN");
    }

    /// <summary>Ordering — newest confirmed payment first.</summary>
    [Fact]
    public async Task ListAsync_OrdersByLastPaymentAtUtcDesc()
    {
        var (sut, db, _) = Create();
        var older = SeedOrder(db, "2000000000001", 100m, ClockNow.AddDays(-3), "older");
        var newer = SeedOrder(db, "2000000000002", 200m, ClockNow, "newer");
        // Add an unconfirmed row — should appear last (NULLS LAST).
        SeedOrder(db, "2000000000003", 300m, null);

        var result = await sut.ListAsync(beneficiaryIdnp: null, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].Sqid.Should().Be($"SQID-{newer.Id}");
        result.Value.Items[1].Sqid.Should().Be($"SQID-{older.Id}");
    }
}
