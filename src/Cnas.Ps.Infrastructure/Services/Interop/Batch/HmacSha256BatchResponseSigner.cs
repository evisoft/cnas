using System.Security.Cryptography;
using Cnas.Ps.Application.Interop.Batch;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — HMAC-SHA256 implementation of
/// <see cref="IBatchResponseSigner"/>. The signing key is loaded once at
/// construction from <see cref="BatchResponseSigningOptions.HmacKeyBase64"/>
/// — the decoded byte array is owned by the singleton instance for the
/// lifetime of the process.
/// </summary>
public sealed class HmacSha256BatchResponseSigner : IBatchResponseSigner
{
    private readonly byte[] _keyBytes;

    /// <summary>Constructs the signer.</summary>
    /// <param name="options">Bound signing options (HMAC key in base64).</param>
    /// <exception cref="FormatException">When the configured key is not valid base64.</exception>
    /// <exception cref="ArgumentException">When the decoded key is empty.</exception>
    public HmacSha256BatchResponseSigner(IOptions<BatchResponseSigningOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var keyBase64 = options.Value.HmacKeyBase64;
        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            throw new ArgumentException(
                "BatchResponseSigningOptions.HmacKeyBase64 must be configured.",
                nameof(options));
        }
        var decoded = Convert.FromBase64String(keyBase64);
        if (decoded.Length == 0)
        {
            throw new ArgumentException(
                "BatchResponseSigningOptions.HmacKeyBase64 decoded to an empty byte array.",
                nameof(options));
        }
        _keyBytes = decoded;
    }

    /// <inheritdoc />
    public Task<string> SignAsync(byte[] responseFileBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responseFileBytes);
        using var hmac = new HMACSHA256(_keyBytes);
        var mac = hmac.ComputeHash(responseFileBytes);
        return Task.FromResult(Convert.ToBase64String(mac));
    }

    /// <inheritdoc />
    public Task<bool> VerifyAsync(
        byte[] responseFileBytes,
        string signatureBase64,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responseFileBytes);
        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            return Task.FromResult(false);
        }
        byte[] candidate;
        try
        {
            candidate = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return Task.FromResult(false);
        }
        using var hmac = new HMACSHA256(_keyBytes);
        var actual = hmac.ComputeHash(responseFileBytes);
        // Timing-safe comparison — never branch on byte equality before
        // exhausting the loop.
        return Task.FromResult(CryptographicOperations.FixedTimeEquals(actual, candidate));
    }
}
