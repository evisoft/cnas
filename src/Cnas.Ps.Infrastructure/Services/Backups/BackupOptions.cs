namespace Cnas.Ps.Infrastructure.Services.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — bound from <c>Cnas:Backups</c>. Carries the S3
/// destination addresses the placeholder
/// <see cref="S3CompatibleBackupTarget"/> looks up at run time. Production
/// swaps this surface for the real DevOps-driven adapter; until then the
/// presence of <see cref="S3"/>.<see cref="S3Settings.Endpoint"/> decides
/// whether the placeholder returns a success stub or a deterministic
/// <c>BACKUP.TARGET_NOT_CONFIGURED</c> failure.
/// </summary>
public sealed class BackupOptions
{
    /// <summary>Stable IOptions section path (<c>Cnas:Backups</c>).</summary>
    public const string SectionName = "Cnas:Backups";

    /// <summary>S3-compatible target settings.</summary>
    public S3Settings S3 { get; set; } = new();

    /// <summary>
    /// R2307 / TOR SEC 060 — S3-flavoured backup-target settings nested
    /// under <see cref="SectionName"/> as <c>Cnas:Backups:S3</c>.
    /// </summary>
    public sealed class S3Settings
    {
        /// <summary>
        /// Endpoint URL of the S3-compatible service (MinIO / AWS / MCloud).
        /// Empty until production wires the real configuration; the
        /// placeholder target reads this property to decide between a no-op
        /// success or a deterministic <c>BACKUP.TARGET_NOT_CONFIGURED</c>
        /// failure.
        /// </summary>
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>Default bucket name; empty in dev / test.</summary>
        public string Bucket { get; set; } = string.Empty;
    }
}
