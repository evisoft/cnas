using System;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// Configuration for <see cref="AesFieldEncryptor"/>. Bound from
/// <c>Cnas:FieldEncryption</c> at application startup. In production the
/// underlying value MUST come from the secrets manager (HashiCorp Vault, k8s
/// Secret, MCloud KMS) per CLAUDE.md §1.8 and TOR SEC 005 / SEC 006 — NEVER
/// from <c>appsettings.json</c> committed to source control.
/// </summary>
/// <remarks>
/// <para>
/// The CNAS deployment binds this via the standard secret-injection chain:
/// the secret store exposes <c>CNAS__FIELDENCRYPTION__KEY</c> as an environment
/// variable, the .NET configuration provider promotes it to the
/// <c>Cnas:FieldEncryption:Key</c> path, and <c>AddOptions</c> validates the
/// length on startup. Rotating the key without a coordinated re-encryption
/// pass breaks every <c>v1:</c> envelope at rest — see <see cref="Cnas.Ps.Application.Abstractions.IFieldEncryptor"/>
/// for the rotation strategy.
/// </para>
/// </remarks>
public sealed class FieldEncryptionOptions
{
    /// <summary>Configuration section name to bind from app settings.</summary>
    public const string SectionName = "Cnas:FieldEncryption";

    /// <summary>
    /// Base64-encoded AES-256 master key — exactly 32 raw bytes when decoded.
    /// Sourced at runtime from the secrets manager; the property exists only
    /// so the options pipeline can hand the value to
    /// <see cref="AesFieldEncryptor"/>'s constructor.
    /// </summary>
    /// <remarks>
    /// The key is decoded once at construction time and held in a private
    /// field on the encryptor — it is not exposed by reference, and it is
    /// never logged. See <see cref="GetKeyBytes"/> for the decode + validate
    /// helper invoked by the encryptor.
    /// </remarks>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Decodes <see cref="Key"/> from base64 and validates that it is exactly
    /// 32 bytes (256 bits — the only key length accepted by AES-GCM in our
    /// implementation). Throws <see cref="ArgumentException"/> on any
    /// validation failure so options binding fails loud at startup rather
    /// than silently no-op'ing in production.
    /// </summary>
    /// <returns>The raw 32-byte master key.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="Key"/> is null/empty/whitespace, is not valid
    /// base64, or decodes to a length other than 32 bytes.
    /// </exception>
    public byte[] GetKeyBytes()
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            throw new ArgumentException(
                $"{SectionName}:Key is required (base64-encoded 256-bit AES key). " +
                "Bind from the secrets manager per CLAUDE.md §1.8.", nameof(Key));
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(Key);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(
                $"{SectionName}:Key must be valid base64 (256-bit AES key).", nameof(Key), ex);
        }

        if (decoded.Length != 32)
        {
            throw new ArgumentException(
                $"{SectionName}:Key must decode to exactly 32 bytes (256 bits); " +
                $"received {decoded.Length} bytes.", nameof(Key));
        }

        return decoded;
    }
}
