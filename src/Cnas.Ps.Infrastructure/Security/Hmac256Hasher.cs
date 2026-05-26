using System;
using System.Security.Cryptography;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// HMAC-SHA256 implementation of <see cref="IDeterministicHasher"/>. Produces a
/// stable base64 hash of a canonicalized input, suitable for shadow columns
/// that restore equality lookups against encrypted national-identifier fields
/// (TOR SEC 035 / CLAUDE.md §5.7 follow-up). The 32-byte HMAC-SHA256 output
/// base64-encodes to exactly 44 characters, matching the column declaration
/// in the migration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why HMAC-SHA256 (vs SHA-256).</b> See <see cref="IDeterministicHasher"/>:
/// the IDNP keyspace is small enough to brute-force a plain SHA-256 offline,
/// and HMAC raises the bar to "first compromise the salt". The salt lives in
/// the secrets manager next to the AES master key — same blast radius, same
/// rotation discipline.
/// </para>
/// <para>
/// <b>Why static <see cref="HMACSHA256.HashData(byte[], byte[])"/>.</b> The
/// instance form of <see cref="HMACSHA256"/> is documented as NOT thread-safe;
/// the static <c>HashData</c> overload is allocation-light and thread-safe by
/// construction (no shared mutable state). The DI registration is therefore
/// <c>Singleton</c> — a single hasher instance serves every request.
/// </para>
/// <para>
/// <b>Canonicalization.</b> Inputs are normalized via
/// <c>Trim().ToUpperInvariant()</c> before being hashed. The contract is
/// documented on <see cref="IDeterministicHasher.ComputeHash"/>; this
/// implementation MUST be the single source of truth — never re-implement the
/// canonicalization at call sites.
/// </para>
/// <para>
/// <b>Logging policy.</b> This class NEVER logs the salt, the plaintext, or
/// the resulting hash at any level. The salt is held in a private byte array
/// initialized once at construction; the byte array is never exposed by
/// reference.
/// </para>
/// </remarks>
public sealed class Hmac256Hasher : IDeterministicHasher
{
    /// <summary>
    /// HMAC-SHA256 secret bytes. Decoded once at construction and held for the
    /// lifetime of the process. Never logged, never returned by reference.
    /// </summary>
    private readonly byte[] _saltBytes;

    /// <summary>
    /// Initializes the hasher with the configured salt. The salt is decoded
    /// and validated eagerly so a mis-configured deployment fails at startup
    /// rather than at first equality lookup.
    /// </summary>
    /// <param name="options">Bound <see cref="FieldHashingOptions"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// The configured salt is missing, not base64, or decodes to fewer than 32
    /// bytes. See <see cref="FieldHashingOptions.GetSaltBytes"/> for the exact
    /// preconditions.
    /// </exception>
    public Hmac256Hasher(IOptions<FieldHashingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _saltBytes = options.Value.GetSaltBytes();
    }

    /// <inheritdoc />
    public string ComputeHash(string canonicalValue)
    {
        ArgumentNullException.ThrowIfNull(canonicalValue);

        // Canonicalize: Trim() collapses leading/trailing whitespace (a frequent shape
        // difference between MConnect payloads and local input), ToUpperInvariant folds
        // case (some external registers report alphabetical legal-entity codes in mixed
        // case). The canonical form is what gets hashed; the column entries are therefore
        // case-/whitespace-insensitive at the lookup boundary even though the underlying
        // plaintext column preserves whatever the caller wrote.
        var canonical = canonicalValue.Trim().ToUpperInvariant();
        var inputBytes = Encoding.UTF8.GetBytes(canonical);

        // The static overload is thread-safe and allocation-light — no per-call HMACSHA256
        // instance, no IDisposable to manage. Output is exactly 32 bytes (SHA-256 digest).
        var hashBytes = HMACSHA256.HashData(_saltBytes, inputBytes);

        // base64(32 bytes) = 44 chars including '=' padding — matches the column width.
        return Convert.ToBase64String(hashBytes);
    }
}
