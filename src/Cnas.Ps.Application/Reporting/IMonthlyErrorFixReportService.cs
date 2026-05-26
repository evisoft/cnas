using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reporting;

/// <summary>
/// R2462 / Deliverable 7.2 — service that aggregates monthly error-fix /
/// documentation-update metrics from <c>IntegrityCheckFinding</c>,
/// <c>ChangeRequest</c>, and <c>TemplateVariant</c> rows. Pure-read;
/// consumers run the projection from the admin dashboard's "monthly
/// error-fix" tile.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bucketing rules.</b>
/// <list type="bullet">
///   <item>Integrity findings count when their <c>FirstDetectedAt</c> falls in the month.</item>
///   <item>Change requests "rolled back" count when their <c>RolledBackAt</c> falls in the month.</item>
///   <item>Change requests "deployed" count when their <c>DeployedAt</c> falls in the month.</item>
///   <item>Template variant updates count when their <c>UpdatedAtUtc</c> falls in the month.</item>
/// </list>
/// </para>
/// <para>
/// <b>No persistence side-effects.</b> The implementation runs against
/// the <c>IReadOnlyCnasDbContext</c> seam and does NOT emit audit events.
/// </para>
/// </remarks>
public interface IMonthlyErrorFixReportService
{
    /// <summary>
    /// Computes the monthly error-fix / documentation-update report for the supplied input.
    /// </summary>
    /// <param name="input">First-of-month UTC date.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the computed report on
    /// success; <see cref="ErrorCodes.ValidationFailed"/> when the input fails
    /// the FluentValidation guard.
    /// </returns>
    Task<Result<MonthlyErrorFixReportDto>> ComputeAsync(
        MonthlyErrorFixReportInputDto input,
        CancellationToken cancellationToken = default);
}
