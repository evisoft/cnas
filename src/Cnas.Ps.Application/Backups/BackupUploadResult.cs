namespace Cnas.Ps.Application.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — return value of
/// <c>IBackupTarget.UploadAsync</c>. Carries the opaque storage key the
/// target chose for the payload plus an echo of the size + hash the
/// orchestrator can cross-check against the local hash it pre-computed.
/// </summary>
/// <param name="StorageKey">Opaque target-scoped key persistted on the <c>BackupRun</c> row.</param>
/// <param name="SizeBytes">Number of bytes the target accepted.</param>
/// <param name="Sha256Hex">SHA-256 digest the target observed (lowercase-hex, 64 chars).</param>
public sealed record BackupUploadResult(
    string StorageKey,
    long SizeBytes,
    string Sha256Hex);
