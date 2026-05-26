using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ContributorProfileUpdates;

/// <summary>
/// R0362 / TOR UC13 strategy 2 — workflow-driven contributor-profile updates. Implements
/// the three lifecycle entry points (submit / approve / reject) of a profile-update
/// request and ensures the corresponding contributor child-table mutation happens
/// atomically when the approver says yes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> A Solicitant (or admin acting on their behalf) submits a request
/// via <see cref="SubmitAsync"/>. The service creates the parent
/// <c>ServiceApplication</c> shell + the child <c>ProfileUpdateRequest</c> row, both
/// in <see cref="Cnas.Ps.Core.Domain.ProfileUpdateRequestStatus.Pending"/>. An
/// administrator with the <c>Profile.Approve</c> permission then calls
/// <see cref="ApproveAsync"/>, which deserialises
/// <see cref="Cnas.Ps.Core.Domain.ProfileUpdateRequest.RequestedChangesJson"/> and
/// applies it via <c>IContributorLinkedEntitiesService</c>. The row's status flips to
/// <c>Applied</c> on success or <c>Failed</c> on apply-side failure; the row is
/// persisted either way for audit traceability.
/// </para>
/// <para>
/// <b>Audit.</b> <see cref="ApproveAsync"/> emits a Critical
/// <c>PROFILE.UPDATE.APPLIED</c> row (carrying <c>{ requestSqid, type,
/// contributorSqid, success }</c>); <see cref="RejectAsync"/> emits a Notice
/// <c>PROFILE.UPDATE.REJECTED</c> row.
/// </para>
/// </remarks>
public interface IProfileUpdateService
{
    /// <summary>
    /// Submits a new profile-update request. Validates the Type (must parse to
    /// <c>ProfileUpdateRequestType</c>), the JSON shape (must be syntactically valid),
    /// and the target contributor (must exist). On success creates the parent
    /// <c>ServiceApplication</c> + child row and returns the resulting DTO.
    /// </summary>
    /// <param name="input">Submission payload.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<ProfileUpdateRequestDto>> SubmitAsync(ProfileUpdateRequestSubmitDto input, CancellationToken ct = default);

    /// <summary>
    /// Approves the request, applies the change to the matching contributor child table,
    /// and writes the audit row. Returns a failure when the caller lacks the
    /// <c>Profile.Approve</c> permission, when the request is missing or already
    /// decided, or when the apply step itself fails (in which case the row is still
    /// persisted with <c>Status=Failed</c> + <c>ApplicationErrorJson</c>).
    /// </summary>
    /// <param name="requestId">Internal id of the request row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<ProfileUpdateRequestDto>> ApproveAsync(long requestId, CancellationToken ct = default);

    /// <summary>
    /// Rejects the request, stamps the rejection reason, and writes the audit row. The
    /// row is never re-opened — callers that want to retry must submit a fresh request.
    /// </summary>
    /// <param name="requestId">Internal id of the request row.</param>
    /// <param name="reason">Free-text rationale (1..1024 chars).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> RejectAsync(long requestId, string reason, CancellationToken ct = default);

    /// <summary>Loads one request by id (Sqid-encoded on the wire).</summary>
    /// <param name="requestId">Internal id of the request row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<ProfileUpdateRequestDto>> GetAsync(long requestId, CancellationToken ct = default);
}
