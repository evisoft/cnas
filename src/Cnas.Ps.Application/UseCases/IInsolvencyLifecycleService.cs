using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — dedicated insolvency lifecycle
/// service. Splits the historical <c>Contributor.IsInsolvent</c> single-bit
/// flag (no history, no rationale) into a registry of fully-audited
/// <c>InsolvencyCase</c> rows with their own claims + payments sub-tables.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sqid contract.</b> All identifiers crossing the API boundary are
/// Sqid-encoded per CLAUDE.md RULE 3. Returned DTOs carry Sqid strings;
/// inputs accept Sqid strings and decode internally.
/// </para>
/// <para>
/// <b>Audit.</b> Every state-changing call emits a Critical-severity audit
/// row (<c>INSOLVENCY.OPENED</c> / <c>INSOLVENCY.RESOLVED</c>) so an
/// investigator can later reconstruct the citizen-side insolvency story.
/// Audit payloads are PII-free — they carry the raw <c>ContributorId</c>
/// and lifecycle metadata only.
/// </para>
/// </remarks>
public interface IInsolvencyLifecycleService
{
    /// <summary>
    /// Opens a new insolvency lifecycle case against the supplied contributor.
    /// Concurrently flips <see cref="Cnas.Ps.Core.Domain.Contributor.IsInsolvent"/>
    /// to <c>true</c> in the same atomic save. Refuses when an open case
    /// already exists for the same payer (no double-open).
    /// </summary>
    /// <param name="contributorSqid">Sqid-encoded id of the payer.</param>
    /// <param name="reason">Operator-supplied rationale (3..500 chars).</param>
    /// <param name="insolvencyDate">Effective insolvency date; must not be in the future.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly-persisted case projected as a <see cref="InsolvencyCaseDto"/>.</returns>
    Task<Result<InsolvencyCaseDto>> OpenAsync(
        string contributorSqid,
        string reason,
        DateOnly insolvencyDate,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves an open insolvency case. Stamps <c>ResolvedAtUtc</c> +
    /// <c>Resolution</c>, flips
    /// <see cref="Cnas.Ps.Core.Domain.Contributor.IsInsolvent"/> back to false,
    /// and emits a <c>INSOLVENCY.RESOLVED</c> Critical-severity audit row.
    /// Refuses on an already-resolved case with
    /// <see cref="ErrorCodes.Conflict"/>.
    /// </summary>
    /// <param name="caseSqid">Sqid-encoded id of the case to resolve.</param>
    /// <param name="resolution">Resolution rationale (3..500 chars).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success; otherwise a failure carrying one of
    /// <see cref="ErrorCodes.InvalidSqid"/>, <see cref="ErrorCodes.NotFound"/>,
    /// <see cref="ErrorCodes.Conflict"/>, or <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result> ResolveAsync(
        string caseSqid,
        string resolution,
        CancellationToken ct = default);

    /// <summary>
    /// Lists every currently-open insolvency case ordered by
    /// <c>OpenedAtUtc</c> ascending (oldest first).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The open cases as DTO projections.</returns>
    Task<Result<IReadOnlyList<InsolvencyCaseDto>>> ListActiveAsync(
        CancellationToken ct = default);

    // ─── R0834 — Claims + payments sub-table surface ───

    /// <summary>Registers a claim against the supplied open insolvency case.</summary>
    /// <param name="caseSqid">Sqid-encoded id of the parent case.</param>
    /// <param name="input">Claim input payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly-persisted claim as a DTO.</returns>
    Task<Result<InsolvencyClaimDto>> AddClaimAsync(
        string caseSqid,
        InsolvencyClaimInputDto input,
        CancellationToken ct = default);

    /// <summary>Lists every claim row registered against the supplied case (ordered by <c>IncurredOn</c> ascending).</summary>
    /// <param name="caseSqid">Sqid-encoded id of the parent case.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<InsolvencyClaimDto>>> ListClaimsAsync(
        string caseSqid,
        CancellationToken ct = default);

    /// <summary>Registers a payment against the supplied open insolvency case.</summary>
    /// <param name="caseSqid">Sqid-encoded id of the parent case.</param>
    /// <param name="input">Payment input payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly-persisted payment as a DTO.</returns>
    Task<Result<InsolvencyPaymentDto>> AddPaymentAsync(
        string caseSqid,
        InsolvencyPaymentInputDto input,
        CancellationToken ct = default);

    /// <summary>Lists every payment row registered against the supplied case (ordered by <c>PaymentDate</c> ascending).</summary>
    /// <param name="caseSqid">Sqid-encoded id of the parent case.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<InsolvencyPaymentDto>>> ListPaymentsAsync(
        string caseSqid,
        CancellationToken ct = default);
}
