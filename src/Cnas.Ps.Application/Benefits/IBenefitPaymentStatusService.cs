using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Benefits;

/// <summary>
/// R0517 / TOR CF 02.05 — citizen "status of benefit payments" service. Drives
/// two authenticated endpoints:
/// <list type="bullet">
///   <item>Self-service <c>GET /api/self-service/benefit-payments?fromMonth=&amp;toMonth=&amp;type=</c>
///   for the caller's own ledger (resolved server-side via
///   <c>ICallerContext</c>).</item>
///   <item>Admin / utilizator-autorizat
///   <c>GET /api/admin/benefit-payments/{solicitantSqid}?fromMonth=&amp;toMonth=&amp;type=</c>
///   gated by the <c>BenefitPayment.ReadAny</c> permission.</item>
/// </list>
/// Both surfaces share the same aggregation logic so the wire shape stays
/// consistent regardless of who reads the status payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Window contract.</b> When the caller omits both <c>FromMonth</c> and
/// <c>ToMonth</c>, the service substitutes the default 12-month-history +
/// 3-month-look-ahead window relative to the server clock. When the caller
/// supplies one bound, the service substitutes the matching half of the
/// default for the missing bound. The validator enforces FromMonth ≤ ToMonth
/// and a total window of at most 36 months.
/// </para>
/// <para>
/// <b>Totals projection.</b> Two roll-ups travel alongside the row list:
/// <c>TotalPaidLast12Months</c> sums every <c>Status = Paid</c> row whose
/// month falls inside the last 12 calendar months (independent of the
/// requested window), and <c>TotalScheduledNext3Months</c> sums every
/// <c>Status = Scheduled</c> row in the next 3 calendar months. The window
/// independence keeps the rolling totals comparable across calls.
/// </para>
/// <para>
/// <b>Audit.</b> Every successful invocation writes one Sensitive-severity
/// <c>BENEFIT_PAYMENT.READ</c> audit row carrying the solicitant's Sqid +
/// number of rows returned + cumulative paid total. The Sensitive severity
/// matches the citizen-financial-data classification.
/// </para>
/// </remarks>
public interface IBenefitPaymentStatusService
{
    /// <summary>
    /// Resolves the caller's Solicitant via the existing UserProfile→Solicitant
    /// identity link and returns the benefit-payment status payload for that
    /// Solicitant. The query envelope's window + type filter are applied; an
    /// omitted filter falls back to its documented default.
    /// </summary>
    /// <param name="query">Query envelope — see <see cref="BenefitPaymentStatusQueryDto"/>.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated <see cref="BenefitPaymentStatusDto"/>; when
    /// the caller is anonymous <see cref="ErrorCodes.Unauthorized"/>; when the
    /// caller's user row carries no matching Solicitant
    /// <see cref="ErrorCodes.NotFound"/>; when the query envelope fails
    /// validation <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<BenefitPaymentStatusDto>> GetForCurrentUserAsync(
        BenefitPaymentStatusQueryDto query,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the benefit-payment status payload for the supplied Solicitant
    /// id. Restricted to callers holding the <c>BenefitPayment.ReadAny</c>
    /// permission (administrator / utilizator-autorizat). Useful for
    /// back-office assistance, inspections, and dispute resolution.
    /// </summary>
    /// <param name="solicitantId">Raw bigint id of the target Solicitant.</param>
    /// <param name="query">Query envelope — see <see cref="BenefitPaymentStatusQueryDto"/>.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated <see cref="BenefitPaymentStatusDto"/>; when
    /// the caller lacks the permission <see cref="ErrorCodes.Forbidden"/>;
    /// when the query envelope fails validation
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<BenefitPaymentStatusDto>> GetForSolicitantAsync(
        long solicitantId,
        BenefitPaymentStatusQueryDto query,
        CancellationToken ct = default);
}
