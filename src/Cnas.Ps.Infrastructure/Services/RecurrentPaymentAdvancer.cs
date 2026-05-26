using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R1000..R1034 / TOR §3.2-Z — production
/// <see cref="IRecurrentPaymentAdvancer"/>. Advances the matching
/// <see cref="RecurrentPaymentSchedule"/> when its
/// <see cref="RecurrentPaymentSchedule.LastDispatchedOrderId"/> matches the
/// confirmed-order id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> When the schedule has already been advanced (i.e.
/// LastDispatchedOrderId no longer points at the confirmed order) the
/// advancer returns success without re-mutating the row. Safe to invoke
/// multiple times from a retried MPay callback.
/// </para>
/// </remarks>
public sealed class RecurrentPaymentAdvancer : IRecurrentPaymentAdvancer
{
    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;

    /// <summary>Constructs the advancer.</summary>
    /// <param name="db">Writer context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    public RecurrentPaymentAdvancer(ICnasDbContext db, ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<Result> AdvanceOnConfirmationAsync(
        long confirmedOrderId,
        CancellationToken cancellationToken = default)
    {
        // The link is one-to-one — at most one schedule points at any given
        // MPay order id. SingleOrDefault is safe because LastDispatchedOrderId
        // is monotonically updated on every new dispatch.
        var schedule = await _db.RecurrentPaymentSchedules
            .FirstOrDefaultAsync(s => s.LastDispatchedOrderId == confirmedOrderId, cancellationToken)
            .ConfigureAwait(false);
        if (schedule is null)
        {
            // The confirmed order does not belong to any recurrent-payment
            // schedule (regular ad-hoc order). No-op success.
            return Result.Success();
        }

        var now = _clock.UtcNow;
        schedule.NextPaymentDate = AdvanceByCadence(schedule.NextPaymentDate, schedule.Cadence);
        schedule.LastPaymentAtUtc = now;
        schedule.FailureCount = 0;
        schedule.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Advances a <see cref="DateOnly"/> by one cadence step.</summary>
    /// <param name="from">Starting date.</param>
    /// <param name="cadence">Cadence enum value.</param>
    /// <returns>The advanced date.</returns>
    private static DateOnly AdvanceByCadence(DateOnly from, RecurrentPaymentCadence cadence)
        => cadence switch
        {
            RecurrentPaymentCadence.Monthly => from.AddMonths(1),
            RecurrentPaymentCadence.Quarterly => from.AddMonths(3),
            RecurrentPaymentCadence.Annual => from.AddMonths(12),
            _ => from.AddMonths(1),
        };
}
