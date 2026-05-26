using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Documents;

/// <summary>
/// R0341 / TOR CF 11.06 — verifies that the bytes currently in object storage
/// match the SHA-256 hash recorded on the corresponding <c>Document</c> row.
/// Surfaced through the admin REST surface so operators can catch silent
/// storage corruption or unauthorised tampering on the MinIO bucket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always audited.</b> Every verification emits an audit row — Information
/// severity when the hashes match, Critical severity when they do not. The
/// service also increments <c>cnas.document.hash_verify</c> tagged with
/// <c>outcome={match|mismatch|error}</c> so operators can chart the false-
/// positive rate.
/// </para>
/// </remarks>
public interface IDocumentHashVerifier
{
    /// <summary>Stable audit event code emitted on every verification (severity depends on outcome).</summary>
    public const string AuditHashVerify = "DOCUMENT.HASH_VERIFY";

    /// <summary>
    /// Reads <c>Document.ContentSha256Hex</c>, downloads the underlying bytes
    /// via <c>IFileStorage</c>, computes a fresh SHA-256, and compares.
    /// </summary>
    /// <param name="documentSqid">Sqid-encoded id of the document to verify.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// On success the verification outcome (always populated, including the
    /// mismatch path); on lookup failure a typed <see cref="Result{T}"/>
    /// failure (<see cref="ErrorCodes.NotFound"/> / <see cref="ErrorCodes.InvalidSqid"/>).
    /// </returns>
    Task<Result<DocumentHashVerificationDto>> VerifyAsync(
        string documentSqid,
        CancellationToken cancellationToken = default);
}
