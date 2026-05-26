using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Help;

/// <summary>
/// R0225 / TOR UI 015 — admin-facing upsert surface over the per-language help
/// topic translations. The (topic, language) tuple is the natural key; the route
/// addresses translations by topic Sqid + language code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The controller applies the <c>CnasAdmin</c> policy;
/// here we only guard the "service called without an authenticated principal" case.
/// </para>
/// <para>
/// <b>Cache invalidation.</b> Every successful upsert triggers a synchronous
/// invalidation of the <see cref="IHelpResolver"/> snapshot.
/// </para>
/// </remarks>
public interface IHelpTopicTranslationService
{
    /// <summary>
    /// Idempotent upsert for one (<paramref name="topicSqid"/>, <paramref name="language"/>)
    /// translation. Inserts on first call, updates the title + body + note otherwise.
    /// </summary>
    /// <param name="topicSqid">Sqid-encoded id of the parent <see cref="Core.Domain.HelpTopic"/>.</param>
    /// <param name="language">ISO-639-1 language code.</param>
    /// <param name="input">Authoring payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resulting translation DTO with the Sqid id assigned.</returns>
    Task<Result<HelpTopicTranslationDto>> UpsertAsync(
        string topicSqid,
        string language,
        HelpTopicTranslationUpsertDto input,
        CancellationToken ct = default);

    /// <summary>
    /// Flips <see cref="Core.Domain.HelpTopicTranslation.IsApproved"/> to <c>true</c>
    /// and emits a Critical <c>HELP.APPROVED</c> audit row.
    /// </summary>
    /// <param name="translationSqid">Sqid-encoded id of the translation row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated translation DTO on success.</returns>
    Task<Result<HelpTopicTranslationDto>> ApproveAsync(
        string translationSqid,
        CancellationToken ct = default);
}
