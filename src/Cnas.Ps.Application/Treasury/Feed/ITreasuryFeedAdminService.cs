using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — admin façade over the Treasury feed import
/// registry. Hosts the manual-trigger entry, per-import lookups, the rows
/// drill-down, and the list page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b>
/// <list type="bullet">
///   <item><see cref="TriggerManualImportAsync"/> → <c>TREASURY_FEED.MANUAL_IMPORT_STARTED</c> at Critical severity.</item>
///   <item>The importer itself emits <c>TREASURY_FEED.IMPORT_COMPLETED</c> at Information severity.</item>
/// </list>
/// </para>
/// </remarks>
public interface ITreasuryFeedAdminService
{
    /// <summary>Stable audit event code emitted when an admin starts a manual import.</summary>
    public const string AuditManualImportStarted = "TREASURY_FEED.MANUAL_IMPORT_STARTED";

    /// <summary>
    /// Triggers a manual import for <paramref name="feedDate"/>. Emits the
    /// <see cref="AuditManualImportStarted"/> Critical audit row, then defers
    /// to <see cref="ITreasuryFeedImporter.ImportAsync"/> with
    /// <c>TriggerKind=Manual</c>.
    /// </summary>
    /// <param name="feedDate">Calendar date the feed covers.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The compact summary on success; a failed result on validation / configuration miss.</returns>
    Task<Result<TreasuryFeedImportSummaryDto>> TriggerManualImportAsync(
        DateOnly feedDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single import by its Sqid (no rows attached). Returns
    /// <see cref="ErrorCodes.InvalidSqid"/> on a malformed input,
    /// <see cref="ErrorCodes.NotFound"/> on a missing row.
    /// </summary>
    /// <param name="sqid">Sqid-encoded import id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The import summary on success.</returns>
    Task<Result<TreasuryFeedImportDto>> GetImportByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single import together with a filtered, paged subset of its
    /// rows. The page is ordered by <c>RowOrdinal ASC</c>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded import id.</param>
    /// <param name="filter">Row filter + paging envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The details envelope on success.</returns>
    Task<Result<TreasuryFeedImportDetailsDto>> GetImportDetailsAsync(
        string sqid,
        TreasuryFeedImportRowFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists imports matching the filter envelope. Ordered by
    /// <c>StartedAt DESC</c>.
    /// </summary>
    /// <param name="filter">Filter + paging envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The page DTO on success.</returns>
    Task<Result<TreasuryFeedImportPageDto>> ListAsync(
        TreasuryFeedImportFilterDto filter,
        CancellationToken cancellationToken = default);
}
