using System;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for <see cref="MPayOrderStore"/> — the EF-backed persistence façade for
/// <see cref="MPayOrder"/> rows. Verifies the three idempotency-critical contracts
/// documented on <see cref="IMPayOrderStore"/>:
/// <list type="bullet">
///   <item>Happy-path confirmation — first call stamps PaymentRef + ConfirmedAtUtc.</item>
///   <item>Idempotent replay — second call with the SAME paymentRef is a no-op success.</item>
///   <item>Conflicting replay — second call with a DIFFERENT paymentRef returns
///   <see cref="ErrorCodes.Conflict"/> (never silently overwrites).</item>
/// </list>
/// Mirrors the harness pattern in <see cref="DataSearchServiceTests"/>: a unique
/// EF Core InMemory database name per test, plus NSubstitute-substituted
/// <see cref="ICnasTimeProvider"/> so the audit timestamps are deterministic.
/// </summary>
public sealed class MPayOrderStoreTests
{
    /// <summary>Deterministic clock instant — keeps audit fields stable across runs.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── GetByOrderIdAsync ───────────────────────

    /// <summary>
    /// Read miss: an unknown <c>orderId</c> must return <c>null</c> (not throw, not
    /// fabricate a zero-amount placeholder). The controller branches on this null to
    /// produce a 404.
    /// </summary>
    [Fact]
    public async Task GetByOrderIdAsync_UnknownOrderId_ReturnsNull()
    {
        var harness = Harness.Create();

        var result = await harness.Store.GetByOrderIdAsync("ORD-UNKNOWN");

        result.Should().BeNull();
    }

    /// <summary>
    /// Read hit: a seeded row is projected into the snapshot record verbatim, including
    /// the pending-state nulls on PaymentRef / ConfirmedAtUtc.
    /// </summary>
    [Fact]
    public async Task GetByOrderIdAsync_KnownOrderId_ReturnsSnapshot()
    {
        var harness = Harness.Create();
        await harness.SeedOrderAsync(
            orderId: "ORD-A",
            amountMdl: 1234.56m,
            descriptionRo: "Plata contributii",
            beneficiaryIdnp: "2000000000007");

        var result = await harness.Store.GetByOrderIdAsync("ORD-A");

        result.Should().NotBeNull();
        result!.OrderId.Should().Be("ORD-A");
        result.AmountMdl.Should().Be(1234.56m);
        result.DescriptionRo.Should().Be("Plata contributii");
        result.BeneficiaryIdnp.Should().Be("2000000000007");
        result.PaymentRef.Should().BeNull();
        result.ConfirmedAtUtc.Should().BeNull();
    }

    // ─────────────────────── ConfirmAsync — happy path ───────────────────────

    /// <summary>
    /// First-time confirmation: stamps PaymentRef + ConfirmedAtUtc + UpdatedAtUtc on the
    /// row and returns <see cref="Result.Success()"/>. The persisted state must match the
    /// inputs exactly (no rounding, no kind-juggling on the timestamp).
    /// </summary>
    [Fact]
    public async Task ConfirmAsync_FirstTime_PersistsPaymentRefAndConfirmedAt()
    {
        var harness = Harness.Create();
        await harness.SeedOrderAsync(orderId: "ORD-B");
        var confirmedAt = new DateTime(2026, 5, 20, 11, 30, 0, DateTimeKind.Utc);

        var result = await harness.Store.ConfirmAsync("ORD-B", "BANK-REF-1", confirmedAt);

        result.IsSuccess.Should().BeTrue();
        var persisted = await harness.Db.MPayOrders.SingleAsync(o => o.OrderId == "ORD-B");
        persisted.PaymentRef.Should().Be("BANK-REF-1");
        persisted.ConfirmedAtUtc.Should().Be(confirmedAt);
        persisted.UpdatedAtUtc.Should().Be(ClockNow);
    }

    // ─────────────────────── ConfirmAsync — idempotent replay ───────────────────────

    /// <summary>
    /// Replaying the same <c>(orderId, paymentRef, confirmedAtUtc)</c> tuple must be a
    /// no-op success — both calls return <see cref="Result.Success()"/>, and the row's
    /// confirmed state is identical to the first call (no clock drift, no duplicate row).
    /// CLAUDE.md cross-cutting "Idempotent Callbacks".
    /// </summary>
    [Fact]
    public async Task ConfirmAsync_SamePaymentRefTwice_IsIdempotent()
    {
        var harness = Harness.Create();
        await harness.SeedOrderAsync(orderId: "ORD-C");
        var confirmedAt = new DateTime(2026, 5, 20, 11, 30, 0, DateTimeKind.Utc);

        var first = await harness.Store.ConfirmAsync("ORD-C", "BANK-REF-IDEMPO", confirmedAt);
        var second = await harness.Store.ConfirmAsync("ORD-C", "BANK-REF-IDEMPO", confirmedAt);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        // Exactly one matching row — the second call did not insert a duplicate.
        (await harness.Db.MPayOrders.CountAsync(o => o.OrderId == "ORD-C")).Should().Be(1);
        var persisted = await harness.Db.MPayOrders.SingleAsync(o => o.OrderId == "ORD-C");
        persisted.PaymentRef.Should().Be("BANK-REF-IDEMPO");
        persisted.ConfirmedAtUtc.Should().Be(confirmedAt);
    }

