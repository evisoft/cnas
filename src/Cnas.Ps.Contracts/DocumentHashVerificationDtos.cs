using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0341 / TOR CF 11.06 — DTO surfaced by the document hash-verification
// endpoint. Operators run a verification to catch silent storage corruption
// or unauthorised tampering on the MinIO bucket.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0341 — outcome envelope returned by an <c>IDocumentHashVerifier</c> call.
/// Carries the match flag + the two hashes the caller compared. Mismatches
/// emit a Critical-severity audit row and a <c>cnas.document.hash_verify</c>
/// counter increment tagged with <c>outcome=mismatch</c>.
/// </summary>
/// <param name="DocumentSqid">Sqid-encoded id of the verified document.</param>
/// <param name="IsMatch"><c>true</c> when the stored bytes hash to the recorded value.</param>
/// <param name="StoredHash">SHA-256 hex digest recorded on the document row.</param>
/// <param name="ComputedHash">SHA-256 hex digest computed from the storage bytes.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record DocumentHashVerificationDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DocumentSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsMatch,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string StoredHash,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ComputedHash);
