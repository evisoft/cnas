using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Identity;

/// <summary>
/// R0211 / TOR UI 003 — resolves a user's persisted UI-language preference into
/// the canonical lowercase ISO code consumed by the request-localisation pipeline.
/// The resolver isolates the database lookup behind a thin interface so the
/// custom <c>RequestCultureProvider</c> registered in the API composition root
/// does not have to take an EF Core dependency directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fallback contract.</b> The resolver NEVER fails the request: when the user
/// row is missing or carries no preference, it returns the system default
/// <c>"ro"</c>. This is by design — a malformed / missing preference must not
/// short-circuit the request pipeline.
/// </para>
/// <para>
/// <b>Caching.</b> The current implementation does NOT cache; each call performs
/// one indexed lookup against the <c>UserProfiles</c> table. Caching is left as a
/// future optimisation behind a dedicated decorator because the per-request hit
/// is a single primary-key lookup and the data is small.
/// </para>
/// </remarks>
public interface IPreferredLanguageResolver
{
    /// <summary>
    /// Resolves the user's preferred UI language from their profile row.
    /// </summary>
    /// <param name="userSqid">
    /// Sqid-encoded id of the user whose preference to resolve. When null or
    /// undecodable, the resolver returns the system default <c>"ro"</c> rather
    /// than failing — see remarks for the never-fail contract.
    /// </param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the resolved lowercase ISO code
    /// (one of <c>"ro"</c>, <c>"en"</c>, <c>"ru"</c>; falls back to <c>"ro"</c>
    /// when no profile or no preference is found).
    /// </returns>
    Task<Result<string>> ResolveAsync(string? userSqid, CancellationToken cancellationToken = default);
}