    // ─────────────────────── ConfirmAsync — conflicting replay ───────────────────────

    /// <summary>
    /// A retried confirm with a DIFFERENT <c>paymentRef</c> on an already-confirmed row
    /// must NOT silently overwrite the prior confirmation. The store returns
    /// <see cref="ErrorCodes.Conflict"/> so the controller can map to HTTP 409 and let
    /// operators investigate the divergence.
    /// </summary>
    [Fact]
    public async Task ConfirmAsync_DifferentPaymentRefOnConfirmedRow_ReturnsConflict()
    {
        var harness = Harness.Create();
        await harness.SeedOrderAsync(orderId: "ORD-D");
        var firstConfirmedAt = new DateTime(2026, 5, 20, 11, 30, 0, DateTimeKind.Utc);
        var secondConfirmedAt = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

        var first = await harness.Store.ConfirmAsync("ORD-D", "BANK-REF-ORIGINAL", firstConfirmedAt);
        var second = await harness.Store.ConfirmAsync("ORD-D", "BANK-REF-OTHER", secondConfirmedAt);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeFalse();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
        // Original confirmation preserved — no overwrite.
        var persisted = await harness.Db.MPayOrders.SingleAsync(o => o.OrderId == "ORD-D");
        persisted.PaymentRef.Should().Be("BANK-REF-ORIGINAL");
        persisted.ConfirmedAtUtc.Should().Be(firstConfirmedAt);
    }

    // ─────────────────────── ConfirmAsync — unknown order ───────────────────────

    /// <summary>
    /// Confirming an order id that has no row returns
    /// <see cref="ErrorCodes.NotFound"/> — the controller maps to 404. Distinct from
    /// the conflict case so operators dashboards can see "MPay confirmed a payment we
    /// never originated" as a separate signal.
    /// </summary>
    [Fact]
    public async Task ConfirmAsync_UnknownOrderId_ReturnsNotFound()
    {
        var harness = Harness.Create();

        var result = await harness.Store.ConfirmAsync(
            "ORD-DOES-NOT-EXIST",
            "BANK-REF",
            new DateTime(2026, 5, 20, 11, 30, 0, DateTimeKind.Utc));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────────── CreateAsync ───────────────────────

    /// <summary>
    /// Creating a new pending order inserts the row with PaymentRef = null and
    /// ConfirmedAtUtc = null and stamps CreatedAtUtc from the clock provider.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NewOrder_InsertsPendingRow()
    {
        var harness = Harness.Create();
        var snapshot = new MPayOrderSnapshot(
            OrderId: "ORD-NEW",
            AmountMdl: 100.00m,
            DescriptionRo: "Plata test",
            BeneficiaryIdnp: "2000000000007",
            PaymentRef: null,
            ConfirmedAtUtc: null);

        var result = await harness.Store.CreateAsync(snapshot);

        result.IsSuccess.Should().BeTrue();
        var persisted = await harness.Db.MPayOrders.SingleAsync(o => o.OrderId == "ORD-NEW");
        persisted.AmountMdl.Should().Be(100.00m);
        persisted.PaymentRef.Should().BeNull();
        persisted.ConfirmedAtUtc.Should().BeNull();
        persisted.CreatedAtUtc.Should().Be(ClockNow);
        persisted.IsActive.Should().BeTrue();
    }

    /// <summary>
    /// Re-inserting a row with an existing OrderId returns
    /// <see cref="ErrorCodes.Conflict"/> — the natural-key uniqueness invariant is owned
    /// by the store so callers do not need to inspect EF-specific exceptions.
    /// </summary>
    [Fact]
    public async Task CreateAsync_DuplicateOrderId_ReturnsConflict()
    {
        var harness = Harness.Create();
        await harness.SeedOrderAsync(orderId: "ORD-DUP");

        var result = await harness.Store.CreateAsync(new MPayOrderSnapshot(
            OrderId: "ORD-DUP",
            AmountMdl: 1m,
            DescriptionRo: "x",
            BeneficiaryIdnp: "2000000000007",
            PaymentRef: null,
            ConfirmedAtUtc: null));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-mpayorder-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Bundles the SUT and its collaborators so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required MPayOrderStore Store { get; init; }

        /// <summary>Wires up the SUT with NSubstitute fakes and a fresh InMemory DB.</summary>
        public static Harness Create()
        {
            var db = CreateContext();
            var clock = Substitute.For<ICnasTimeProvider>();
            clock.UtcNow.Returns(ClockNow);
            var store = new MPayOrderStore(db, clock);
            return new Harness { Db = db, Store = store };
        }

        /// <summary>Inserts a pending (unconfirmed) <see cref="MPayOrder"/> with sane defaults.</summary>
        public async Task<MPayOrder> SeedOrderAsync(
            string orderId,
            decimal amountMdl = 100.00m,
            string descriptionRo = "(seed)",
            string beneficiaryIdnp = "2000000000007")
        {
            var entity = new MPayOrder
            {
                OrderId = orderId,
                AmountMdl = amountMdl,
                DescriptionRo = descriptionRo,
                BeneficiaryIdnp = beneficiaryIdnp,
                PaymentRef = null,
                ConfirmedAtUtc = null,
                CreatedAtUtc = ClockNow.AddMinutes(-5),
                IsActive = true,
            };
            Db.MPayOrders.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }
    }
}
