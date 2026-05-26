using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// Annex 1 — <c>Plătitori de contribuții</c> (contributor) registry CRUD façade.
/// Owns the full lifecycle of a Contributor entity: registration, lookup, search,
/// insolvency flag toggling, and soft-deletion (deregistration).
/// </summary>
/// <remarks>
/// All identifiers crossing this façade are external Sqid strings or canonical IDNO
/// strings per CLAUDE.md RULE 3. Business failures are returned as <see cref="Result"/>
/// values; only true exceptions (network, OOM) throw.
/// </remarks>
public interface IContributorService
{
    /// <summary>
    /// Registers a new contributor. Validates the IDNO via
    /// <see cref="Cnas.Ps.Core.ValueObjects.Idno.TryCreate(string?)"/> and rejects
    /// duplicates (an active contributor with the same IDNO already exists).
    /// </summary>
    /// <param name="input">The registration payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Sqid-encoded id of the new contributor, or a failure with one of:
    /// <see cref="ErrorCodes.InvalidIdno"/>, <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result<string>> RegisterAsync(ContributorRegistrationInput input, CancellationToken ct = default);

    /// <summary>
    /// Loads a single contributor by its Sqid id.
    /// </summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The contributor projection, or a failure with
    /// <see cref="ErrorCodes.InvalidSqid"/> / <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<ContributorOutput>> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Loads a single contributor by its IDNO. Validates the IDNO before querying.
    /// </summary>
    /// <param name="idno">Candidate 13-digit IDNO.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The contributor projection, or a failure with
    /// <see cref="ErrorCodes.InvalidIdno"/> / <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<ContributorOutput>> GetByIdnoAsync(string idno, CancellationToken ct = default);

    /// <summary>
    /// Paged registry search by partial denumire OR IDNO substring (case-insensitive).
    /// </summary>
    /// <param name="denumireOrIdno">
    /// Optional substring filter. When null/whitespace, returns all active contributors.
    /// </param>
    /// <param name="page">Pagination request (clamped to [1, 200] page size).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Page of <see cref="ContributorListItem"/> rows ordered by Denumire.</returns>
    Task<Result<PagedResult<ContributorListItem>>> SearchAsync(
        string? denumireOrIdno,
        PageRequest page,
        CancellationToken ct = default);

    /// <summary>Flips <c>IsInsolvent=true</c> on the contributor and audits the change.</summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or <see cref="ErrorCodes.InvalidSqid"/> / <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result> MarkInsolventAsync(string id, CancellationToken ct = default);

