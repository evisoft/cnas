using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R1000..R1034 / TOR §3.2-Z — application-layer seam invoked by the MPay
/// callback handler when an order belonging to a recurrent-payment schedule
/// is confirmed. Advances the matching schedule's
/// <c>NextPaymentDate</c> by one cadence step and clears the failure counter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seam?</b> The MPay callback handler lives in the API layer and
/// doesn't know about <c>RecurrentPaymentSchedule</c> internals. Punching the
/// schedule advance through this interface keeps the callback handler thin
/// and lets the test pyramid stub the advance step independently of the
/// rest of the recurrent-payment subsystem.
/// </para>
/// <para>
/// <b>Idempotency.</b> The advancer is safe to call multiple times for the
/// same confirmed-order id — the second invocation observes the
/// already-advanced schedule (its <c>LastDispatchedOrderId</c> no longer
/// matches) and is a no-op.
/// </para>
/// </remarks>
public interface IRecurrentPaymentAdvancer
{
    /// <summary>
    /// Advances the recurrent-payment schedule whose
    /// <c>LastDispatchedOrderId</c> matches <paramref name="confirmedOrderId"/>.
    /// </summary>
    /// <param name="confirmedOrderId">Database id of the MPay order whose callback just confirmed it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when an advance was applied or the
    /// confirmed order is not linked to any schedule (no-op success).
    /// Failure when the database write fails.
    /// </returns>
    Task<Result> AdvanceOnConfirmationAsync(long confirmedOrderId, CancellationToken cancellationToken = default);
}
