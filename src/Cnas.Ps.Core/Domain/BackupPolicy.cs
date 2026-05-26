namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2307 / TOR SEC 060 — operator-configurable backup-policy registry row.
/// Each row binds a stable code to a scope, strategy, cron schedule, retention
/// window, and target adapter. The Quartz <c>BackupExecutionJob</c> consults
/// the Active rows on every fire and triggers a run when the policy's cron is
/// due. See <c>BackupRun</c> for per-execution ledger records.
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural-key uniqueness.</b> <see cref="PolicyCode"/> is the stable
/// SCREAMING_SNAKE_CASE identifier (e.g. <c>DB_FULL</c>,
/// <c>FILE_STORAGE_DAILY</c>); the EF configuration enforces a unique
/// constraint.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — operators
/// reference policies by Sqid through the admin surface.
/// </para>
/// <para>
/// <b>No credentials at rest.</b> <see cref="TargetReference"/> carries only the
/// bucket-name / path part of the destination address; credentials live in
/// app config / secrets manager and are looked up by the concrete
/// <c>IBackupTarget</c> at run-time.
/// </para>
/// </remarks>
public sealed class BackupPolicy : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable SCREAMING_SNAKE_CASE policy code, e.g. <c>DB_FULL</c>,
    /// <c>FILE_STORAGE_DAILY</c>. Pattern <c>^[A-Z][A-Z0-9_.]{1,63}$</c>,
    /// length ≤ 64. Unique within the system.
    /// </summary>
    public string PolicyCode { get; set; } = string.Empty;

    /// <summary>Human-readable display name. Bounded to 256 characters.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional free-form description. Bounded to 2000 characters.</summary>
    public string? Description { get; set; }

    /// <summary>Data-set scope this policy targets.</summary>
    public BackupScope Scope { get; set; }

    /// <summary>Backup strategy (Full / Incremental / Differential).</summary>
    public BackupStrategy Strategy { get; set; }

    /// <summary>
    /// Quartz cron expression governing when this policy fires. Validated
    /// upstream via <c>Quartz.CronExpression.IsValidExpression</c>. Bounded
    /// to 64 characters.
    /// </summary>
    public string CronSchedule { get; set; } = string.Empty;

    /// <summary>Days backups stay on the target before the retention sweep purges them. 1..3650.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Physical backup-target kind this policy uploads to.</summary>
    public BackupTargetKind TargetKind { get; set; }

    /// <summary>
    /// Opaque target-reference (bucket-name / path). Carries NO credentials;
    /// bounded to 256 characters; may be null.
    /// </summary>
    public string? TargetReference { get; set; }

    /// <summary>UTC instant of the most recent <c>Succeeded</c> run for this policy; null until the first success.</summary>
    public DateTime? LastSuccessfulRunAt { get; set; }

    /// <summary>UTC instant of the most recent <c>Failed</c> or <c>IntegrityFailed</c> run; null when no failure observed.</summary>
    public DateTime? LastFailedRunAt { get; set; }

    /// <summary>Internal id of the operator who registered the policy.</summary>
    public long RegisteredByUserId { get; set; }

    /// <summary>
    /// True once an operator has archived this policy via the admin surface.
    /// Distinct from the inherited <see cref="AuditableEntity.IsActive"/>
    /// (which is the framework-wide soft-delete) — both flags must be
    /// false-equivalent for the orchestrator to ignore the row, but the
    /// service uses this field to refuse <c>ModifyAsync</c> after archive
    /// while still allowing <c>ListAsync</c> on the row.
    /// </summary>
    public bool IsArchived { get; set; }
}
