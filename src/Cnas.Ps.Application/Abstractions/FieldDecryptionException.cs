namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Raised by <see cref="IFieldEncryptor.Decrypt(string)"/> when a stored
/// ciphertext envelope cannot be transformed back into plaintext. Distinct
/// from a generic <see cref="System.Exception"/> so middleware and audit
/// pipelines can pattern-match on it (e.g. raise a CRITICAL audit event
/// without crashing the request).
/// </summary>
/// <remarks>
/// <para>
/// Decryption failure is exceptional, not a business outcome. The three real
/// causes are:
/// </para>
/// <list type="bullet">
///   <item><b>Tamper</b> — the ciphertext was modified at rest (storage compromise) or in transit (memory corruption). The GCM auth tag detects this.</item>
///   <item><b>Wrong key</b> — the active encryption key does not match the one used to write the row (key rotation gone wrong, or restore from a backup taken under a different key).</item>
///   <item><b>Malformed envelope</b> — unknown version prefix, invalid base64, truncated payload. Usually indicates a hand-edited column or a bug in a sibling system that wrote to the column directly.</item>
/// </list>
/// <para>
/// Callers should treat this as <c>500 INTERNAL_ERROR</c> with a correlation
/// id, NOT as <c>400 VALIDATION_FAILED</c> — the failure is server-side
/// confidentiality enforcement, not a caller input problem.
/// </para>
/// </remarks>
[System.Serializable]
public sealed class FieldDecryptionException : System.Exception
{
    /// <summary>Initializes a new instance with no message or inner exception.</summary>
    public FieldDecryptionException() { }

    /// <summary>Initializes a new instance with a human-readable explanation suitable for the operator log.</summary>
    /// <param name="message">Diagnostic message; MUST NOT include plaintext or key material.</param>
    public FieldDecryptionException(string message) : base(message) { }

    /// <summary>Initializes a new instance wrapping the underlying cryptographic failure.</summary>
    /// <param name="message">Diagnostic message; MUST NOT include plaintext or key material.</param>
    /// <param name="innerException">Underlying exception (e.g. <see cref="System.Security.Cryptography.AuthenticationTagMismatchException"/>) captured for the operator log only.</param>
    public FieldDecryptionException(string message, System.Exception innerException) : base(message, innerException) { }
}
