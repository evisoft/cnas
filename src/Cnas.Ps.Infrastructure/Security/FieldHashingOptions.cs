using System;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// Configuration for <see cref="Hmac256Hasher"/>. Bound from
/// <c>Cnas:FieldHashing</c> at application startup. In production the underlying
/// value MUST come from the same secrets manager (HashiCorp Vault, k8s Secret,
/// MCloud KMS) that supplies <see cref="FieldEncryptionOptions"/> — per
/// CLAUDE.md §1.8 and TOR SEC 005 / SEC 006 — NEVER from
/// <c>appsettings.json</c> committed to source control.
/// </summary>
/// <remarks>
/// <para>
/// <b>Salt is the HMAC secret.</b> The salt is the keyed-hash secret: knowledge
/// of it lets an attacker brute-force the national-identifier shadow columns
/// offline (the keyspace is small — ~10^13 for IDNPs). Treat it with the same
/// blast radius as <see cref="FieldEncryptionOptions.Key"/>.
/// </para>
/// <para>
/// <b>Rotation is expensive.</b> Rotating this salt requires recomputing every
/// shadow-column row in the database — there is no "gradual re-hash at first
/// touch" strategy because the OLD hash is also what the UNIQUE INDEX enforces.
/// Treat the salt as long-lived: rotate only on suspected compromise, plan a
/// maintenance window, and bring application writes offline while the
/// recompute job rewrites every row. The encryption key rotation seam (the
/// <c>v1:</c> envelope prefix) does NOT apply here — hashes carry no version
/// because there is no way to look up by an old version's hash and the new
/// version's hash at the same time.
/// </para>
/// <para>
/// The CNAS deployment binds this via the standard secret-injection chain:
/// the secret store exposes <c>CNAS__FIELDHASHING__SALTKEY</c> as an
/// environment variable, the .NET configuration provider promotes it to the
/// <c>Cnas:FieldHashing:SaltKey</c> path, and <c>AddOptions</c> validates the
/// length on startup. A missing key registers
/// <see cref="MissingSaltHmacHasher"/> instead — fail loud, not silent — so
/// the application boots but the first equality lookup against a hash column
/// throws an immediate <see cref="InvalidOperationException"/> that surfaces
/// in the alert pipeline.
/// </para>
/// </remarks>
public sealed class FieldHashingOptions
{
    /// <summary>Configuration section name to bind from app settings.</summary>
    public const string SectionName = "Cnas:FieldHashing";

    /// <summary>Minimum salt length in bytes (NIST FIPS 198-1 recommends matching the underlying hash length: SHA-256 → 32 bytes).</summary>
    private const int MinKeyBytes = 32;

    /// <summary>
    /// Base64-encoded HMAC-SHA256 key — at least 32 raw bytes when decoded.
    /// Sourced at runtime from the secrets manager; the property exists only
    /// so the options pipeline can hand the value to
    /// <see cref="Hmac256Hasher"/>'s constructor.
    /// </summary>
    /// <remarks>
    /// Per CLAUDE.md §1.8 the salt is held in a private field on the hasher
    /// (decoded once at construction time) and is never exposed by reference,
    /// never logged, never returned through any API. See
    /// <see cref="GetSaltBytes"/> for the decode + validate helper invoked by
    /// the hasher.
    /// </remarks>
    public string SaltKey { get; init; } = string.Empty;

    /// <summary>
    /// Decodes <see cref="SaltKey"/> from base64 and validates it is at least
    /// 32 bytes. Throws <see cref="ArgumentException"/> on any validation
    /// failure so options binding fails loud at startup rather than silently
    /// no-op'ing in production. (NIST FIPS 198-1: HMAC key length should be
    /// at least the hash output length — 32 bytes for SHA-256.)
    /// </summary>
    /// <returns>The raw HMAC key bytes (≥ 32 bytes).</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="SaltKey"/> is null/empty/whitespace, is not
    /// valid base64, or decodes to fewer than 32 bytes.
    /// </exception>
    public byte[] GetSaltBytes()
    {
        if (string.IsNullOrWhiteSpace(SaltKey))
        {
            throw new ArgumentException(
                $"{SectionName}:SaltKey is required (base64-encoded HMAC-SHA256 key, ≥ 32 bytes). " +
                "Bind from the secrets manager per CLAUDE.md §1.8.", nameof(SaltKey));
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(SaltKey);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(
                $"{SectionName}:SaltKey must be valid base64 (HMAC-SHA256 key).", nameof(SaltKey), ex);
        }

        if (decoded.Length < MinKeyBytes)
        {
            throw new ArgumentException(
                $"{SectionName}:SaltKey must decode to at least {MinKeyBytes} bytes ({MinKeyBytes * 8} bits) — " +
                $"NIST FIPS 198-1 recommends matching the hash output length; received {decoded.Length} bytes.",
                nameof(SaltKey));
        }

        return decoded;
    }
}
