using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Pluggable abstraction over a secrets-management backend. The CNAS PS application
/// reads sensitive configuration (database connection strings, MGov bearer tokens,
/// MinIO credentials, JWT signing material, ...) through this interface rather than
/// directly from <c>Microsoft.Extensions.Configuration.IConfiguration</c> so
/// that the storage mechanism (environment variables in dev, HashiCorp Vault KV v2
/// in production) can be swapped at the composition root without recompiling
/// downstream services.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are registered as singletons (stateless network clients) and must
/// be safe to call concurrently. Callers should treat the returned value as
/// short-lived and re-fetch on demand rather than caching across the process — the
/// production Vault adapter rotates tokens and rewrites secrets out-of-band.
/// </para>
/// <para>
/// All failures are surfaced via <see cref="Result{T}"/> with stable codes from
/// <see cref="ErrorCodes"/>:
/// <list type="bullet">
///   <item><see cref="ErrorCodes.SecretNotFound"/> — the named secret does not exist.</item>
///   <item><see cref="ErrorCodes.SecretsBackendUnavailable"/> — the backend timed out, refused the connection, or returned 5xx.</item>
/// </list>
/// Exceptions are reserved for genuinely exceptional situations (OOM, programmer
/// errors); business-level failures are values per CLAUDE.md §2.1.
/// </para>
/// </remarks>
public interface ISecretsProvider
{
    /// <summary>
    /// Resolves a single secret by its fully qualified key.
    /// </summary>
    /// <param name="key">
    /// Backend-agnostic key identifying the secret. For the environment-variable
    /// adapter this is the variable name (e.g. <c>POSTGRES_CONNECTION_STRING</c>);
    /// for the Vault KV v2 adapter this is the path under the configured mount
    /// (e.g. <c>cnas/postgres</c>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token honoured by the backend call.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the secret value on success;
    /// <see cref="ErrorCodes.SecretNotFound"/> when the key is unknown;
    /// <see cref="ErrorCodes.SecretsBackendUnavailable"/> when the backend cannot be reached.
    /// </returns>
    Task<Result<string>> GetSecretAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a grouped set of secrets stored under a common path/prefix as a
    /// dictionary of <c>name → value</c>.
    /// </summary>
    /// <param name="path">
    /// Logical grouping. For the environment-variable adapter this is a prefix
    /// followed by a double underscore (e.g. <c>MGOV</c> reads
    /// <c>MGOV__MPASS_BEARER</c>, <c>MGOV__MSIGN_BEARER</c>, ...); for the Vault
    /// adapter this is the KV v2 secret path (e.g. <c>cnas/mgov</c>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token honoured by the backend call.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the inner key/value dictionary on
    /// success — the dictionary is empty (not a failure) when no secrets match the
    /// path; <see cref="ErrorCodes.SecretsBackendUnavailable"/> when the backend
    /// cannot be reached; <see cref="ErrorCodes.SecretNotFound"/> when the backend
    /// distinguishes a missing path (e.g. Vault 404).
    /// </returns>
    Task<Result<IReadOnlyDictionary<string, string>>> GetSecretsAsync(string path, CancellationToken cancellationToken = default);
}
