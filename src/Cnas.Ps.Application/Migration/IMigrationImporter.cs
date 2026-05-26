using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Migration;

/// <summary>
/// R2430 / R2431 / TOR M4 — orchestrator that drives a single
/// <see cref="MigrationPlan"/> from source-stream through batch mapping
/// into the generic staging-row table. Used by both the nightly DryRun
/// Quartz job and the admin REST surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b>
/// <list type="number">
///   <item>Resolve the plan by Sqid; refuse runs unless Status=Active.</item>
///   <item>Insert a <see cref="MigrationRun"/> row, Status=Pending.</item>
///   <item>Resolve <see cref="IMigrationSource"/> by SourceKind + <see cref="IMigrationRecordMapper"/> by TargetEntityName.</item>
///   <item>Flip Status=Running, stream source records in batches of <see cref="MigrationPlan.BatchSize"/>.</item>
///   <item>Per batch: map each record, persist <see cref="MigrationStagingRow"/> + Critical findings, flush a <see cref="MigrationBatch"/> counter row.</item>
///   <item>Compute reconciliation via <c>IMigrationReconciler</c>.</item>
///   <item>Finalise run status to Completed / CompletedWithErrors / Failed.</item>
/// </list>
/// </para>
/// </remarks>
public interface IMigrationImporter
{
    /// <summary>Stable audit code emitted on a successful run completion.</summary>
    public const string AuditRunCompleted = "MIGRATION.RUN_COMPLETED";

    /// <summary>Stable audit code emitted on a run failure (Critical severity).</summary>
    public const string AuditRunFailed = "MIGRATION.RUN_FAILED";

    /// <summary>Stable failure code returned when the plan does not exist.</summary>
    public const string PlanNotFoundCode = "MIGRATION.PLAN_NOT_FOUND";

    /// <summary>Stable failure code returned when the plan is not in Active status.</summary>
    public const string PlanNotActiveCode = "MIGRATION.PLAN_NOT_ACTIVE";

    /// <summary>Stable failure code returned when no source adapter is registered for the plan's SourceKind.</summary>
    public const string SourceNotConfiguredCode = "MIGRATION.SOURCE_NOT_CONFIGURED";

    /// <summary>Stable failure code returned when the peak-hour gate blocks a manual trigger.</summary>
    public const string PeakHourGateBlockedCode = "MIGRATION.PEAK_HOUR_GATE_BLOCKED";

    /// <summary>
    /// Runs a single import for the plan identified by
    /// <paramref name="planSqid"/>. <paramref name="trigger"/> drives the
    /// DryRun vs Apply distinction (DryRun keeps staging rows uncommitted).
    /// </summary>
    /// <param name="planSqid">Sqid-encoded id of the plan to import.</param>
    /// <param name="trigger">Origin + mode of the run.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Run summary on success; failure on validation / configuration miss.</returns>
    Task<Result<MigrationRunSummaryDto>> ImportAsync(
        string planSqid,
        MigrationTriggerKind trigger,
        CancellationToken cancellationToken = default);
}
