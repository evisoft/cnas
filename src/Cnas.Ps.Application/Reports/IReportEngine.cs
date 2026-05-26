using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reports;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — execution half of the ad-hoc report builder.
/// Materialises a <see cref="ReportTemplateDto"/> against the live registry, paging
/// through the result rows or rendering them as a downloadable export via the R0226
/// universal grid-export pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Budget gating.</b> Every run is gated by the R0167
/// <c>IQueryBudgetService</c> — the engine builds the filter-applied
/// <see cref="IQueryable{T}"/>, asks the budget guard whether the row count fits,
/// and only materialises when the verdict is positive. A refusal returns
/// <see cref="ErrorCodes.QueryTooBroad"/>.
/// </para>
/// <para>
/// <b>Run history.</b> Every call writes a <c>ReportRun</c> row capturing the
/// outcome, the row count, and the wall-clock duration. The audit subsystem
/// receives a <c>REPORT.EXECUTED</c> entry with the template Sqid + outcome +
/// duration.
/// </para>
/// <para>
/// <b>Group-by.</b> When the template has a non-null
/// <see cref="ReportTemplateDto.GroupByField"/> the engine emits one row per
/// distinct value with a synthetic <c>"count"</c> aggregate column. Richer
/// aggregates (sum/avg/min/max) are deferred to a future batch.
/// </para>
/// </remarks>
public interface IReportEngine
{
    /// <summary>
    /// Executes the template and returns a single paged result set.
    /// </summary>
    /// <param name="templateId">Internal id of the template to run.</param>
    /// <param name="skip">Number of rows to skip before returning the page (≥ 0).</param>
    /// <param name="take">Page size (clamped to <c>[1, 200]</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The executed page or a failure with a stable error code.</returns>
    Task<Result<ReportExecutionResultDto>> RunAsync(long templateId, int skip, int take, CancellationToken ct = default);

    /// <summary>
    /// Executes the template and renders the entire result set as a downloadable
    /// file in the requested format via the R0226 universal grid exporter.
    /// </summary>
    /// <param name="templateId">Internal id of the template to run.</param>
    /// <param name="format">Output format — CSV / XLSX / PDF.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the rendered bytes. On failure a <see cref="Result{T}.Failure"/>
    /// carrying one of <see cref="ErrorCodes.QueryTooBroad"/>,
    /// <see cref="ErrorCodes.ExportTooLarge"/>,
    /// <see cref="ErrorCodes.ExportFormatNotSupported"/>,
    /// <see cref="ErrorCodes.NotFound"/>, or <see cref="ErrorCodes.Forbidden"/>.
    /// </returns>
    Task<Result<byte[]>> ExportAsync(long templateId, ExportFormat format, CancellationToken ct = default);
}
