namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2307 / TOR SEC 060 — per-execution ledger row attached to a
/// <see cref="BackupPolicy"/>. One row per scheduled or manual fire of a
/// policy carries the runtime metadata (timing, payload hash + size, target
/// storage key, terminal status, and optional sanitised failure reason).
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural-key uniqueness.</b> <see cref="RunNumber"/> is the deterministic
/// <c>BKR-{year}-{seq:000000}</c> identifier — easy for operators to quote in
/// support tickets. The EF configuration enforces a unique constraint.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — operators
/// reference runs by Sqid through the admin surface.
/// </para>
/// <para>
/// <b>No PII.</b> The ledger never persists payload bytes nor any data
/// extracted from them. Only the SHA-256 hash + length + opaque storage key
/// land here. <see cref="FailureReason"/> is sanitised + bounded to 1000
/// characters at the orchestrator before persistence.
/// </para>
/// </remarks>
public sealed class BackupRun : AuditableEntity, IExternalId
{
    /// <summary>FK to the <see cref="BackupPolicy"/> this run executed.</summary>
    public long PolicyId { get; set; }

    /// <summary>
    /// Deterministic <c>BKR-{year}-{seq:000000}</c> run number. Bounded to 32
    /// characters. Unique within the system.
    /// </summary>
    public string RunNumber { get; set; } = string.Empty;

    /// <summary>Current lifecycle status; defaults to <see cref="BackupRunStatus.Pending"/>.</summary>
    public BackupRunStatus Status { get; set; } = BackupRunStatus.Pending;

    /// <summary>Origin of this run (Scheduled or Manual).</summary>
    public BackupTriggerKind TriggerKind { get; set; }

    /// <summary>UTC instant the run started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC instant the run reached a terminal status (Succeeded / Failed / IntegrityFailed); null while running.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Wall-clock duration of the run in milliseconds; null while still running.</summary>
    public long? DurationMs { get; set; }

    /// <summary>Size of the uploaded payload in bytes; null when the run never produced a payload (failed early).</summary>
    public long? PayloadSizeBytes { get; set; }

    /// <summary>
    /// Lowercase-hex SHA-256 digest of the payload (64 chars). Null when the
    /// run never produced a payload.
    /// </summary>
    public string? PayloadHashSha256 { get; set; }

    /// <summary>
    /// Opaque storage key returned by <c>IBackupTarget.UploadAsync</c>. Used
    /// by the integrity recheck endpoint and the retention sweep. Null when
    /// the upload never completed.
    /// </summary>
    public string? PayloadStorageKey { get; set; }

    /// <summary>
    /// Sanitised, ≤ 1000-char failure description for terminal-Failed runs.
    /// MUST NOT carry PII; the orchestrator sanitises before persistence.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// UTC instant the retention sweeper deleted this run's storage payload
    /// from the target. Null while the payload still exists on the target.
    /// </summary>
    public DateTime? RetentionPurgedAt { get; set; }
}
