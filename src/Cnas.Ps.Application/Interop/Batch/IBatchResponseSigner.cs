namespace Cnas.Ps.Application.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — abstraction over the HMAC-SHA256 signer used to
/// integrity-stamp generated response CSVs. Consumers verify the signature
/// alongside the raw hash before trusting the contents.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why HMAC, not a digital signature.</b> The Annex-4 batch surface is a
/// machine-to-machine integration tunnelled over MConnect — both endpoints
/// share a long-lived secret managed by the operator. HMAC-SHA256 gives
/// integrity + authenticity with low operational overhead; full PKI lands
/// only if the consuming systems require non-repudiation, which Annex-4
/// does not currently mandate.
/// </para>
/// <para>
/// <b>Key rotation.</b> The configured key is treated as stable for the
/// life of the deployment; rotation requires a coordinated re-sign of all
/// existing batch responses (typically not needed in practice — the batches
/// are short-lived artefacts).
/// </para>
/// </remarks>
public interface IBatchResponseSigner
{
    /// <summary>Returns the base64-encoded HMAC-SHA256 of the supplied bytes.</summary>
    /// <param name="responseFileBytes">Raw bytes of the response CSV.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Base64-encoded MAC value.</returns>
    Task<string> SignAsync(byte[] responseFileBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> iff <paramref name="signatureBase64"/> matches
    /// the recomputed MAC of <paramref name="responseFileBytes"/>. Comparison
    /// is timing-safe.
    /// </summary>
    /// <param name="responseFileBytes">Raw bytes of the response CSV.</param>
    /// <param name="signatureBase64">Candidate base64 MAC value.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns><c>true</c> on match; <c>false</c> on mismatch / malformed input.</returns>
    Task<bool> VerifyAsync(
        byte[] responseFileBytes,
        string signatureBase64,
        CancellationToken cancellationToken = default);
}
