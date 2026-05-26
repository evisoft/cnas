namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Options for the R0188 audit-archive replay pipeline. Bound from the
/// <c>Cnas:AuditArchive</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// The archive is the durable spill area for audit batches whose primary flush
/// (DB + MLog) failed. A periodic Quartz job reads each pending archive and
/// re-attempts the flush; on success the archive is deleted, otherwise it is
/// left on disk for the next run. Persistent failures are operator-investigated
/// — this iteration does not track per-batch retry counts.
/// </para>
/// <para>
/// Defaults are tuned for dev / single-node deployments. Production may want a
/// shared filesystem mount (or, in a future iteration, a MinIO-backed
/// <c>IAuditArchive</c> implementation that does not depend on local disk).
/// </para>
/// </remarks>
public sealed class AuditArchiveOptions
{
    /// <summary>Stable configuration section name — <c>Cnas:AuditArchive</c>.</summary>
    public const string SectionName = "Cnas:AuditArchive";

    /// <summary>
    /// Local filesystem path where failed batches are spilled as JSON files.
    /// Default: <c>audit-archive</c> (relative to the process working directory).
    /// </summary>
    /// <remarks>
    /// The directory is created on first use; operators are free to point this
    /// at a persistent volume mount (e.g. <c>/var/lib/cnas/audit-archive</c>)
    /// so spilled records survive pod restarts.
    /// </remarks>
    public string LocalPath { get; init; } = "audit-archive";

    /// <summary>
    /// Replay cadence in Quartz cron syntax. Default <c>0 */5 * ? * *</c>
    /// — every 5 minutes, on the minute boundary.
    /// </summary>
    /// <remarks>
    /// Currently informational only — the Quartz registration in
    /// <c>QuartzComposition</c> hard-codes the same expression so the wiring
    /// does not need to resolve <see cref="Microsoft.Extensions.Options.IOptions{T}"/>
    /// during job-registration. A future iteration may make the cron dynamic.
    /// </remarks>
    public string ReplayCron { get; init; } = "0 */5 * ? * *";

    /// <summary>
    /// Maximum number of archived batches a single replay run will attempt.
    /// Defence against a pathological backlog wedging the job for hours.
    /// Default: <c>100</c>.
    /// </summary>
    public int MaxReplayBatchesPerRun { get; init; } = 100;
}
