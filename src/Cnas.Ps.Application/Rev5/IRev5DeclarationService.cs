using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Rev5;

/// <summary>
/// R0910 / TOR BP 2.2-A — service façade for the REV-5 declarations registry.
/// Owns the register / per-row adjust / cancel lifecycle and the projection
/// of registered rows into <c>PersonalAccountEntry</c> rows on the citizen's
/// personal account.
/// </summary>
/// <remarks>
/// <para>
/// All identifiers crossing the boundary are Sqid-encoded per CLAUDE.md
/// RULE 3; internally the service decodes them to raw <c>long</c> primary
/// keys. Money fields are bounded by the validator. All timestamps come from
/// <c>ICnasTimeProvider</c> — never <see cref="DateTime.UtcNow"/>.
/// </para>
/// <para>
/// <b>Partial-success registration.</b>
/// <see cref="RegisterAsync"/> never aborts a declaration because one
/// insured-person row cannot be resolved to a Solicitant. Rows whose IDNP
/// hash misses are still persisted; the unmatched count + first 10 hash
/// prefixes are surfaced in the response so operators can chase the missing
/// CNAS-side identity record.
/// </para>
/// </remarks>
public interface IRev5DeclarationService
{
    /// <summary>
    /// R0910 / BP 2.2-A — registers a REV-5 declaration header + its
    /// per-employee child rows. For every row whose IDNP hash resolves to a
    /// known Solicitant, projects a corresponding <c>PersonalAccountEntry</c>
    /// with <c>SourceCode = "REV5"</c>.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="Rev5DeclarationDto"/> with the
    /// unmatched-row signal populated; on duplicate natural key
    /// <see cref="ErrorCodes.Conflict"/> with stable
    /// <c>REV5_DUPLICATE</c> in the message; on closed reporting month
    /// <see cref="ErrorCodes.ValidationFailed"/> with stable
    /// <c>MONTH_CLOSED</c> in the message; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on unknown employer
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<Rev5DeclarationDto>> RegisterAsync(
        Rev5DeclarationRegisterInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0910 — adjusts a single REV-5 row's contribution amount and
    /// re-projects the corresponding <c>PersonalAccountEntry</c> upsert. The
    /// parent declaration transitions to
    /// <see cref="Cnas.Ps.Core.Domain.Rev5DeclarationStatus.Adjusted"/> and
    /// its <c>TotalDeclaredAmount</c> is recomputed.
    /// </summary>
    /// <param name="rev5DeclarationId">Raw bigint id of the parent header.</param>
    /// <param name="insuredPersonNationalIdHash">IDNP hash identifying the row.</param>
    /// <param name="adjustedContributionAmount">New contribution amount (MDL, ≥ 0).</param>
    /// <param name="reason">Operator rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the refreshed <see cref="Rev5DeclarationDto"/>; on missing
    /// declaration / row <see cref="ErrorCodes.NotFound"/>; on cancelled
    /// declaration <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result<Rev5DeclarationDto>> AdjustRowAsync(
        long rev5DeclarationId,
        string insuredPersonNationalIdHash,
        decimal adjustedContributionAmount,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// R0910 — cancels a REV-5 declaration and rolls back every
    /// <c>PersonalAccountEntry</c> the declaration projected. Audit Critical
    /// because cancellation materially alters the citizen-facing personal
    /// account.
    /// </summary>
    /// <param name="rev5DeclarationId">Raw bigint id of the parent header.</param>
    /// <param name="reason">Operator rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on already-cancelled
    /// <see cref="ErrorCodes.Conflict"/>; on bad reason
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result> CancelAsync(long rev5DeclarationId, string reason, CancellationToken ct = default);

    /// <summary>
    /// R0910 — fetches a single REV-5 declaration by surrogate id.
    /// </summary>
    /// <param name="id">Raw bigint id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO when the row exists; <c>null</c> otherwise.</returns>
    Task<Rev5DeclarationDto?> GetAsync(long id, CancellationToken ct = default);
}
