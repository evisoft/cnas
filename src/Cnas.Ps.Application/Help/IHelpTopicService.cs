using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Help;

/// <summary>
/// R0225 / TOR UI 015 — admin-facing CRUD surface over the contextual-help topic
/// registry. Pairs with <see cref="IHelpTopicTranslationService"/> for per-language
/// title + body upserts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The controller applies the <c>CnasAdmin</c> policy;
/// the service only guards the "service called without an authenticated principal"
/// case via <c>ICallerContext.UserId</c> presence.
/// </para>
/// <para>
/// <b>Cache invalidation.</b> The help registry is consumed via a cached
/// <see cref="IHelpResolver"/>; the topic-side service triggers a snapshot
/// invalidation after every mutation so the new shape is visible to the next
/// resolver call.
/// </para>
/// </remarks>
public interface IHelpTopicService
{
    /// <summary>
    /// Lists every active help topic, optionally filtered by module. Ordered by
    /// <see cref="Core.Domain.HelpTopic.Code"/> ascending.
    /// </summary>
    /// <param name="module">Optional module filter; null returns every module.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success the list of topic DTOs (with every translation rolled up).</returns>
    Task<Result<IReadOnlyList<HelpTopicDto>>> ListAsync(
        string? module,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches the topic identified by <paramref name="sqid"/>, or
    /// <see cref="ErrorCodes.NotFound"/> when no row matches.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the topic row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The topic DTO on success.</returns>
    Task<Result<HelpTopicDto>> GetAsync(string sqid, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new topic. The validator pre-checks the code shape; the service
    /// rejects with <see cref="ErrorCodes.Conflict"/> when a row with the same code
    /// already exists.
    /// </summary>
    /// <param name="input">Authoring payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created topic DTO carrying its assigned Sqid id.</returns>
    Task<Result<HelpTopicDto>> CreateAsync(
        HelpTopicUpsertDto input,
        CancellationToken ct = default);

    /// <summary>
    /// Updates an existing topic. The code itself is immutable — renaming a code is
    /// a breaking change for every UI binding and is modelled as "create new + delete
    /// old" by the admin UI.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the topic row.</param>
    /// <param name="input">Authoring payload (Code must equal the existing row's code).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated topic DTO on success.</returns>
    Task<Result<HelpTopicDto>> UpdateAsync(
        string sqid,
        HelpTopicUpsertDto input,
        CancellationToken ct = default);
}
