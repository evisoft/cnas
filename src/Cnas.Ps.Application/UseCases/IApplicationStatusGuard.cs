using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0939 / iter 136 — Application-layer guard that validates a proposed
/// <see cref="ApplicationStatus"/> transition against the canonical 8-state
/// matrix pinned in <c>Cnas.Ps.Core.ValueObjects.ApplicationStatusTransitions.Table</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a guard.</b> Pre-iter-136 every status-mutating site hand-rolled its own
/// <c>if</c>-ladder to police illegal transitions (e.g.
/// <c>if (Status is Closed or Approved or Rejected) return Locked</c>). The legacy
/// ladders are scattered across <c>ApplicationServiceImpl</c>, <c>AdminServices</c>,
/// <c>ApplicationProcessingService</c>, the bulk-action runner, and the missing-docs
/// SLA job. Centralising the matrix in one guard makes a future rule change a one-line
/// table edit instead of an N-file sweep.
/// </para>
/// <para>
/// <b>Return contract.</b> Success means "the (from, to) edge exists in the matrix";
/// failure carries the stable code <c>APPLICATION.ILLEGAL_TRANSITION</c> (mirroring the
/// generic <c>STATUS.ILLEGAL_TRANSITION</c> but specialised for the application
/// aggregate so consumers can dispatch on it cleanly). A missing application surfaces
/// as <see cref="ErrorCodes.NotFound"/> so the controller can return 404 without an
/// explicit pre-flight read.
/// </para>
/// <para>
/// <b>Why Application-layer.</b> The guard reads
/// <see cref="ServiceApplication.Status"/> from the database before deciding, so it is
/// NOT pure Core logic. The Core layer hosts the static
/// <see cref="Cnas.Ps.Core.ValueObjects.ApplicationStatusTransitions.Table"/>; this
/// abstraction is the Application-layer port that couples it to a DB read. The
/// implementation lives in Infrastructure.
/// </para>
/// </remarks>
public interface IApplicationStatusGuard
{
    /// <summary>
    /// Stable error code surfaced when the guard rejects a transition. Matches
    /// <c>STATUS.ILLEGAL_TRANSITION</c> from <c>StatusTransitionTable&lt;T&gt;</c>
    /// but is namespaced to the application aggregate for callers that want to
    /// dispatch precisely.
    /// </summary>
    public const string IllegalTransitionCode = "APPLICATION.ILLEGAL_TRANSITION";

    /// <summary>
    /// Validates a proposed transition for the supplied application id. Reads the
    /// current <see cref="ServiceApplication.Status"/> from the database and
    /// consults
    /// <see cref="Cnas.Ps.Core.ValueObjects.ApplicationStatusTransitions.Table"/>.
    /// </summary>
    /// <param name="applicationId">Internal primary key of the application.</param>
    /// <param name="to">Proposed next status.</param>
    /// <param name="cancellationToken">Cancellation propagation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> when the edge is allowed; failure with
    /// <see cref="ErrorCodes.NotFound"/> when no active application matches
    /// <paramref name="applicationId"/>; otherwise failure with
    /// <see cref="IllegalTransitionCode"/> describing the rejected edge.
    /// </returns>
    Task<Result> ValidateTransitionAsync(
        long applicationId,
        ApplicationStatus to,
        CancellationToken cancellationToken = default);
}
