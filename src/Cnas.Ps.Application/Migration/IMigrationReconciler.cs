using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Migration;

/// <summary>
/// R2433 / TOR M4 — produces a <c>ReconciliationReport</c> for a
/// migration run by comparing source counts + fingerprints with the
/// persisted staging-row counts + fingerprints.
/// </summary>
public interface IMigrationReconciler
{
    /// <summary>Stable audit code emitted on a successful reconciliation compute.</summary>
    public const string AuditReconciliationComputed = "MIGRATION.RECONCILIATION_COMPUTED";

    /// <summary>Stable failure code returned when the run does not exist.</summary>
    public const string RunNotFoundCode = "MIGRATION.RUN_NOT_FOUND";

    /// <summary>
    /// Computes (or recomputes) the reconciliation report for the run
    /// identified by <paramref name="runSqid"/>. The result is persisted
    /// to the <c>ReconciliationReports</c> table (one row per run, upsert).
    /// </summary>
    /// <param name="runSqid">Sqid-encoded id of the run to reconcile.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The reconciliation report DTO on success.</returns>
    Task<Result<ReconciliationReportDto>> ReconcileAsync(
        string runSqid,
        CancellationToken cancellationToken = default);
}
