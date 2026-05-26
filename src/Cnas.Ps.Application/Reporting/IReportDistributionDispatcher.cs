using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reporting;

/// <summary>
/// R1906 / TOR Annex 6 — orchestrator that fans a finalised report run out
/// across every active <c>ReportDistributionRule</c> matching the supplied
/// report code. Persists one
/// <c>ReportDistributionDispatch</c> row per consulted rule and returns a
/// terminal-status summary so the caller (job, in-band service, ...) can
/// chart per-call delivery outcomes without re-reading the dispatch table.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-band invocation.</b> The dispatcher is invoked inside the
/// originating request / job — there is NO separate Quartz job in this
/// iteration. The report-engine code path calls
/// <see cref="DispatchAsync"/> right after persisting the report run; the
/// dispatcher's own work is bounded by the rule count for the code, which
/// is small (≤ a handful per report code in practice).
/// </para>
/// <para>
/// <b>Failure semantics.</b> Channel-handler failures NEVER throw out of
/// <see cref="DispatchAsync"/> — they are caught by the dispatcher, mapped
/// to a sanitised <c>FailureReason</c>, and persisted on the dispatch row.
/// The dispatcher itself only returns <see cref="Result{T}.Failure"/> when
/// the input fails validation OR the read of active rules itself fails.
/// </para>
/// </remarks>
public interface IReportDistributionDispatcher
{
    /// <summary>
    /// Fans a finalised report run out across every active rule for the
    /// supplied <paramref name="input"/>'s <c>ReportCode</c>. For each
    /// matching rule the dispatcher resolves recipients, invokes the
    /// appropriate channel handler, persists a
    /// <c>ReportDistributionDispatch</c> row, and accumulates the outcome
    /// into the returned summary.
    /// </summary>
    /// <param name="input">Dispatch envelope describing the report run.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the per-outcome counters on success;
    /// <see cref="ErrorCodes.ValidationFailed"/> when the input is malformed.
    /// </returns>
    Task<Result<ReportDistributionDispatchSummaryDto>> DispatchAsync(
        ReportDispatchInputDto input,
        CancellationToken cancellationToken = default);
}
