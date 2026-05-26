using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R1504 / TOR §3.7-E — CNAS-initiated payment suspend / resume lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Suspend.</b> Mints a <c>Document</c> row via the
/// <c>DecizieSuspendarePlataTemplate</c>, flips every active
/// <c>MPayOrder</c> bound to the same beneficiary into the suspended state,
/// writes a Critical <c>PAYMENT.SUSPENDED</c> audit row, and fires the citizen
/// ActionResult notification.
/// </para>
/// <para>
/// <b>Resume.</b> Mints a <c>Document</c> row via the
/// <c>DispozitieReluareTemplate</c>, flips the suspended <c>MPayOrder</c> rows
/// back to Active, writes a Critical <c>PAYMENT.RESUMED</c> audit row, and
/// fires the resume notification.
/// </para>
/// </remarks>
public interface IPaymentSuspensionService
{
    /// <summary>
    /// Suspends payments against the supplied prior decision.
    /// </summary>
    /// <param name="decisionSqid">Sqid-encoded id of the prior decision (ServiceApplication).</param>
    /// <param name="reason">Free-text justification (3-500 chars).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the freshly-minted suspension row;
    /// <see cref="Result{T}.Failure"/> with <see cref="ErrorCodes.NotFound"/> when
    /// the decision is unknown, <see cref="ErrorCodes.Conflict"/> when payments
    /// are already suspended, <see cref="ErrorCodes.ValidationFailed"/> on bad input.
    /// </returns>
    Task<Result<PaymentSuspensionDto>> SuspendAsync(
        string decisionSqid,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes payments against the suspended decision.
    /// </summary>
    /// <param name="suspensionSqid">Sqid-encoded id of the prior <c>PaymentSuspensionRecord</c>.</param>
    /// <param name="reason">Free-text justification for the resume (3-500 chars).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the updated suspension row;
    /// <see cref="Result{T}.Failure"/> with <see cref="ErrorCodes.NotFound"/> when
    /// the suspension is unknown, <see cref="ErrorCodes.Conflict"/> when already resumed,
    /// <see cref="ErrorCodes.ValidationFailed"/> on bad input.
    /// </returns>
    Task<Result<PaymentSuspensionDto>> ResumeAsync(
        string suspensionSqid,
        string reason,
        CancellationToken cancellationToken = default);
}
