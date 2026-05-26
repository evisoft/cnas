using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// UC15 — Configure electronic service. Administers <c>ServicePassport</c> definitions.
/// </summary>
/// <remarks>
/// R0142 / CF 15.04 — passport definitions are versioned and append-only. The
/// <see cref="UpsertAsync"/> contract therefore branches on whether the input carries an
/// id: absent id =&gt; create version 1; present id =&gt; semantic-diff against the
/// addressed row's logical code and EITHER insert a new version (when the diff is
/// non-empty) OR no-op success (when the diff is empty). See
/// <see cref="Cnas.Ps.Core.Domain.ServicePassport.IsCurrent"/> for the catalogue-vs-history distinction.
/// </remarks>
public interface IServicePassportService
{
    /// <summary>
    /// Lists active service passports — one row per logical code (the
    /// <see cref="Cnas.Ps.Core.Domain.ServicePassport.IsCurrent"/> = <c>true</c> row). Historical versions
    /// are accessed via <see cref="GetHistoryAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Compact list rows for every current, active passport.</returns>
    Task<Result<IReadOnlyList<ServicePassportListItem>>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// R0528 / TOR CF 03.13 — diacritic + case-insensitive list variant that filters
    /// the result by a free-text query against the passport's Romanian display name.
    /// Routes through <c>DiacriticFolding.Fold</c> + <c>CnasDbFunctions.Unaccent</c>
    /// so an ASCII query (<c>"alocatii"</c>) matches the diacritic name
    /// (<c>"Alocații pentru copii"</c>).
    /// </summary>
    /// <param name="nameQuery">
    /// Optional free-text query (substring match against <c>NameRo</c>). Null / empty
    /// behaves identically to <see cref="ListAsync(CancellationToken)"/>.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Matching active passports in the same projection as the parameterless list.</returns>
    Task<Result<IReadOnlyList<ServicePassportListItem>>> ListAsync(
        string? nameQuery,
        CancellationToken cancellationToken = default);

    /// <summary>Returns full passport details by Sqid id (any revision — current or historical).</summary>
    /// <param name="id">Sqid-encoded passport row id (CLAUDE.md RULE 3).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Detail DTO including the Version + IsCurrent flag.</returns>
    Task<Result<ServicePassportDetailOutput>> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a service passport (admin only).</summary>
    /// <param name="input">
    /// Passport definition; <c>Id</c> null/empty triggers the create branch, otherwise the
    /// service runs the semantic-diff branch and emits a new version row when the diff is
    /// non-empty.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The Sqid id of the persisted row (new version row on a meaningful update).</returns>
    Task<Result<string>> UpsertAsync(ServicePassportInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0142 / CF 15.04 — returns the full history of versions for the passport addressed
    /// by <paramref name="id"/>, ordered by <c>ServicePassport.Version</c> DESC.
    /// </summary>
    /// <param name="id">Sqid id of any revision row (current or historical) — the service resolves the logical code.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>History entries, newest revision first.</returns>
    Task<Result<IReadOnlyList<ServicePassportHistoryItem>>> GetHistoryAsync(string id, CancellationToken cancellationToken = default);
}
