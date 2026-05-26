namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Hashes and verifies passwords using a memory-hard KDF (Argon2id). Used by the
/// local-login fallback (R0051) for the <c>Utilizator autorizat</c> role only —
/// citizen authentication flows through MPass SAML and never touches this seam.
/// </summary>
/// <remarks>
/// <para>
/// Per CLAUDE.md §5.3: Argon2id, parameters per OWASP 2024 (64 MiB / 4 iterations /
/// 4 parallelism / 16-byte salt / 32-byte hash). The hash format is a self-describing
/// string carrying salt + parameters so future-you can rotate parameters without
/// schema changes (PHC-style: <c>$argon2id$v=19$m=65536,t=4,p=4$...</c>).
/// </para>
/// <para>
/// Implementations are stateless and thread-safe; register as <c>Singleton</c> at
/// the composition root.
/// </para>
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>
    /// Hashes a plaintext password. Returns a PHC-formatted string ready for column
    /// storage (target column: <c>UserProfile.LocalPasswordHash</c>).
    /// </summary>
    /// <param name="plaintext">
    /// The password to hash. Must be non-null and non-whitespace; callers are expected
    /// to validate against <c>PasswordPolicyValidator</c> first.
    /// </param>
    /// <returns>PHC-formatted Argon2id string carrying parameters + salt + hash.</returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when <paramref name="plaintext"/> is null, empty, or whitespace.
    /// </exception>
    /// <example>
    /// <code>
    /// var phc = hasher.Hash("Aa1!aaaa");
    /// // phc → "$argon2id$v=19$m=65536,t=4,p=4$&lt;16-byte saltB64&gt;$&lt;32-byte hashB64&gt;"
    /// </code>
    /// </example>
    string Hash(string plaintext);

    /// <summary>
    /// Verifies a plaintext against a previously-stored PHC hash. Uses constant-time
    /// comparison to defeat timing attacks; NEVER throws on mismatch or on malformed
    /// input — returns <c>false</c> instead so callers do not need to defensively
    /// try/catch on every login attempt.
    /// </summary>
    /// <param name="plaintext">Candidate password supplied by the user.</param>
    /// <param name="phcHash">PHC string previously produced by <see cref="Hash(string)"/>.</param>
    /// <returns>
    /// <c>true</c> when the password matches; <c>false</c> when it does not, when the
    /// PHC string is malformed, or when either argument is <c>null</c>.
    /// </returns>
    bool Verify(string plaintext, string phcHash);
}
