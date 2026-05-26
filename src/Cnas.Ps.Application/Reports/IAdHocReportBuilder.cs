using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reports;

/// <summary>
/// R0580 / TOR CF 09.02 — thin ad-hoc report builder service. Accepts an
/// inline <see cref="AdHocReportSpecDto"/> and projects the requested entity
/// set through filters / column selection / ordering, returning the result
/// rows as a dynamic-shape <see cref="AdHocReportResultDto"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Difference from <see cref="IReportTemplateService"/>.</b> Saved
/// templates are first-class durable rows that can be re-executed via
/// <see cref="IReportEngine"/>. The ad-hoc builder is a one-shot path —
/// the spec lives only in the request body and the result is returned
/// inline.
/// </para>
/// <para>
/// <b>Hard cap.</b> The builder enforces a fixed cap (10 000 rows). When
/// the projected row count exceeds the cap the builder refuses with
/// <see cref="ErrorCodes.AdHocReportTooLarge"/>; the caller must narrow
/// the filter and retry. Unlike <see cref="IReportEngine"/> the ad-hoc
/// path is not gated by the per-registry query budget — the cap is a
/// blunter instrument that fits the inline / interactive use case.
/// </para>
/// <para>
/// <b>Supported entity sets.</b> Today the builder supports
/// <c>Applications</c>, <c>Contributors</c>, <c>Dossiers</c>, and
/// <c>Decisions</c>. Unknown entity sets short-circuit at submission time
/// with <see cref="ErrorCodes.AdHocReportUnknownEntity"/>.
/// </para>
/// </remarks>
public interface IAdHocReportBuilder
{
    /// <summary>Hard cap on the row count returned by a single ad-hoc run.</summary>
    public const int MaxRowsPerRun = 10_000;

    /// <summary>
    /// Builds the ad-hoc report. Returns the materialised rows on success;
    /// a stable failure code otherwise.
    /// </summary>
    /// <param name="spec">The ad-hoc report specification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the materialised rows. On failure one of
    /// <see cref="ErrorCodes.AdHocReportTooLarge"/>,
    /// <see cref="ErrorCodes.AdHocReportUnknownEntity"/>,
    /// <see cref="ErrorCodes.AdHocReportUnknownColumn"/>, or
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<AdHocReportResultDto>> BuildAsync(AdHocReportSpecDto spec, CancellationToken ct = default);
}
