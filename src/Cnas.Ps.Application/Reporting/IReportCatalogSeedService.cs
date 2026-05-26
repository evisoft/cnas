using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reporting;

/// <summary>
/// R1900-R1905 / TOR §13 Annex 6 — admin façade that seeds and refreshes the
/// persisted Annex 6 report catalog from the static
/// <see cref="ReportCatalogDescriptors"/> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated seed service.</b> The Annex 6 dispatcher
/// (<c>ReportingService.Annex6*.cs</c>) declares the implemented codes in
/// code; the persisted <c>cnas.Reports</c> table mirrors them so the catalog
/// endpoint, the search picker and the distribution rules can render the
/// human-readable metadata without round-tripping to the materialiser. The
/// seed service is the bridge between the two: it walks the descriptor table
/// once and upserts every row so an iter-N redeploy automatically catches up.
/// </para>
/// <para>
/// <b>Idempotence.</b> <see cref="RefreshAsync"/> is idempotent — re-running
/// against an up-to-date table reports zero inserts and zero updates. Rows
/// whose code is no longer in the descriptor table are NOT removed (they
/// might still be referenced by historical <c>ReportRun</c> rows or
/// distribution rules); call out unused codes via the
/// <see cref="ListAsync"/> envelope instead.
/// </para>
/// <para>
/// <b>Audit.</b> A successful refresh emits one Critical audit row
/// <c>REPORT_CATALOG.REFRESHED</c> whose detail JSON carries the
/// (<c>Inserted</c>, <c>Updated</c>, <c>Unchanged</c>) totals.
/// </para>
/// </remarks>
public interface IReportCatalogSeedService
{
    /// <summary>Stable audit event code emitted by <see cref="RefreshAsync"/>.</summary>
    public const string AuditCatalogRefreshed = "REPORT_CATALOG.REFRESHED";

    /// <summary>
    /// Reseeds / refreshes the persisted report catalog from the
    /// <see cref="ReportCatalogDescriptors"/> table. The operation is
    /// idempotent; rows whose code matches an existing row are upserted in
    /// place, otherwise inserted. Rows present in the DB but absent from the
    /// descriptor table are left alone.
    /// </summary>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The refresh outcome on success; a failure on DB error.</returns>
    Task<Result<ReportCatalogRefreshResultDto>> RefreshAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every row in the persisted report catalog, ordered by
    /// <c>Code</c>. Caller may filter by category and frequency.
    /// </summary>
    /// <param name="category">Optional category filter (exact match).</param>
    /// <param name="frequency">Optional frequency filter (exact match).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The paged catalog envelope.</returns>
    Task<Result<ReportCatalogPageDto>> ListAsync(
        string? category = null,
        string? frequency = null,
        CancellationToken cancellationToken = default);
}
