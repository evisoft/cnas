namespace Cnas.Ps.Application.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — options binding for
/// <see cref="IBatchResponseSigner"/>. Bound from the
/// <c>Cnas:BatchResponseSigning</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// The HMAC key is treated as a secret — operators store it in the secrets
/// manager (see <c>EnvironmentSecretsProvider</c> / <c>VaultSecretsProvider</c>)
/// and surface it through environment variables / Vault references. The
/// in-process options instance never logs the key directly; the signer
/// resolves it on construction and discards it after the underlying
/// <c>HMACSHA256</c> primitive has been initialised.
/// </para>
/// </remarks>
public sealed class BatchResponseSigningOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:BatchResponseSigning";

    /// <summary>
    /// Base64-encoded HMAC key. Must decode to a non-empty byte array. A
    /// dev-safe default ships here so unit tests do not need to wire a
    /// configuration provider — production deployments override it via the
    /// secrets manager.
    /// </summary>
    public string HmacKeyBase64 { get; set; } = "ZGV2LWluc2VjdXJlLWtleS0xLW5vdC1mb3ItcHJvZHVjdGlvbg==";
}
