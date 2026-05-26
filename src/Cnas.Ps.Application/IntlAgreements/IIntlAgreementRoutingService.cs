using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.IntlAgreements;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — service façade over the
/// international-agreements 3-level routing chain. The same aggregate
/// backs every benefit kind under bilateral social-security agreements;
/// the per-benefit-kind <see cref="IIntlAgreementRoutingPolicy"/> selects
/// the reviewer role codes + evidence schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>State machine.</b>
/// <c>Draft → AtLocalReview</c> (via <see cref="SubmitAsync"/> — Submitted
/// is transitional and never visible to callers);
/// <c>AtLocalReview → AtRegionalReview</c> (level-1 Approved);
/// <c>AtRegionalReview → AtNationalReview</c> (level-2 Approved);
/// <c>AtNationalReview → Approved</c> (level-3 Approved);
/// any level may go to <c>Rejected</c> (terminal) or
/// <c>RevisionRequested</c> (resubmit re-enters from level 1);
/// non-terminal states may go to <c>Cancelled</c>.
/// </para>
/// <para>
/// <b>Authorisation.</b> Every <see cref="RecordReviewAsync"/> call asserts
/// the calling user holds the reviewer role for the case's current level
/// (resolved through the policy). Callers that lack the role receive
/// <see cref="ErrorCodes.Forbidden"/> with the stable
/// <c>INTL_AGREEMENT.WRONG_REVIEWER_ROLE</c> message.
/// </para>
/// </remarks>
public interface IIntlAgreementRoutingService
{
    /// <summary>
    /// Creates a new international-agreements review case in
    /// <see cref="Cnas.Ps.Core.Domain.IntlAgreementReviewCaseStatus.Draft"/>.
    /// </summary>
    /// <param name="input">Validated create envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted DTO; validation / conflict failures on error.</returns>
    Task<Result<IntlAgreementReviewCaseDto>> CreateAsync(
        IntlAgreementReviewCaseCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes a Draft case to <c>AtLocalReview</c>. Returns Conflict for
    /// any non-Draft starting status.
    /// </summary>
    /// <param name="sqid">Sqid-encoded case id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed DTO; conflict / not-found on error.</returns>
    Task<Result<IntlAgreementReviewCaseDto>> SubmitAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a reviewer decision at the case's current routing level.
    /// Asserts the caller holds the matching reviewer role; persists a
    /// <see cref="Cnas.Ps.Core.Domain.IntlAgreementReviewStep"/> row;
    /// transitions the case based on the outcome.
    /// </summary>
    /// <param name="sqid">Sqid-encoded case id.</param>
    /// <param name="input">Reviewer decision envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed DTO; conflict / forbidden / not-found on error.</returns>
    Task<Result<IntlAgreementReviewCaseDto>> RecordReviewAsync(
        string sqid,
        IntlAgreementReviewInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-enters a <c>RevisionRequested</c> case into the chain at
    /// level 1 (<c>AtLocalReview</c>). Optionally accepts updated evidence
    /// JSON.
    /// </summary>
    /// <param name="sqid">Sqid-encoded case id.</param>
    /// <param name="input">Re-submit envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed DTO; conflict / not-found on error.</returns>
    Task<Result<IntlAgreementReviewCaseDto>> ResubmitAsync(
        string sqid,
        IntlAgreementReviewCaseResubmitInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a non-terminal case with an operator-supplied reason.
    /// Returns Conflict if the case is already in a terminal state.
    /// </summary>
    /// <param name="sqid">Sqid-encoded case id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed DTO; conflict / not-found on error.</returns>
    Task<Result<IntlAgreementReviewCaseDto>> CancelAsync(
        string sqid,
        IntlAgreementReviewCaseReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single case (including its review-step history) by Sqid.
    /// </summary>
    /// <param name="sqid">Sqid-encoded case id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success; not-found on missing.</returns>
    Task<Result<IntlAgreementReviewCaseDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Filtered list of cases ordered by registration date desc, capped at
    /// 100 rows per page.
    /// </summary>
    /// <param name="filter">Validated filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page envelope on success; validation failure otherwise.</returns>
    Task<Result<IntlAgreementReviewCasePageDto>> ListAsync(
        IntlAgreementReviewCaseFilterDto filter,
        CancellationToken cancellationToken = default);
}
