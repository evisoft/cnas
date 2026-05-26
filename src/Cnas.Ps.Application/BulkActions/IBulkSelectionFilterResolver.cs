using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.BulkActions;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — per-registry strategy that interprets an opaque
/// JSON filter envelope and returns the matching primary keys. Implementations live in
/// the Infrastructure layer (they touch <see cref="Cnas.Ps.Application.Abstractions.ICnasDbContext"/>);
/// the application layer depends only on this contract so the bulk-selection service
/// stays registry-agnostic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dispatch.</b> The bulk-selection service consults
/// <see cref="IBulkSelectionFilterResolverFactory"/> at runtime, keyed by the
/// selection's <c>Registry</c> string. A missing resolver for a registered registry
/// is a configuration bug and surfaces as <see cref="ErrorCodes.Internal"/>.
/// </para>
/// <para>
/// <b>Materialisation contract.</b> The implementation returns the raw primary keys.
/// The selection service unions them with <c>ExplicitIncludeIds</c> and subtracts
/// <c>ExplicitExcludeIds</c> on the way out; resolvers MUST NOT apply those overlays
/// themselves.
/// </para>
/// <para>
/// <b>Diacritic-insensitive matching.</b> Resolvers that interpret a free-text query
/// term MUST use <c>CnasDbFunctions.Unaccent</c> on the relational path so the
/// R0162 / CF 03.13 diacritic-insensitive contract is preserved. The InMemory test
/// provider falls back to <c>DiacriticFolding.Fold</c>.
/// </para>
/// </remarks>
public interface IBulkSelectionFilterResolver
{
    /// <summary>
    /// Stable registry code this resolver handles (must match one of the
    /// <see cref="BulkRegistries"/> constants). Used as the dispatch key.
    /// </summary>
    string Registry { get; }

    /// <summary>
    /// Resolves the filter envelope to a materialised id list against the live DB.
    /// </summary>
    /// <param name="filterJson">
    /// Opaque JSON filter envelope supplied at selection-create time. Implementations
    /// validate the shape internally — a malformed JSON or an unrecognised filter
    /// key should return <see cref="ErrorCodes.ValidationFailed"/> rather than
    /// throwing.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the matched primary keys (possibly empty). Failures: malformed
    /// filter shapes surface as <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<IReadOnlyList<long>>> ResolveAsync(string filterJson, CancellationToken ct = default);
}

/// <summary>
/// R0166 — factory for per-registry filter resolvers, keyed by registry code. The
/// concrete implementation is registered as a singleton in DI and built once at
/// startup from the registered <see cref="IBulkSelectionFilterResolver"/> set.
/// </summary>
public interface IBulkSelectionFilterResolverFactory
{
    /// <summary>
    /// Resolves the filter resolver registered for the supplied registry, or returns
    /// a failed <see cref="Result{T}"/> when no resolver matches.
    /// </summary>
    /// <param name="registry">Stable registry code (e.g. <c>Cerere</c>).</param>
    /// <returns>
    /// On success the matching resolver. Failures:
    /// <see cref="ErrorCodes.ValidationFailed"/> when the registry is unknown.
    /// </returns>
    Result<IBulkSelectionFilterResolver> ForRegistry(string registry);
}
