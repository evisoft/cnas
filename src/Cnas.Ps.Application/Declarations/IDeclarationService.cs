using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Declarations;

/// <summary>
/// R0810 / R0811 / R0812 / TOR BP 1.2 (Annex 8 — Declarații) — service façade
/// for the contribution-declarations registry. Owns the three registration
/// paths (SFS feed, CNAS desk, other documents) plus the per-row adjustment /
/// cancellation lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Each registration method emits one Notice-severity audit row with a stable
/// event code <c>DECLARATION.REGISTERED.&lt;Kind&gt;</c>; <c>AdjustAsync</c>
/// emits <c>DECLARATION.ADJUSTED</c> and <c>CancelAsync</c> emits
/// <c>DECLARATION.CANCELLED</c>, both at Notice severity.
/// </para>
/// <para>
/// All identifiers crossing the boundary are Sqid-encoded per CLAUDE.md RULE
/// 3; internally the service decodes them to raw <c>long</c> primary keys
/// before touching the DbContext.
/// </para>
/// </remarks>
public interface IDeclarationService
{
    /// <summary>
    /// R0810 / BP 1.2-A — registers a declaration ingested from the automated
    /// monthly SI SFS feed. The service implicitly stamps
    /// <c>Kind = DeclarationKind.Sfs</c>.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="DeclarationDto"/>; on duplicate-key
    /// collision <see cref="ErrorCodes.Conflict"/> with stable code
    /// <c>DECLARATION_DUPLICATE</c> in the error message; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on unknown payer
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<DeclarationDto>> RegisterFromSfsAsync(
        DeclarationFromSfsInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0811 / BP 1.2-B — registers a paper declaration submitted at a CNAS
    /// desk. The validator restricts <c>Kind</c> to <c>BassFour</c>, <c>Bass</c>,
    /// <c>BassAn</c>, or <c>Pre2018</c>.
    /// </summary>
    /// <param name="input">Validated input envelope (Kind constrained by validator).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted DTO; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on duplicate-key collision
    /// <see cref="ErrorCodes.Conflict"/>; on unknown payer
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<DeclarationDto>> RegisterAtCnasAsync(
        DeclarationAtCnasInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0812 / BP 1.2-C — registers a contribution recalculated from a
    /// supporting document. The validator restricts <c>Kind</c> to
    /// <c>Control</c>, <c>CourtDecision</c>, or <c>Other</c>.
    /// </summary>
    /// <param name="input">Validated input envelope (Kind constrained by validator).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted DTO; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on duplicate-key collision
    /// <see cref="ErrorCodes.Conflict"/>; on unknown payer
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<DeclarationDto>> RegisterFromOtherDocumentAsync(
        DeclarationFromOtherDocumentInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions an existing declaration to
    /// <see cref="Cnas.Ps.Core.Domain.DeclarationStatus.Adjusted"/> by stamping a
    /// supersession amount. The original <c>DeclaredContributionAmount</c> is
    /// preserved.
    /// </summary>
    /// <param name="declarationId">Raw bigint id of the row.</param>
    /// <param name="adjustedAmount">New amount (MDL, ≥ 0).</param>
    /// <param name="reason">Operator rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the updated DTO; on cancelled row
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on bad reason
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<DeclarationDto>> AdjustAsync(
        long declarationId,
        decimal adjustedAmount,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions an existing declaration to
    /// <see cref="Cnas.Ps.Core.Domain.DeclarationStatus.Cancelled"/>. Cancelled
    /// rows are excluded from R0813 monthly totals.
    /// </summary>
    /// <param name="declarationId">Raw bigint id of the row.</param>
    /// <param name="reason">Cancellation rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on already-cancelled
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result> CancelAsync(
        long declarationId,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Lists every (non-deleted) declaration for the supplied payer inside the
    /// <c>[fromMonth, toMonth]</c> window, ordered by
    /// <see cref="Cnas.Ps.Core.Domain.Declaration.ReportingMonth"/> DESC then by
    /// <see cref="Cnas.Ps.Core.Domain.Declaration.Kind"/> ASC.
    /// </summary>
    /// <param name="contributorId">Raw bigint id of the payer.</param>
    /// <param name="fromMonth">Inclusive lower bound (day = 1).</param>
    /// <param name="toMonth">Inclusive upper bound (day = 1).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>An ordered list — empty when the payer has no declarations in the window.</returns>
    Task<IReadOnlyList<DeclarationDto>> ListForPayerAsync(
        long contributorId,
        DateOnly fromMonth,
        DateOnly toMonth,
        CancellationToken ct = default);

    /// <summary>
    /// R0821 / BP 1.2-L / Annex 1 §8.1.3 — attaches a scanned copy of the
    /// paper declaration plus optional OCR metadata to an existing row. The
    /// underlying blob upload is delegated to
    /// <c>IAttachmentService.UploadAsync</c> with
    /// <c>OwnerEntityType="Declaration"</c>,
    /// <c>Category=AttachmentCategory.LegalDocument</c>, and
    /// <c>SensitivityLabel=Confidential</c>. On success the row's
    /// <see cref="Cnas.Ps.Core.Domain.Declaration.HasScannedCopy"/> flag flips
    /// to <see langword="true"/> and the supplied OCR metadata is persisted
    /// onto the row. Emits a Notice audit
    /// <c>DECLARATION.SCANNED_COPY_ATTACHED</c>.
    /// </summary>
    /// <param name="declarationId">Raw bigint id of the target row.</param>
    /// <param name="input">Upload payload (file + optional OCR metadata).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the refreshed <see cref="DeclarationDto"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on cancelled row
    /// <see cref="ErrorCodes.Conflict"/>; on malformed payload
    /// <see cref="ErrorCodes.ValidationFailed"/>; on upload failure the stable
    /// error code surfaced by the attachment service.
    /// </returns>
    Task<Result<DeclarationDto>> AttachScannedCopyAsync(
        long declarationId,
        ScannedDeclarationAttachmentInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0822 / BP 1.2-M / Annex 1 §8.1.3 — server-side paged + budget-gated
    /// explorer call. The R0163 QBE filter is converted to a typed LINQ
    /// predicate against the
    /// <c>QueryBudgetRegistries.Declaration</c> schema; the R0167 query-budget
    /// guard is consulted BEFORE materialisation.
    /// </summary>
    /// <param name="input">QBE filter + date window + paging slots.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated <see cref="DeclarationsListPageDto"/>;
    /// <see cref="ErrorCodes.QueryTooBroad"/> when the budget guard refuses;
    /// QBE-specific failure codes (<see cref="ErrorCodes.QbeFieldNotQueryable"/>,
    /// <see cref="ErrorCodes.QbeOperatorNotSupported"/>, etc.) on bad QBE
    /// payload; <see cref="ErrorCodes.ValidationFailed"/> on bad paging.
    /// </returns>
    Task<Result<DeclarationsListPageDto>> SearchAsync(
        DeclarationsSearchInput input,
        CancellationToken ct = default);
}
