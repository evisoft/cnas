using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Security;

/// <summary>Supported server-to-server callback providers.</summary>
public enum CallbackSignatureProvider
{
    /// <summary>MPay payment details / confirmation callback surface.</summary>
    MPay,

    /// <summary>MNotify bounce / delivery-failure callback surface.</summary>
    MNotify,

    /// <summary>MSign signing-readiness callback surface.</summary>
    MSign,
}

/// <summary>Result returned by <see cref="ICallbackSignatureVerifier"/>.</summary>
public sealed record CallbackSignatureVerificationResult(bool IsSuccess, string? ErrorMessage)
{
    /// <summary>Creates a successful verification result.</summary>
    public static CallbackSignatureVerificationResult Success() => new(true, null);

    /// <summary>Creates a failed verification result.</summary>
    public static CallbackSignatureVerificationResult Failure(string message) => new(false, message);
}

/// <summary>Verifies HMAC signatures on anonymous server-to-server callbacks.</summary>
public interface ICallbackSignatureVerifier
{
    /// <summary>
    /// Validates the timestamped HMAC over the provider name and canonical payload.
    /// </summary>
    CallbackSignatureVerificationResult Verify(
        CallbackSignatureProvider provider,
        string canonicalPayload,
        IHeaderDictionary headers);
}

/// <summary>Bound options for callback HMAC validation.</summary>
public sealed class CallbackSignatureOptions
{
    /// <summary>Configuration section path.</summary>
    public const string SectionName = "Cnas:CallbackSignatures";

    /// <summary>Maximum accepted clock skew for signed callbacks.</summary>
    public TimeSpan MaxSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>MPay signing options.</summary>
    public ProviderSignatureOptions MPay { get; set; } = new();

    /// <summary>MNotify signing options.</summary>
    public ProviderSignatureOptions MNotify { get; set; } = new();

    /// <summary>MSign signing options.</summary>
    public ProviderSignatureOptions MSign { get; set; } = new();
}

/// <summary>Per-provider callback HMAC settings.</summary>
public sealed class ProviderSignatureOptions
{
    /// <summary>Shared secret used as the HMAC-SHA256 key. Required for live callbacks.</summary>
    public string SigningKey { get; set; } = string.Empty;
}

/// <summary>
/// Header-based HMAC verifier for anonymous callback endpoints. The signature covers
/// provider, timestamp, and route/payload identifiers so a valid callback cannot be
/// replayed across endpoints or providers.
/// </summary>
public sealed class CallbackSignatureVerifier(
    IOptions<CallbackSignatureOptions> options,
    ICnasTimeProvider clock) : ICallbackSignatureVerifier
{
    /// <summary>ISO-8601 UTC timestamp header.</summary>
    public const string TimestampHeader = "X-CNAS-Callback-Timestamp";

    /// <summary>Base64 HMAC-SHA256 header, prefixed with <c>sha256=</c>.</summary>
    public const string SignatureHeader = "X-CNAS-Callback-Signature";

    private readonly CallbackSignatureOptions _options = options.Value;
    private readonly ICnasTimeProvider _clock = clock;

    /// <inheritdoc />
    public CallbackSignatureVerificationResult Verify(
        CallbackSignatureProvider provider,
        string canonicalPayload,
        IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        ArgumentNullException.ThrowIfNull(headers);

        var providerOptions = provider switch
        {
            CallbackSignatureProvider.MPay => _options.MPay,
            CallbackSignatureProvider.MNotify => _options.MNotify,
            CallbackSignatureProvider.MSign => _options.MSign,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
        if (string.IsNullOrWhiteSpace(providerOptions.SigningKey))
        {
            return CallbackSignatureVerificationResult.Failure("Callback signing key is not configured.");
        }

        var timestampRaw = headers[TimestampHeader].ToString();
        if (!DateTimeOffset.TryParse(
                timestampRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return CallbackSignatureVerificationResult.Failure("Callback timestamp is missing or invalid.");
        }

        var skew = (_clock.UtcNow - timestamp.UtcDateTime).Duration();
        if (skew > _options.MaxSkew)
        {
            return CallbackSignatureVerificationResult.Failure("Callback timestamp is outside the replay window.");
        }

        var supplied = headers[SignatureHeader].ToString();
        const string prefix = "sha256=";
        if (!supplied.StartsWith(prefix, StringComparison.Ordinal))
        {
            return CallbackSignatureVerificationResult.Failure("Callback signature is missing or invalid.");
        }

        var canonical = string.Join(
            '\n',
            provider.ToString(),
            timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            canonicalPayload);
        var expectedBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(providerOptions.SigningKey),
            Encoding.UTF8.GetBytes(canonical));
        var expected = Convert.ToBase64String(expectedBytes);

        var suppliedBytes = Encoding.ASCII.GetBytes(supplied[prefix.Length..]);
        var expectedBytesAscii = Encoding.ASCII.GetBytes(expected);
        return suppliedBytes.Length == expectedBytesAscii.Length
               && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytesAscii)
            ? CallbackSignatureVerificationResult.Success()
            : CallbackSignatureVerificationResult.Failure("Callback signature is invalid.");
    }
}
