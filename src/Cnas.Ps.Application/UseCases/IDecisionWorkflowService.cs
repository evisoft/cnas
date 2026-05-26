using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// UC10 — Approve / reject / list decisions (Decizii) within the workflow. The list
/// surface projects from the Dossier + parent ServiceApplication aggregate; the
/// approve / reject paths mutate that aggregate in place.
/// </summary>
/// <remarks>
/// <para>
/// <b>List surface.</b> <see cref="ListAsync"/> is the decision-registry equivalent of
/// <see cref="ISolicitantService.SearchAsync"/>: canonical QBE + budget + access-scope
/// pipeline against the <c>Decision</c> registry. Sqid round-tripping happens at the
/// API boundary; the service body deals in raw long ids.
/// </para>
/// <para>
/// <b>Failure modes for <see cref="ListAsync"/>:</b>
/// <list type="bullet">
///   <item><see cref="ErrorCodes.ValidationFailed"/> — input validator rejected the envelope.</item>
///   <item><see cref="ErrorCodes.QueryTooBroad"/> — budget guard refused; verdict on <see cref="LastBudgetVerdict"/>.</item>
///   <item>Any of the <c>QBE_*</c> family — converter rejected the QBE envelope.</item>
/// </list>
/// </para>
/// </remarks>
public interface IDecisionWorkflowService
{
    /// <summary>Records the decider's approval.</summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="note">Optional free-text note attached to the audit row.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns><see cref="Result.Success"/> on a successful transition; failure with a stable error code otherwise.</returns>
    Task<Result> ApproveAsync(string dossierId, string? note, CancellationToken cancellationToken = default);

    /// <summary>Records the decider's rejection together with a mandatory reason.</summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="reason">Mandatory reason recorded on the audit row.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns><see cref="Result.Success"/> on a successful transition; failure with a stable error code otherwise.</returns>
    Task<Result> RejectAsync(string dossierId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0671 continuation — paged QBE-filterable list of decisions (projected from the
    /// Dossier + parent ServiceApplication aggregate). Wires the R0163 QBE converter, the
    /// R0167 query budget guard, and the R0671 access-scope filter (narrowing via the
    /// parent application's <c>SubdivisionCode</c>) against the <c>Decision</c> registry.
    /// </summary>
    /// <param name="input">Search envelope — optional QBE filter, optional UTC date range, paging.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// On success a <see cref="DecisionsListPageDto"/> carrying Sqid-encoded rows and the
    /// total matching count. On failure one of the codes listed in the interface remarks.
    /// </returns>
    Task<Result<DecisionsListPageDto>> ListAsync(
        DecisionsListInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Most-recent budget verdict captured during a <see cref="ListAsync"/> call. The
    /// controller reads this slot when surfacing a <see cref="ErrorCodes.QueryTooBroad"/>
    /// failure to populate the <c>extensions["budget"]</c> bag on the 422 ProblemDetails.
    /// </summary>
    QueryBudgetVerdict? LastBudgetVerdict { get; }

    /// <summary>
    /// R0574 / TOR CF 08.06 — explicit "forward to next level" branch of the
    /// 3-level CNAS approval chain. Reads the application's current approval level
    /// from its status, computes the next level via
    /// <c>WorkflowRouting.ComputeNextLevel</c>, flips the application status, and
    /// audits <c>WORKFLOW.FORWARDED_TO_{LEVEL}</c>. Scaffolded BPM-engine independent
    /// so the iter-137 enum + iter-119 status-transition table own the contract; an
    /// Operaton adapter MAY observe the resulting transitions later.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded application id.</param>
    /// <param name="reason">Mandatory free-text reason captured in the audit row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on a successful forward; failure with
    /// <see cref="ErrorCodes.WorkflowNotDecider"/> when the caller lacks the decider
    /// role, <see cref="ErrorCodes.InvalidSqid"/> when the sqid cannot be decoded,
    /// <see cref="ErrorCodes.NotFound"/> when the application is missing or inactive,
    /// or <see cref="ErrorCodes.WorkflowAlreadyAtTop"/> when forwarding from
    /// <c>ChiefCnas</c> is requested (no-op).
    /// </returns>
    Task<Result> ForwardToNextLevelAsync(
        string applicationSqid,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// R0592 / TOR CF 10.03 — explicit "return to previous step" branch of the
    /// 3-level CNAS approval chain. Reads the application's current approval level
    /// from its status, computes the previous level via
    /// <c>WorkflowRouting.ComputePreviousLevel</c>, flips the application status,
    /// and audits <c>WORKFLOW.RETURNED</c>. When invoked at the
    /// <c>UserCnas</c> floor (no previous level), flips the application to
    /// <c>Rejected</c> and audits <c>WORKFLOW.RETURNED_AT_FLOOR</c> instead.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded application id.</param>
    /// <param name="reason">Mandatory free-text reason captured in the audit row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on a successful return; failure with the same
    /// error-code envelope as <see cref="ForwardToNextLevelAsync"/>.
    /// </returns>
    Task<Result> ReturnToPreviousStepAsync(
        string applicationSqid,
        string reason,
        CancellationToken cancellationToken = default);
}
