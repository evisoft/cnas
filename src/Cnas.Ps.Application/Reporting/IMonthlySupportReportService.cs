using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reporting;

/// <summary>
/// R2461 / Deliverable 7.1 — service that aggregates monthly support ticket
/// metrics from <c>SupportTicket</c> + <c>SupportTicketSlaEvent</c> rows.
/// Pure-read; consumers run the projection from the admin dashboard's
/// "monthly support report" tile.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bucketing rules.</b> Tickets are included when their
/// <c>SubmittedAt</c> falls inside the requested month [UTC). The breach
/// rates use the <c>FirstResponseBreached</c> / <c>ResolutionBreached</c>
/// SLA event rows whose <c>DetectedAt</c> falls inside the same month and
/// whose parent ticket was submitted in the month. Average resolution
/// minutes are computed only across tickets whose <c>ResolvedAt</c> is
/// non-null and falls inside the month.
/// </para>
/// <para>
/// <b>Filter.</b> When <c>CategoryCodes</c> is non-null/non-empty the
/// query restricts to tickets whose category code matches one of the
/// supplied values. Codes are matched case-sensitively against the stable
/// SCREAMING_SNAKE_CASE <c>SupportTicketCategory.Code</c> column.
/// </para>
/// <para>
/// <b>No persistence side-effects.</b> The implementation runs against
/// the <c>IReadOnlyCnasDbContext</c> seam (routed to the read replica in
/// production) and does NOT emit audit events — this is a snapshot read
/// safe to call from polling dashboards.
/// </para>
/// </remarks>
public interface IMonthlySupportReportService
{
    /// <summary>
    /// Computes the monthly support report for the supplied input.
    /// </summary>
    /// <param name="input">First-of-month UTC date + optional category code filter.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the computed report on
    /// success; <see cref="ErrorCodes.ValidationFailed"/> when the input fails
    /// the FluentValidation guard.
    /// </returns>
    Task<Result<MonthlySupportReportDto>> ComputeAsync(
        MonthlySupportReportInputDto input,
        CancellationToken cancellationToken = default);
}
