using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Localization;

/// <summary>
/// R0210 / TOR UI 007 / CF 17.16 — admin-facing upsert + approve surface over the
/// per-language translation values. The (key, language) tuple is the natural key;
/// the route addresses values by the key Sqid + language code, while the dedicated
/// approve endpoint addresses individual rows by their Sqid id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The controller applies the <c>CnasAdmin</c> policy;
/// here we only guard the "service called without an authenticated principal" case.
/// </para>
/// <para>
/// <b>Cache invalidation.</b> Every successful upsert + every approval triggers a
/// synchronous invalidation of the <see cref="ITranslationResolver"/> snapshot so the
/// new text is visible to the next call without waiting for the 60 s background
/// refresh tick.
/// </para>
/// <para>
/// <b>Audit.</b> <see cref="ApproveAsync"/> emits a Critical
/// <c>TRANSLATION.APPROVED</c> audit row carrying the key code + language so
/// reviewers' activity is traceable end-to-end. Upserts emit an Information-severity
/// <c>TRANSLATION.UPSERTED</c> row (operators tune copy frequently — promoting
/// every upsert to Critical would flood the audit explorer).
/// </para>
/// </remarks>
public interface ITranslationValueService
{
    /// <summary>
    /// Idempotent upsert for one (<paramref name="keySqid"/>, <paramref name="language"/>)
    /// value. Inserts when no row exists, updates the text + translator note otherwise.
    /// </summary>
    /// <param name="keySqid">Sqid-encoded id of the parent <see cref="Core.Domain.TranslationKey"/>.</param>
    /// <param name="language">ISO-639-1 language code (<c>ro</c>/<c>en</c>/<c>ru</c>).</param>
    /// <param name="input">Authoring payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resulting value DTO with the Sqid id assigned.</returns>
    Task<Result<TranslationValueDto>> UpsertAsync(
        string keySqid,
        string language,
        TranslationValueUpsertDto input,
        CancellationToken ct = default);

    /// <summary>
    /// Flips <see cref="Core.Domain.TranslationValue.IsApproved"/> to <c>true</c>
    /// and emits a Critical <c>TRANSLATION.APPROVED</c> audit row capturing the key
    /// code + language. Idempotent: approving an already-approved row is a no-op
    /// (no audit row is emitted) so re-running an approval workflow does not flood
    /// the audit explorer.
    /// </summary>
    /// <param name="valueSqid">Sqid-encoded id of the value row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated value DTO on success.</returns>
    Task<Result<TranslationValueDto>> ApproveAsync(
        string valueSqid,
        CancellationToken ct = default);
}
