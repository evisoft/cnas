using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// Filesystem-backed <see cref="ICertificateStore"/>. Loads PFX / PKCS#12 files from disk
/// on first access, validates the optional thumbprint pin, and caches the parsed
/// <see cref="X509Certificate2"/> for the lifetime of the process.
/// </summary>
/// <remarks>
/// <para>
/// The store is registered as a singleton in the composition root because
/// <see cref="X509Certificate2"/> wraps an unmanaged key handle that should be loaded
/// once and reused for every outbound MGov request. The internal cache is a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by case-insensitive service
/// name (matches <see cref="MTlsOptions.Certificates"/>).
/// </para>
/// <para>
/// Failure modes are surfaced as values per CLAUDE.md §2.1:
/// <list type="bullet">
///   <item><see cref="ErrorCodes.CertificateNotConfigured"/> — no entry for the service.</item>
///   <item><see cref="ErrorCodes.CertificateLoadFailed"/> — file missing, wrong password, corrupt PFX.</item>
///   <item><see cref="ErrorCodes.CertificateThumbprintMismatch"/> — pin mismatch.</item>
/// </list>
/// The store implements <see cref="IDisposable"/> so the host can release the unmanaged
/// key handles on shutdown.
/// </para>
/// </remarks>
public sealed class FileCertificateStore : ICertificateStore, IDisposable
{
    private readonly MTlsOptions _options;
    private readonly ILogger<FileCertificateStore> _logger;
    private readonly ConcurrentDictionary<string, X509Certificate2> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initialises the store from the bound <see cref="MTlsOptions"/> snapshot.
    /// </summary>
    /// <param name="options">Configured certificate registrations.</param>
    /// <param name="logger">Structured logger. Never logs the PFX password.</param>
    public FileCertificateStore(
        IOptions<MTlsOptions> options,
        ILogger<FileCertificateStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value ?? new MTlsOptions();
        _logger = logger;
    }

    /// <inheritdoc />
    public Result<X509Certificate2> GetCertificate(string serviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Certificates.TryGetValue(serviceName, out var entry))
        {
            return Result<X509Certificate2>.Failure(
                ErrorCodes.CertificateNotConfigured,
                $"No mTLS client certificate is configured for service '{serviceName}'.");
        }

        return LoadOrGet(serviceName, entry);
    }

    /// <inheritdoc />
    public Result<X509Certificate2?> TryGetCertificate(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (!_options.Certificates.TryGetValue(serviceName, out var entry))
        {
            // No entry — successful null so the caller can fall back to Bearer auth.
            return Result<X509Certificate2?>.Success(null);
        }

        var loaded = LoadOrGet(serviceName, entry);
        return loaded.IsSuccess
            ? Result<X509Certificate2?>.Success(loaded.Value)
            : Result<X509Certificate2?>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
    }

    /// <summary>
    /// Returns the cached certificate for <paramref name="serviceName"/> if present,
    /// otherwise loads it from disk, validates the optional thumbprint pin, and inserts
    /// the parsed certificate into the cache.
    /// </summary>
    /// <param name="serviceName">Cache key (case-insensitive).</param>
    /// <param name="entry">Configuration entry describing the PFX file.</param>
    private Result<X509Certificate2> LoadOrGet(string serviceName, MTlsCertificateOptions entry)
    {
        if (_cache.TryGetValue(serviceName, out var cached))
        {
            return Result<X509Certificate2>.Success(cached);
        }

        X509Certificate2 cert;
        try
        {
            // X509CertificateLoader replaces the obsolete-on-net10 constructor.
            cert = string.IsNullOrEmpty(entry.Password)
                ? X509CertificateLoader.LoadPkcs12FromFile(
                    entry.Path,
                    password: null,
                    keyStorageFlags: X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable)
                : X509CertificateLoader.LoadPkcs12FromFile(
                    entry.Path,
                    entry.Password,
                    keyStorageFlags: X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex,
                "Failed to load mTLS certificate for service {Service} from {Path}.",
                serviceName, entry.Path);
            return Result<X509Certificate2>.Failure(
                ErrorCodes.CertificateLoadFailed,
                $"Failed to load PFX for service '{serviceName}': {ex.Message}");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex,
                "I/O error loading mTLS certificate for service {Service} from {Path}.",
                serviceName, entry.Path);
            return Result<X509Certificate2>.Failure(
                ErrorCodes.CertificateLoadFailed,
                $"Failed to read PFX file for service '{serviceName}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex,
                "Permission denied loading mTLS certificate for service {Service} from {Path}.",
                serviceName, entry.Path);
            return Result<X509Certificate2>.Failure(
                ErrorCodes.CertificateLoadFailed,
                $"Permission denied reading PFX for service '{serviceName}': {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(entry.Thumbprint)
            && !string.Equals(cert.Thumbprint, entry.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "mTLS certificate thumbprint mismatch for service {Service}: loaded={Loaded}, configured={Configured}.",
                serviceName, cert.Thumbprint, entry.Thumbprint);
            cert.Dispose();
            return Result<X509Certificate2>.Failure(
                ErrorCodes.CertificateThumbprintMismatch,
                $"Certificate thumbprint mismatch for service '{serviceName}'.");
        }

        // Race-safe insertion — if another thread inserted first, dispose ours.
        var stored = _cache.GetOrAdd(serviceName, cert);
        if (!ReferenceEquals(stored, cert))
        {
            cert.Dispose();
        }
        return Result<X509Certificate2>.Success(stored);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var kv in _cache)
        {
            try
            {
                kv.Value.Dispose();
            }
            catch
            {
                // Best-effort — never throw from Dispose. Unmanaged handle cleanup is
                // already handled by the finaliser if Dispose fails.
            }
        }
        _cache.Clear();
    }
}
