namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2307 / TOR SEC 060 — single integrity-verification record attached to a
/// <see cref="BackupRun"/>. Captures the expected (locally computed) vs
/// actual (re-downloaded) SHA-256 hash and the resulting verdict. At most
/// one row per parent run (the EF configuration enforces a unique
/// constraint on <c>RunId</c>); subsequent re-checks update the existing
/// row in place.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — operators
/// reference checks by Sqid in admin tooling.
/// </para>
/// <para>
/// <b>Inconclusive verdict.</b> Set when the orchestrator could not
/// re-download the payload (target unreachable, key purged); operators see
/// the gap separately from a true hash-mismatch failure.
/// </para>
/// </remarks>
public sealed class BackupIntegrityCheck : AuditableEntity, IExternalId
{
    /// <summary>FK to the <see cref="BackupRun"/> the check verified.</summary>
    public long RunId { get; set; }

    /// <summary>Outcome of the check.</summary>
    public BackupIntegrityStatus Status { get; set; }

    /// <summary>UTC instant the check completed.</summary>
    public DateTime CheckedAt { get; set; }

    /// <summary>
    /// Lowercase-hex SHA-256 digest computed BEFORE upload (64 chars). The
    /// reference hash the check compares against.
    /// </summary>
    public string ExpectedHash { get; set; } = string.Empty;

    /// <summary>
    /// Lowercase-hex SHA-256 digest computed AFTER re-download (64 chars).
    /// Equals <see cref="ExpectedHash"/> on a passing check.
    /// </summary>
    public string ActualHash { get; set; } = string.Empty;

    /// <summary>
    /// Sanitised, ≤ 1000-char failure description for non-Passed checks. MUST
    /// NOT carry PII.
    /// </summary>
    public string? FailureReason { get; set; }
}
