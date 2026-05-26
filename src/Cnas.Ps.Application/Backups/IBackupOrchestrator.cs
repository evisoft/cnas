using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — runtime orchestrator that drives an individual
/// <c>BackupRun</c> end-to-end (insert Pending row → ask provider for
/// payload → upload to target → verify hashes → persist integrity check
/// → finalise Status). Also owns the retention sweep + integrity recheck
/// flows.
/// </summary>
public interface IBackupOrchestrator
{
    /// <summary>Stable failure code: no <c>IBackupPayloadProvider</c> is registered for the policy's scope.</summary>
    public const string ProviderNotConfiguredCode = "BACKUP.PROVIDER_NOT_CONFIGURED";

    /// <summary>Stable failure code: the policy is inactive or archived.</summary>
    public const string PolicyNotActiveCode = "BACKUP.POLICY_NOT_ACTIVE";

    /// <summary>Stable audit event emitted on a successful run.</summary>
    public const string AuditRunSucceeded = "BACKUP.RUN_SUCCEEDED";

    /// <summary>Stable audit event emitted on a failed run.</summary>
    public const string AuditRunFailed = "BACKUP.RUN_FAILED";

    /// <summary>Stable audit event emitted when an integrity check finishes with a non-Passed verdict.</summary>
    public const string AuditIntegrityFailed = "BACKUP.INTEGRITY_FAILED";

    /// <summary>Stable audit event emitted by the retention sweep.</summary>
    public const string AuditRetentionSwept = "BACKUP.RETENTION_SWEPT";

    /// <summary>
    /// Runs the backup pipeline for the policy identified by
    /// <paramref name="policySqid"/>.
    /// </summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="trigger">Origin of the run (Scheduled / Manual).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The persisted run DTO on success.</returns>
    Task<Result<BackupRunDto>> RunPolicyAsync(
        string policySqid,
        BackupTriggerKind trigger,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one run by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The run DTO on success.</returns>
    Task<Result<BackupRunDto>> GetRunByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>Lists runs (paged + filterable).</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<BackupRunPageDto>> ListRunsAsync(
        BackupRunFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sweeps every run whose retention window has expired and that still
    /// holds an un-purged storage key. Deletes the payload via the target,
    /// sets <c>RetentionPurgedAt</c>, emits one Critical-severity audit row
    /// summarising the count.
    /// </summary>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The number of rows purged.</returns>
    Task<Result<int>> SweepExpiredRunsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-downloads and re-hashes the payload for an existing
    /// <c>BackupRun</c>; upserts the matching <c>BackupIntegrityCheck</c>
    /// row.
    /// </summary>
    /// <param name="runSqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The persisted integrity-check DTO.</returns>
    Task<Result<BackupIntegrityCheckDto>> RetryIntegrityCheckAsync(
        string runSqid,
        CancellationToken cancellationToken = default);
}