    /// <summary>Flips <c>IsInsolvent=false</c> on the contributor and audits the restoration.</summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or <see cref="ErrorCodes.InvalidSqid"/> / <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result> MarkSolventAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the contributor (<c>IsActive=false</c>, sets <c>DeregisteredAtUtc=now</c>).
    /// Never performs a hard delete — see CLAUDE.md cross-cutting "Soft Deletes".
    /// </summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or <see cref="ErrorCodes.InvalidSqid"/> / <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result> DeactivateAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// R0305 / BP 1.2 — updates the mutable primary attributes (Denumire + classifier
    /// codes) of an existing contributor. The IDNO is immutable post-registration.
    /// Rejects when the contributor is administratively deactivated (BP 1.3) or merged.
    /// Emits a Notice audit event <c>CONTRIBUTOR.UPDATED</c>.
    /// </summary>
    /// <param name="contributorId">Internal contributor primary key.</param>
    /// <param name="input">New attribute values.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Updated <see cref="ContributorOutput"/>, or a failure with one of
    /// <see cref="ErrorCodes.NotFound"/> / <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result<ContributorOutput>> UpdateAttributesAsync(
        long contributorId,
        ContributorAttributesUpdateDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0305 / BP 1.3 — administratively deactivates the contributor (does NOT
    /// soft-delete — see <see cref="DeactivateAsync(string, CancellationToken)"/>
    /// for that path). Sets <c>IsDeactivated=true</c>, stamps <c>DeactivatedAtUtc</c>,
    /// persists the reason, and emits Critical audit <c>CONTRIBUTOR.DEACTIVATED_BP</c>.
    /// Rejects when already deactivated.
    /// </summary>
    /// <param name="contributorId">Internal contributor primary key.</param>
    /// <param name="reason">Operator-supplied reason; 3..500 chars.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or <see cref="ErrorCodes.NotFound"/> / <see cref="ErrorCodes.Conflict"/>.</returns>
    Task<Result> DeactivateAsync(long contributorId, string reason, CancellationToken ct = default);

    /// <summary>
    /// R0305 / BP 1.4 — reactivates a previously-deactivated contributor.
    /// Rejects when the contributor is in a terminal state (deceased/dissolved/merged).
    /// Emits Critical audit <c>CONTRIBUTOR.REACTIVATED</c>.
    /// </summary>
    /// <param name="contributorId">Internal contributor primary key.</param>
    /// <param name="reason">Operator-supplied reason; 3..500 chars.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or <see cref="ErrorCodes.NotFound"/> / <see cref="ErrorCodes.Conflict"/>.</returns>
    Task<Result> ReactivateAsync(long contributorId, string reason, CancellationToken ct = default);

    /// <summary>
    /// R0305 / BP 1.5 — merges <paramref name="duplicateContributorId"/> INTO
    /// <paramref name="survivorContributorId"/>. The duplicate row receives
    /// <c>MergedIntoContributorId = survivor.Id</c>, is flipped to
    /// <c>IsDeactivated = true</c>, and gets a synthetic reason
    /// <c>"merged into {survivorSqid}"</c>. The survivor is untouched.
    /// Emits Critical audit <c>CONTRIBUTOR.MERGED</c>. Refuses when either side
    /// is already merged.
    /// </summary>
    /// <param name="duplicateContributorId">Internal id of the duplicate row.</param>
    /// <param name="survivorContributorId">Internal id of the surviving row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or <see cref="ErrorCodes.NotFound"/> / <see cref="ErrorCodes.Forbidden"/>.</returns>
    Task<Result> MergeDuplicatesAsync(
        long duplicateContributorId,
        long survivorContributorId,
        CancellationToken ct = default);

    /// <summary>
    /// R0305 / BP 1.6 — placeholder for the rare "split a contributor row" operation.
    /// Returns <see cref="ErrorCodes.NotImplemented"/> in this build; documented as
    /// deferred-by-design because the criteria are tied to specialist tooling not
    /// yet available.
    /// </summary>
    /// <param name="sourceContributorId">Internal id of the source row that would be split.</param>
    /// <param name="input">Operator-supplied rationale (unused — preserved for the future implementation).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="ErrorCodes.NotImplemented"/>.</returns>
    Task<Result> SplitAsync(
        long sourceContributorId,
        ContributorSplitInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0305 / BP 1.7 — records an administrative field-level correction WITHOUT
    /// performing the field write (which goes through BP 1.2 / R0301 child services).
    /// The audit row carries the field name + hashed before/after values so the
    /// journal preserves the change without exposing PII. Requires the
    /// <c>Contributor.AdminCorrect</c> permission on the caller.
    /// </summary>
    /// <param name="contributorId">Internal contributor primary key.</param>
    /// <param name="fieldName">Logical name of the corrected field (e.g. <c>"Denumire"</c>).</param>
    /// <param name="oldValueHash">SHA-256 / HMAC hash of the prior value.</param>
    /// <param name="newValueHash">SHA-256 / HMAC hash of the new value.</param>
    /// <param name="reason">Free-form justification; 3..500 chars.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or <see cref="ErrorCodes.Forbidden"/> / <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result> AdminCorrectAsync(
        long contributorId,
        string fieldName,
        string oldValueHash,
        string newValueHash,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// R0305 / BP 1.9 — terminal state. Sets <see cref="Cnas.Ps.Core.Domain.Contributor.IsDeceased"/>
    /// for natural persons or <see cref="Cnas.Ps.Core.Domain.Contributor.IsDissolved"/>
    /// for legal persons (decided by IDNO leading digit), stamps the effective date,
    /// flips <c>IsDeactivated = true</c>, and emits Critical audit
    /// <c>CONTRIBUTOR.DECEASED_OR_DISSOLVED</c>. No reactivation is possible afterward.
    /// </summary>
    /// <param name="contributorId">Internal contributor primary key.</param>
    /// <param name="effectiveDate">Local date on which the contributor died / was dissolved.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or <see cref="ErrorCodes.NotFound"/> / <see cref="ErrorCodes.Conflict"/>.</returns>
    Task<Result> MarkDeceasedOrDissolvedAsync(
        long contributorId,
        DateOnly effectiveDate,
        CancellationToken ct = default);

    /// <summary>
    /// High-frequency internal query: was the contributor with this IDNO insured (active and
    /// not de-registered before <paramref name="atUtc"/>)? Used by
    /// <c>IApplicationProcessingService</c> when evaluating eligibility rules. No audit log
    /// is emitted because the call rate would overwhelm the journal.
    /// </summary>
    /// <param name="idno">Candidate 13-digit IDNO.</param>
    /// <param name="atUtc">UTC instant the question applies to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="IsInsuredResult"/> with the answer, or <see cref="ErrorCodes.InvalidIdno"/>
    /// when the IDNO fails value-object validation.
    /// </returns>
    Task<Result<IsInsuredResult>> IsInsuredAsync(string idno, DateTime atUtc, CancellationToken ct = default);
}
