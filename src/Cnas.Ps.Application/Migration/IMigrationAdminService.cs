using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Migration;

/// <summary>
/// R2430 / R2431 / R2433 / TOR M4 — admin façade over the migration
/// registry. Hosts the manual-trigger entry, per-run lookups + drill-down,
/// findings worklist + acknowledgement, and the reconciliation report.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b>
/// <list type="bullet">
///   <item><see cref="TriggerManualImportAsync"/> → <c>MIGRATION.MANUAL_IMPORT_STARTED</c> at Critical severity.</item>
///   <item><see cref="AcknowledgeFindingAsync"/> → <c>MIGRATION.FINDING_ACKNOWLEDGED</c> at Sensitive severity.</item>
/// </list>
/// </para>
/// </remarks>
public interface IMigrationAdminService
{
    /// <summary>Stable audit code emitted when an admin starts a manual import.</summary>
    public const string AuditManualImportStarted = "MIGRATION.MANUAL_IMPORT_STARTED";

    /// <summary>Stable audit code emitted when an admin acknowledges a finding.</summary>
    public const string AuditFindingAcknowledged = "MIGRATION.FINDING_ACKNOWLEDGED";

    /// <summary>
    /// Triggers a manual import for <paramref name="planSqid"/>. When
    /// <paramref name="dryRun"/> is true the run completes with all staging
    /// rows left uncommitted; when false the rows are committed.
    /// </summary>
    /// <param name="planSqid">Sqid-encoded plan id.</param>
    /// <param name="dryRun">When true triggers a DryRun; when false an Apply.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Run summary on success.</returns>
    Task<Result<MigrationRunSummaryDto>> TriggerManualImportAsync(
        string planSqid,
        bool dryRun,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a run by Sqid (no findings attached).</summary>
    /// <param name="runSqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The run DTO on success.</returns>
    Task<Result<MigrationRunDto>> GetRunByIdAsync(
        string runSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a run + paged findings + all batches.</summary>
    /// <param name="runSqid">Sqid-encoded run id.</param>
    /// <param name="filter">Findings page filter envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The details DTO on success.</returns>
    Task<Result<MigrationRunDetailsDto>> GetRunDetailsAsync(
        string runSqid,
        MigrationRunDetailsFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>Lists runs matching <paramref name="filter"/>, ordered by StartedAt DESC.</summary>
    /// <param name="filter">Filter + paging envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Page DTO on success.</returns>
    Task<Result<MigrationRunPageDto>> ListRunsAsync(
        MigrationRunFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledges a finding.</summary>
    /// <param name="findingSqid">Sqid-encoded finding id.</param>
    /// <param name="input">Acknowledgement payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The updated finding DTO on success.</returns>
    Task<Result<MigrationFindingDto>> AcknowledgeFindingAsync(
        string findingSqid,
        MigrationFindingAcknowledgeInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Lists findings matching <paramref name="filter"/>.</summary>
    /// <param name="filter">Filter + paging envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Page DTO on success.</returns>
    Task<Result<MigrationFindingPageDto>> ListFindingsAsync(
        MigrationFindingFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches the reconciliation report for a run.</summary>
    /// <param name="runSqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The reconciliation DTO on success.</returns>
    Task<Result<ReconciliationReportDto>> GetReconciliationAsync(
        string runSqid,
        CancellationToken cancellationToken = default);
}
