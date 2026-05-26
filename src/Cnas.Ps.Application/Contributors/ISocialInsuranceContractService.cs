using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Contributors;

/// <summary>
/// R0912 / TOR BP 2.2-C — service façade for the social-insurance contract
/// lifecycle (issue, modify, terminate) attached to a <c>Contributor</c>
/// (InsuredPerson, "Persoană asigurată"). The underlying entity
/// <c>ContributorSocialInsuranceContract</c> was added in R0311 — R0912 ships
/// the explicit issue/modify/terminate operations on top.
/// </summary>
/// <remarks>
/// <para>
/// All identifiers crossing the boundary are Sqid-encoded per CLAUDE.md
/// RULE 3. Mutating operations emit Critical-severity audit events.
/// Supersession semantics:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Issue</b> inserts a new row with <c>ValidFromUtc = now</c>,
///     <c>ValidToUtc = null</c>; rejected if an overlapping active contract
///     already exists.
///   </description></item>
///   <item><description>
///     <b>Modify</b> closes the current row (<c>ValidToUtc = now</c>) and
///     inserts a fresh row at <c>ValidFromUtc = now</c> with the modified
///     fields (the R0311 supersession pattern).
///   </description></item>
///   <item><description>
///     <b>Terminate</b> sets <c>ContractEndDate</c> + <c>ValidToUtc</c> on the
///     existing current row — terminal state, no new row inserted.
///   </description></item>
/// </list>
/// </remarks>
public interface ISocialInsuranceContractService
{
    /// <summary>
    /// R0912 — issues a brand new social-insurance contract for the
    /// specified Contributor. Rejected when the Contributor is deactivated
    /// or when an overlapping active contract already exists.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="ContributorSocialInsuranceContractDto"/>;
    /// on missing Contributor <see cref="ErrorCodes.NotFound"/>;
    /// on overlapping active contract <see cref="ErrorCodes.Conflict"/>;
    /// on deactivated Contributor <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result<ContributorSocialInsuranceContractDto>> IssueAsync(
        SocialInsuranceContractIssueDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0912 — applies a supersession update to an existing current contract
    /// row. The current row is closed (<c>ValidToUtc = now</c>) and a fresh
    /// row is inserted at <c>ValidFromUtc = now</c> with the modified fields.
    /// </summary>
    /// <param name="contractId">Raw bigint id of the current contract row.</param>
    /// <param name="input">Validated modify-input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the new (post-supersession) contract row DTO;
    /// on missing contract <see cref="ErrorCodes.NotFound"/>;
    /// on already-terminated contract <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result<ContributorSocialInsuranceContractDto>> ModifyAsync(
        long contractId,
        SocialInsuranceContractModifyDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0912 — terminates an active contract by setting
    /// <c>ContractEndDate</c> + <c>ValidToUtc</c> on the existing row.
    /// Terminal state — no new supersession row is inserted.
    /// </summary>
    /// <param name="contractId">Raw bigint id of the current contract row.</param>
    /// <param name="effectiveDate">Date the contract terminates.</param>
    /// <param name="reason">Operator rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>;
    /// on missing contract <see cref="ErrorCodes.NotFound"/>;
    /// on already-terminated contract <see cref="ErrorCodes.Conflict"/>;
    /// on bad reason <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result> TerminateAsync(
        long contractId,
        DateOnly effectiveDate,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// R0912 — returns the current (open, non-superseded) social-insurance
    /// contract rows for the specified Contributor. At most one row should be
    /// returned per the supersession invariant.
    /// </summary>
    /// <param name="contributorId">Raw bigint id of the Contributor (InsuredPerson).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Bounded read-only list (empty when no current contract is on file).</returns>
    Task<IReadOnlyList<ContributorSocialInsuranceContractDto>> GetCurrentForContributorAsync(
        long contributorId,
        CancellationToken ct = default);
}
