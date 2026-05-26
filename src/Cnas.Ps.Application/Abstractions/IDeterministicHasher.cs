namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Deterministic, keyed hash for shadow columns that restore equality lookups
/// against fields encrypted at rest (CLAUDE.md §5.7, TOR SEC 035 follow-up).
/// The same canonicalized input MUST produce the same hash output across calls,
/// processes, and machines — the column built from these values backs a UNIQUE
/// INDEX and equality joins.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why HMAC, not plain SHA-256.</b> The Moldovan IDNP keyspace is small —
/// approximately 10^13 syntactically valid values — so a plain SHA-256 of an
/// IDNP can be brute-forced in milliseconds on commodity GPU hardware (a few
/// trillion SHA-256/s on a modern card). HMAC keyed with a server-side secret
/// raises the attacker bar to "must first compromise the salt", which sits in
/// the secrets manager next to the AES master key (CLAUDE.md §1.8). As long as
/// the salt stays secret, a database leak cannot be brute-forced offline into
/// the original identifiers.
/// </para>
/// <para>
/// <b>Why deterministic.</b> Random per-call salts (the standard advice for
/// password hashing) would defeat the equality-lookup goal of this primitive —
/// the same input MUST produce the same output, or the shadow column cannot
/// support <c>WHERE NationalIdHash = X</c> and the UNIQUE INDEX cannot enforce
/// "one row per IDNP". The trade-off is that two equal plaintexts produce two
/// equal hashes, which is a property attackers can exploit only through a
/// frequency analysis they could already perform on the original ciphertexts'
/// row counts. We accept this trade explicitly because the alternative —
/// keeping the plaintext column indexed — leaks far more.
/// </para>
/// <para>
/// <b>Canonicalization contract.</b> Implementations MUST canonicalize the
/// input — <c>Trim().ToUpperInvariant()</c> — BEFORE hashing. This is what
/// makes <c>" 2000000000007 "</c> and <c>"2000000000007"</c> collide
/// deterministically (a common shape difference between MConnect payloads and
/// local user input). Every call site MUST pass the raw value through this
/// method and never invent a parallel canonicalization step; otherwise the
/// hash will not match the column.
/// </para>
/// <para>
/// <b>Failure model.</b> A <c>null</c> input is a programmer error and throws
/// <see cref="System.ArgumentNullException"/>. An empty string is a legitimate
/// input and produces a stable hash. Implementations register as singletons
/// (stateless once constructed) and MUST be safe to call concurrently.
/// </para>
/// </remarks>
public interface IDeterministicHasher
{
    /// <summary>
    /// Computes the canonical hash of <paramref name="canonicalValue"/> for use
    /// in a shadow column. Implementations canonicalize the input
    /// (<c>Trim</c> + <c>ToUpperInvariant</c>) before hashing, so callers can
    /// safely pass values straight from user input or external payloads — there
    /// is no need (and no benefit) to pre-canonicalize at the call site.
    /// </summary>
    /// <param name="canonicalValue">
    /// The raw value to hash. The parameter name is "canonical" because the
    /// hasher will canonicalize it; the contract is that the resulting hash
    /// equals the hash of the canonical form. Must not be <c>null</c>.
    /// </param>
    /// <returns>
    /// A base64-encoded HMAC-SHA256 (44 characters including <c>=</c> padding).
    /// Suitable for storage in a <c>VARCHAR(44)</c> column and for direct use
    /// in <c>WHERE column = ?</c> equality predicates.
    /// </returns>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="canonicalValue"/> is <c>null</c>.
    /// </exception>
    string ComputeHash(string canonicalValue);
}
