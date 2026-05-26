using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// EF Core-backed implementation of <see cref="IMPayOrderStore"/>. Owns the three write
/// paths that make MPay callbacks idempotent (CLAUDE.md cross-cutting
/// "Idempotent Callbacks", red-flag #15): <see cref="GetByOrderIdAsync"/>,
/// <see cref="ConfirmAsync"/>, and <see cref="CreateAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: registered as <c>Scoped</c> in
/// <c>InfrastructureServiceCollectionExtensions.AddCnasInfrastructure</c> because the
/// EF Core <see cref="ICnasDbContext"/> dependency is scoped (per-request) and tracks
/// changes within a single unit of work. The clock dependency is a singleton; no per-
/// instance state is held.
/// </para>
/// <para>
/// Logging discipline: this store NEVER logs the <see cref="MPayOrder.BeneficiaryIdnp"/>
/// field (PII per TOR SEC 035). The fields it does log via structured logging on the
/// controller side are <c>OrderId</c> (an opaque business identifier) and
/// <c>PaymentRef</c> (an upstream bank transaction id, not a citizen identifier).
/// </para>
/// </remarks>
/// <param name="db">Scoped EF Core context — owns the per-request unit of work.</param>
/// <param name="clock">Clock provider; supplies <see cref="ICnasTimeProvider.UtcNow"/> for the audit timestamps.</param>
public sealed class MPayOrderStore(
    ICnasDbContext db,
    ICnasTimeProvider clock) : IMPayOrderStore
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;

    /// <inheritdoc />
    public async Task<MPayOrderSnapshot?> GetByOrderIdAsync(string orderId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        // The CnasDbContext does not declare a global soft-delete query filter (existing
        // services filter on IsActive explicitly — see DataSearchService, FailedJobStore).
        // Apply the same convention here so soft-deleted rows look like "not found" to
        // the controller, which is the right behaviour for the inbound callback surface.
        var entity = await _db.MPayOrders
            .AsNoTracking()
            .Where(o => o.IsActive)
            .FirstOrDefaultAsync(o => o.OrderId == orderId, ct)
            .ConfigureAwait(false);

        return entity is null
            ? null
            : new MPayOrderSnapshot(
                OrderId: entity.OrderId,
                AmountMdl: entity.AmountMdl,
                DescriptionRo: entity.DescriptionRo,
                BeneficiaryIdnp: entity.BeneficiaryIdnp,
                PaymentRef: entity.PaymentRef,
                ConfirmedAtUtc: entity.ConfirmedAtUtc);
    }

    /// <inheritdoc />
    public async Task<Result> ConfirmAsync(
        string orderId,
        string paymentRef,
        DateTime confirmedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentRef);

        var entity = await _db.MPayOrders
            .Where(o => o.IsActive)
            .FirstOrDefaultAsync(o => o.OrderId == orderId, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                "No MPay order matches the supplied order id.");
        }

        // First-time confirmation: PaymentRef is still null, so stamp it.
        if (entity.PaymentRef is null)
        {
            entity.PaymentRef = paymentRef;
            entity.ConfirmedAtUtc = confirmedAtUtc;
            entity.UpdatedAtUtc = _clock.UtcNow;
            try
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Result.Success();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Two concurrent callbacks raced to stamp PaymentRef. The
                // PaymentRef column carries IsConcurrencyToken so the loser
                // lands here. Drop the stale snapshot, reload the row, and
                // re-evaluate against the winning peer's stamp.
                if (_db is DbContext concrete)
                {
                    concrete.ChangeTracker.Clear();
                }
                var reloaded = await _db.MPayOrders
                    .Where(o => o.IsActive)
                    .FirstOrDefaultAsync(o => o.OrderId == orderId, ct)
                    .ConfigureAwait(false);
                if (reloaded is null)
                {
                    return Result.Failure(
                        ErrorCodes.NotFound,
                        "No MPay order matches the supplied order id.");
                }
                // The row is now confirmed by the winner; idempotent vs
                // conflicting replay decision below.
                if (reloaded.PaymentRef is not null
                    && string.Equals(reloaded.PaymentRef, paymentRef, StringComparison.Ordinal))
                {
                    return Result.Success();
                }
                return Result.Failure(
                    ErrorCodes.Conflict,
                    "Order is already confirmed with a different payment reference.");
            }
        }

        // Idempotent replay: the row is already confirmed with the same payment
        // reference — return success without an additional DB write. CLAUDE.md
        // cross-cutting "Idempotent Callbacks".
        if (string.Equals(entity.PaymentRef, paymentRef, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        // Conflicting replay: a different paymentRef on an already-confirmed row.
        // NEVER silently overwrite — surface the divergence so operators can investigate
        // (a third party trying to spoof a payment, or an upstream MPay retry that
        // changed the reference unexpectedly).
        return Result.Failure(
            ErrorCodes.Conflict,
            "Order is already confirmed with a different payment reference.");
    }

    /// <inheritdoc />
    public async Task<Result> CreateAsync(MPayOrderSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Guard against the natural-key collision at the service layer so callers do not
        // have to inspect EF-specific exceptions. The database unique index remains in
        // place as a defence-in-depth safety net — a race between two CreateAsync calls
        // for the same OrderId would resolve at SaveChanges time via DbUpdateException;
        // covering that path is out of scope for this iteration because the outbound
        // PostOrderAsync wrapper is the only producer.
        var existing = await _db.MPayOrders
            .AsNoTracking()
            .Where(o => o.IsActive)
            .AnyAsync(o => o.OrderId == snapshot.OrderId, ct)
            .ConfigureAwait(false);
        if (existing)
        {
            return Result.Failure(
                ErrorCodes.Conflict,
                "An MPay order with this order id already exists.");
        }

        var now = _clock.UtcNow;
        _db.MPayOrders.Add(new MPayOrder
        {
            OrderId = snapshot.OrderId,
            AmountMdl = snapshot.AmountMdl,
            DescriptionRo = snapshot.DescriptionRo,
            BeneficiaryIdnp = snapshot.BeneficiaryIdnp,
            PaymentRef = snapshot.PaymentRef,
            ConfirmedAtUtc = snapshot.ConfirmedAtUtc,
            CreatedAtUtc = now,
            IsActive = true,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }
}
