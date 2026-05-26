using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0570 / TOR CF 08.02 — distribution-of-incoming-cases service. Selects
/// the next examiner for a freshly-submitted application using a round-robin
/// over the eligible pool while EXCLUDING the registrar (the user who
/// recorded the submission) — CF 08.02 mandates that the same person who
/// registered a cerere cannot examine it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Eligible pool.</b> The implementation considers every
/// <c>UserProfile</c> row that (a) carries the <c>cnas-examiner</c> role
/// claim, (b) is in <c>UserAccountState.Active</c>, and (c) has
/// <c>IsActive == true</c>. The <see cref="AssignExaminerAsync"/> method
/// removes the supplied registrar from this pool before applying the
/// round-robin, so the registrar is never assigned even when they
/// themselves carry the examiner role.
/// </para>
/// <para>
/// <b>Uniform spread.</b> A persisted singleton-row cursor
/// (<c>ExaminerAssignmentCursor</c>) is incremented after every successful
/// assignment so consecutive submissions fan out across the pool in
/// canonical (Id-ascending) order. The cursor survives process restarts —
/// an in-memory counter would silently restart the rotation on every deploy
/// and concentrate the workload on the first examiner.
/// </para>
/// <para>
/// <b>Empty pool.</b> When the eligible pool is empty after the registrar
/// exclusion, the method returns a
/// <see cref="ErrorCodes.ApplicationNoAvailableExaminer"/> failure and does
/// NOT mutate the cursor. The caller MUST refuse the submission — leaving
/// an application without an examiner stalls the workflow.
/// </para>
/// <para>
/// <b>Result.</b> A successful call returns the <see cref="long"/> internal
/// user id of the chosen examiner. The caller is responsible for any
/// downstream side-effects (stamping the dossier, audit row, notification);
/// the assignment service only computes the selection.
/// </para>
/// </remarks>
public interface IExaminerAssignmentService
{
    /// <summary>
    /// Selects the next examiner for the supplied application using the
    /// round-robin policy described in the type remarks. The registrar
    /// is excluded from the candidate pool even when their account
    /// independently carries the examiner role.
    /// </summary>
    /// <param name="applicationId">
    /// Internal id of the application being submitted. Passed in so the
    /// audit/observability stream can correlate the assignment to the
    /// originating cerere even when downstream code rolls back.
    /// </param>
    /// <param name="registrarUserId">
    /// Internal id of the user who registered (submitted) the application.
    /// The selection algorithm excludes this id from the candidate pool
    /// per CF 08.02.
    /// </param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// On success a <see cref="Result{T}"/> carrying the chosen examiner's
    /// internal user id. <see cref="ErrorCodes.ApplicationNoAvailableExaminer"/>
    /// when no examiner remains after the registrar exclusion.
    /// </returns>
    Task<Result<long>> AssignExaminerAsync(
        long applicationId,
        long registrarUserId,
        CancellationToken cancellationToken = default);
}
