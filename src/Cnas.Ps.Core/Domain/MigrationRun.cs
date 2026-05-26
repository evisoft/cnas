namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2430 / R2431 / TOR M4 — one row per execution attempt of a
/// <see cref="MigrationPlan"/>. The importer creates the row in
/// <see cref="MigrationRunStatus.Pending"/>, flips it to
/// <see cref="MigrationRunStatus.Running"/> when streaming begins, and
/// finalises it with a terminal status (Completed / CompletedWithErrors /
/// Failed / Cancelled).
/// </summary>
/// <remarks>
/// <para>
/// <b>DryRun runs.</b> When <see cref="IsDryRun"/> is true the importer
/// still inserts <see cref="MigrationStagingRow"/> children and a
/// <see cref="ReconciliationReport"/>, but the staging rows remain
/// <c>IsCommitted=false</c>; no actual target-table writes occur.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because operators
/// reference an individual run by Sqid through the admin surface.
/// </para>
/// </remarks>
public sealed class MigrationRun : AuditableEntity, IExternalId
{
    /// <summary>Foreign key to the parent <see cref="MigrationPlan"/>.</summary>
    public long PlanId { get; set; }

    /// <summary>Origin of the run (Scheduled / Manual / DryRun).</summary>
    public MigrationTriggerKind TriggerKind { get; set; }

    /// <summary>Current lifecycle status; defaults to <see cref="MigrationRunStatus.Pending"/>.</summary>
    public MigrationRunStatus Status { get; set; } = MigrationRunStatus.Pending;

    /// <summary>UTC instant the importer started this run.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC instant the importer finalised this run. Null while in flight.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Total source rows observed by the streaming step.</summary>
    public long TotalSourceRowsSeen { get; set; }

    /// <summary>Count of staging rows persisted as Imported.</summary>
    public long TotalRowsImported { get; set; }

    /// <summary>Count of staging rows persisted as Updated (overwrote an existing TargetEntityKey).</summary>
    public long TotalRowsUpdated { get; set; }

    /// <summary>Count of source rows the mapper deemed redundant / no-op.</summary>
    public long TotalRowsSkipped { get; set; }

    /// <summary>Count of source rows that failed mapping (Critical findings).</summary>
    public long TotalRowsFailed { get; set; }

    /// <summary>Sanitised, PII-free failure reason. Bounded by validators to ≤ 1000 characters. Null on success.</summary>
    public string? FailureReason { get; set; }

    /// <summary>True for DryRun runs (no commit); false for Apply runs.</summary>
    public bool IsDryRun { get; set; }
}
