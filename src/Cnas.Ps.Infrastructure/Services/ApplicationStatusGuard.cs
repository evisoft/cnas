using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0939 / iter 136 — concrete <see cref="IApplicationStatusGuard"/> implementation.
/// Reads the current <see cref="ServiceApplication.Status"/> from the read-replica
/// context (per CLAUDE.md guidance "<c>IReadOnlyCnasDbContext</c> for reads") and
/// delegates the legality verdict to the pinned
/// <see cref="ApplicationStatusTransitions.Table"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-replica safety.</b> Replica lag can in theory return a stale status, but
/// the guard is consulted INSIDE the same controller / service call that ALSO writes
/// through the primary, so the surrounding <c>SaveChangesAsync</c> guarantees a final
/// last-write-wins semantics: even if the guard pre-checks against a stale snapshot, a
/// concurrent transition wins or loses on the primary's optimistic concurrency check
/// (R0026 / TOR PSR 006). The guard exists to give callers a uniform pre-flight
/// failure shape, not to be the sole concurrency control.
/// </para>
/// <para>
/// <b>Allocation.</b> The hot path is a single <c>SELECT Status FROM Applications WHERE
/// Id = @id AND IsActive</c> projection (no Include, no tracking, no Cerere row
/// materialisation) so the call costs one round-trip + one boxed enum lookup against
/// the pinned matrix.
/// </para>
/// </remarks>
/// <param name="db">
/// Read-only replica context. Reads the cerere status without engaging the change
/// tracker.
/// </param>
public sealed class ApplicationStatusGuard(IReadOnlyCnasDbContext db) : IApplicationStatusGuard
{
    private readonly IReadOnlyCnasDbContext _db = db;

    /// <inheritdoc />
    public async Task<Result> ValidateTransitionAsync(
        long applicationId,
        ApplicationStatus to,
        CancellationToken cancellationToken = default)
    {
        // Project the status column only — we do not need to materialise the entire
        // Cerere row to make a legality verdict. The IsActive filter mirrors the
        // soft-delete contract enforced everywhere else (CLAUDE.md "Soft Deletes").
        var currentStatus = await _db.Applications
            .Where(a => a.Id == applicationId && a.IsActive)
            .Select(a => (ApplicationStatus?)a.Status)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (currentStatus is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Application not found.");
        }

        // Delegate the actual verdict to the pinned matrix. The underlying
        // StatusTransitionTable returns STATUS.ILLEGAL_TRANSITION; we surface
        // APPLICATION.ILLEGAL_TRANSITION so call sites can dispatch on the
        // domain-scoped code documented on IApplicationStatusGuard.
        var verdict = ApplicationStatusTransitions.Table.Validate(currentStatus.Value, to);
        if (verdict.IsSuccess)
        {
            return Result.Success();
        }

        return Result.Failure(
            IApplicationStatusGuard.IllegalTransitionCode,
            verdict.ErrorMessage ?? "Illegal application status transition.");
    }
}
