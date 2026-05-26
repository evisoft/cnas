namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Application-level field-encryption contract. Implementations transparently
/// protect highly-confidential string values at rest (TOR SEC 035 / CLAUDE.md
/// §5.7) so that an SQL-injection, backup-theft, or DBA-level compromise does
/// not yield plaintext for the protected columns. Encryption is performed at
/// the application boundary — the database stores only the versioned envelope
/// returned by <see cref="Encrypt"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Envelope format.</b> All implementations MUST emit a versioned envelope
/// of the form <c>vN:&lt;base64-payload&gt;</c>, where <c>N</c> identifies the
/// (key-id, algorithm) tuple used to produce the payload. The current
/// production implementation is <c>v1</c>: AES-256-GCM with a fresh
/// 12-byte random nonce per encryption, payload layout
/// <c>nonce (12B) ‖ ciphertext (n B) ‖ tag (16B)</c>.
/// </para>
/// <para>
/// <b>Why a version prefix.</b> The prefix is the rotation seam. When a future
/// batch rolls a new master key, the new implementation emits <c>v2:…</c>
/// while still being able to decrypt <c>v1:…</c> envelopes that pre-date the
/// rotation — the system can therefore re-encrypt at rest gradually,
/// per-row, without a Big Bang outage. Removing the prefix or omitting it on
/// new writes breaks this rotation path; never do that.
/// </para>
/// <para>
/// <b>Failure model.</b> <see cref="Decrypt"/> throws
/// <see cref="FieldDecryptionException"/> on tamper (the GCM auth tag fails
/// verification), on a missing/unknown version prefix, on malformed base64,
/// and on a too-short payload. These are exceptional conditions: a healthy
/// row never produces them. They are exceptions rather than <c>Result</c>
/// values per CLAUDE.md §2.1 because a decryption failure indicates either
/// data corruption, a wrong key (mis-deployment), or active tampering — none
/// of which the caller can recover from at the call site.
/// </para>
/// <para>
/// <b>What this is NOT.</b> Field encryption is NOT a search index. Equality
/// lookups (<c>WHERE NationalId = 'X'</c>) cease to work once a column is
/// encrypted, because every row encrypts the same plaintext to a different
/// ciphertext (random nonce). Columns that need equality lookups require a
/// separate hash-shadow column (e.g. <c>NationalIdHash</c>) — that is a
/// distinct batch of work.
/// </para>
/// <para>
/// Implementations are registered as singletons (stateless once constructed)
/// and MUST be safe to call concurrently.
/// </para>
/// </remarks>
public interface IFieldEncryptor
{
    /// <summary>
    /// Encrypts the given plaintext and returns the versioned envelope to
    /// store at rest. Each call MUST sample a fresh random nonce — repeated
    /// calls with the same input MUST produce different envelopes.
    /// </summary>
    /// <param name="plaintext">
    /// UTF-8 string to protect. Must not be <c>null</c>. The empty string is
    /// a legitimate value (encrypts to a fixed-length envelope).
    /// </param>
    /// <returns>
    /// A string of the form <c>vN:&lt;base64-payload&gt;</c>. The payload layout
    /// is opaque to callers — only <see cref="Decrypt"/> may parse it.
    /// </returns>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="plaintext"/> is <c>null</c>. The EF Core
    /// value converter wired on top of this interface short-circuits nullable
    /// columns BEFORE calling this method.
    /// </exception>
    string Encrypt(string plaintext);

    /// <summary>
    /// Decrypts a versioned envelope previously produced by <see cref="Encrypt"/>
    /// and returns the original plaintext. Constant-time auth-tag verification
    /// is performed by the underlying AEAD primitive.
    /// </summary>
    /// <param name="ciphertext">
    /// The versioned envelope to decrypt. The version prefix is mandatory:
    /// the implementation refuses bare base64.
    /// </param>
    /// <returns>The original UTF-8 plaintext supplied to <see cref="Encrypt"/>.</returns>
    /// <exception cref="FieldDecryptionException">
    /// Thrown for any of:
    /// <list type="bullet">
    ///   <item>missing or unknown <c>vN:</c> version prefix,</item>
    ///   <item>payload that is not valid base64,</item>
    ///   <item>payload too short to contain a nonce + tag,</item>
    ///   <item>GCM auth-tag verification failure (tamper / wrong key),</item>
    ///   <item>any other unexpected failure inside the AEAD primitive.</item>
    /// </list>
    /// The inner exception (when present) carries the underlying cause for
    /// the operator log but never reaches the caller's response body.
    /// </exception>
    string Decrypt(string ciphertext);
}
