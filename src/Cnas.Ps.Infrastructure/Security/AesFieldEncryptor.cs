using System;
using System.Security.Cryptography;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// AES-256-GCM implementation of <see cref="IFieldEncryptor"/>. Produces a
/// versioned envelope <c>v1:&lt;base64(nonce ‖ ciphertext ‖ tag)&gt;</c>
/// suitable for storage in a single character-varying column. Used to protect
/// highly-confidential fields at rest (TOR SEC 035 / CLAUDE.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why AES-256-GCM (vs CBC + HMAC).</b> GCM is an authenticated encryption
/// mode (AEAD): a single primitive provides both confidentiality and integrity,
/// and a single mismatched bit in the ciphertext or tag aborts decryption.
/// CBC + HMAC requires a second key, separate MAC verification, and is
/// historically prone to padding-oracle and timing bugs at the integration
/// layer. NIST SP 800-38D §8.2 endorses 96-bit nonces for GCM, which is
/// exactly what this implementation samples.
/// </para>
/// <para>
/// <b>Why nonce reuse is catastrophic in GCM.</b> Encrypting two distinct
/// plaintexts under the same (key, nonce) pair leaks the XOR of their
/// keystreams AND lets an attacker forge arbitrary ciphertexts under the
/// recovered authentication subkey (Joux's "forbidden attack"). This
/// implementation therefore samples a fresh 12-byte nonce from
/// <see cref="RandomNumberGenerator"/> on every call to
/// <see cref="Encrypt(string)"/> — future maintainers MUST NOT "optimise"
/// this by caching a counter or deriving the nonce from the plaintext.
/// </para>
/// <para>
/// <b>Thread safety.</b> <see cref="AesGcm"/> is documented as thread-safe by
/// .NET; this class allocates the primitive once in the constructor and
/// reuses it for the lifetime of the process. The DI registration is
/// <c>Singleton</c> accordingly.
/// </para>
/// <para>
/// <b>Constant-time tag verification.</b> The auth-tag comparison is performed
/// inside <see cref="AesGcm.Decrypt(byte[], byte[], byte[], byte[], byte[])"/>
/// by the underlying platform crypto provider, which uses a constant-time
/// path — there is no observable timing difference between "tag wrong by 1 bit"
/// and "tag wrong by 128 bits".
/// </para>
/// <para>
/// <b>Logging policy.</b> This class NEVER logs the key, the plaintext, or the
/// ciphertext at any level. Diagnostic messages on
/// <see cref="FieldDecryptionException"/> reference only the kind of failure
/// (unknown version, malformed base64, etc.), never the contents.
/// </para>
/// </remarks>
public sealed class AesFieldEncryptor : IFieldEncryptor, IDisposable
{
    /// <summary>Current envelope version. Bumping this implies a new (key, algorithm) tuple.</summary>
    private const string V1Prefix = "v1:";

    /// <summary>NIST SP 800-38D §8.2 recommended GCM nonce size for random nonces (12 bytes / 96 bits).</summary>
    private const int NonceSize = 12;

    /// <summary>GCM authentication tag size — 16 bytes / 128 bits, the standard maximum.</summary>
    private const int TagSize = 16;

    /// <summary>
    /// Pre-constructed AES-GCM primitive bound to the master key. Allocated
    /// once because <see cref="AesGcm"/> is thread-safe per the BCL docs.
    /// </summary>
    private readonly AesGcm _aes;

    /// <summary>
    /// Initializes the encryptor with the configured master key. The key is
    /// decoded and validated eagerly so a mis-configured deployment fails at
    /// startup rather than at first use.
    /// </summary>
    /// <param name="options">Bound <see cref="FieldEncryptionOptions"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// The configured key is missing, not base64, or does not decode to
    /// exactly 32 bytes. See <see cref="FieldEncryptionOptions.GetKeyBytes"/>
    /// for the exact preconditions.
    /// </exception>
    public AesFieldEncryptor(IOptions<FieldEncryptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = options.Value.GetKeyBytes();
        // AesGcm takes ownership of the key bytes; we never expose the array.
        _aes = new AesGcm(key, TagSize);
    }

    /// <inheritdoc />
    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        // Cryptographic-strength entropy. Critical for GCM: see class remarks.
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];
        _aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Pack as nonce ‖ ciphertext ‖ tag so a single base64 string carries
        // everything Decrypt needs to verify and unseal.
        var payload = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, payload, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, NonceSize + ciphertext.Length, TagSize);

        return V1Prefix + Convert.ToBase64String(payload);
    }

    /// <inheritdoc />
    public string Decrypt(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            throw new FieldDecryptionException("Ciphertext envelope is null or empty.");
        }

        // Locate the version separator. We accept ONLY known prefixes; bare
        // base64 (no prefix) is rejected so we always have a rotation seam.
        var separator = ciphertext.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            throw new FieldDecryptionException(
                "Ciphertext envelope is missing the required 'vN:' version prefix.");
        }

        var version = ciphertext[..separator];
        if (!string.Equals(version, "v1", StringComparison.Ordinal))
        {
            throw new FieldDecryptionException(
                $"Unknown ciphertext envelope version '{version}'. " +
                "This build understands only 'v1' — a higher version implies a newer key/algorithm that has not been deployed.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(ciphertext[(separator + 1)..]);
        }
        catch (FormatException ex)
        {
            throw new FieldDecryptionException("Ciphertext envelope payload is not valid base64.", ex);
        }

        if (payload.Length < NonceSize + TagSize)
        {
            throw new FieldDecryptionException(
                $"Ciphertext envelope payload is too short ({payload.Length} bytes); " +
                $"minimum is {NonceSize + TagSize} bytes (nonce + tag, zero-length plaintext).");
        }

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipherLength = payload.Length - NonceSize - TagSize;
        var cipherBytes = new byte[cipherLength];

        Buffer.BlockCopy(payload, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(payload, NonceSize, cipherBytes, 0, cipherLength);
        Buffer.BlockCopy(payload, NonceSize + cipherLength, tag, 0, TagSize);

        var plaintextBytes = new byte[cipherLength];
        try
        {
            _aes.Decrypt(nonce, cipherBytes, tag, plaintextBytes);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            // Tamper, wrong key, or restored backup under a different key.
            // The inner exception is captured for the operator log; never
            // surfaced to API callers.
            throw new FieldDecryptionException(
                "AES-GCM authentication tag verification failed — the ciphertext has been tampered with or was encrypted under a different key.", ex);
        }
        catch (CryptographicException ex)
        {
            // Catch-all for any other crypto-layer failure (e.g. malformed nonce).
            throw new FieldDecryptionException(
                "AES-GCM decryption failed with an unexpected cryptographic error.", ex);
        }

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    /// <summary>
    /// Disposes the underlying AES-GCM primitive. The DI container invokes
    /// this on application shutdown for the singleton registration.
    /// </summary>
    public void Dispose() => _aes.Dispose();
}
