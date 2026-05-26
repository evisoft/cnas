namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0200 / TOR CF 20.01-03, MR 012 — operator-configurable cron override for an embedded
/// Quartz job. The default cron expression is baked into <c>QuartzComposition</c>; the
/// override row lets a technical administrator change cadence (or pause/resume) without
/// a redeploy. One row per <see cref="JobCode"/>; the absence of a row means "use the
/// baked-in default".
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural-key uniqueness.</b> <see cref="JobCode"/> is the stable Quartz
/// <c>JobKey.Name</c> (e.g. <c>mpay-dispatcher</c>, <c>mconnect-sync</c>); EF enforces a
/// unique index. Operators never reference these by surrogate id — the job code is the
/// admin-facing identifier and matches the rest of the automation surface (see
/// <c>AutomationController</c> and CLAUDE.md RULE 3 §"Sqid scope" — job codes are stable
/// public names and NOT Sqid-encoded).
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — the row is surfaced to the
/// admin REST surface through a Sqid so the DTO carries one alongside the natural key.
/// </para>
/// <para>
/// <b>Soft delete.</b> <see cref="AuditableEntity.IsActive"/> is the soft-delete flag.
/// Operators normally use <see cref="IsPaused"/> (which is preserved across restarts) to
/// temporarily stop a job; deactivating a row removes the override entirely and reverts
/// the scheduler to the baked-in default.
/// </para>
/// </remarks>
public sealed class JobScheduleOverride : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable Quartz job code (e.g. <c>mpay-dispatcher</c>, <c>mconnect-sync</c>). Matches
    /// the <c>JobKey.Name</c> registered in <c>QuartzComposition</c>. Pattern
    /// <c>^[a-z][a-z0-9-]{1,63}$</c> — kebab-case to mirror the existing Quartz job names.
    /// Length ≤ 64. Unique within the system.
    /// </summary>
    public string JobCode { get; set; } = string.Empty;

    /// <summary>
    /// Cron expression in Quartz 6-7 token syntax (sec / min / hour / day-of-month / month
    /// / day-of-week [/ year]). Validated at the application layer via
    /// <c>Quartz.CronExpression.IsValidExpression</c>. Length ≤ 200.
    /// </summary>
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>
    /// When <c>true</c> the job is paused at the Quartz layer — its triggers will not fire
    /// even though the cron expression is still defined. The <c>JobScheduleApplicator</c>
    /// translates this to <c>scheduler.PauseJob(jobKey)</c> on every reconcile.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Database id of the user that last edited the override. Captured at the boundary
    /// from <c>ICallerContext.UserId</c>; <c>null</c> when the row was created by a system
    /// path (test fixture, migration seed).
    /// </summary>
    public long? UpdatedByUserId { get; set; }
}
