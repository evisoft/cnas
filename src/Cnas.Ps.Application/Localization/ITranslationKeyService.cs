using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Localization;

/// <summary>
/// R0210 / TOR UI 007 / CF 17.16 — admin-facing CRUD surface over the
/// translation-key registry. Every mutating method captures the caller via
/// <c>ICallerContext</c>; reads enforce only the authentication invariant. Pairs with
/// <see cref="ITranslationValueService"/> for per-language value upserts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The controller applies the
/// <c>CnasAdmin</c> policy; here we only guard the "service called without an
/// authenticated principal" case via <c>ICallerContext.UserId</c> presence.
/// </para>
/// <para>
/// <b>Cache invalidation.</b> The translation registry is consumed via a cached
/// <see cref="ITranslationResolver"/>; the value-side service triggers a snapshot
/// invalidation after every mutation. Pure key-CRUD operations (create / update
/// metadata) do NOT trigger an invalidation because the resolver only consumes
/// (code, language) values — but they are still part of the same logical surface
/// so they live here for discoverability.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> Every method that emits or consumes a key id uses the
/// Sqid string form per CLAUDE.md RULE 3. The natural key <see cref="TranslationKey.Code"/>
/// stays human-readable in the operator's URL bar but mutations address rows via Sqid.
/// </para>
/// </remarks>
public interface ITranslationKeyService
{
    /// <summary>
    /// Lists every active translation key, optionally filtered by module. Ordered by
    /// <see cref="TranslationKey.Code"/> ascending. Soft-deleted rows are excluded.
    /// </summary>
    /// <param name="module">Optional module filter; null returns every module.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success the list of key DTOs (with every persisted value rolled up).</returns>
    Task<Result<IReadOnlyList<TranslationKeyDto>>> ListAsync(
        string? module,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches the key identified by <paramref name="sqid"/>, or
    /// <see cref="ErrorCodes.NotFound"/> when no row matches.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the key row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The key DTO on success.</returns>
    Task<Result<TranslationKeyDto>> GetAsync(string sqid, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new translation key. The validator pre-checks the code shape; the
    /// service rejects with <see cref="ErrorCodes.Conflict"/> when a row with the
    /// same code already exists.
    /// </summary>
    /// <param name="input">Authoring fields for the new key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created key DTO carrying its assigned Sqid id.</returns>
    Task<Result<TranslationKeyDto>> CreateAsync(
        TranslationKeyUpsertDto input,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the metadata of an existing key (description / module). The code itself
    /// is immutable — renaming a code is a breaking change for every caller and is
    /// modelled as "create new + delete old" by the admin UI.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the key row.</param>
    /// <param name="input">Authoring fields. <c>Code</c> must equal the existing row's code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated key DTO on success.</returns>
    Task<Result<TranslationKeyDto>> UpdateAsync(
        string sqid,
        TranslationKeyUpsertDto input,
        CancellationToken ct = default);
}
