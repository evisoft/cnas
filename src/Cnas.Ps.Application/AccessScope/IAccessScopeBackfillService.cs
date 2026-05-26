using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.AccessScope;

/// <summary>
/// R0671 continuation / TOR CF 18.06 — admin back-fill helper that retro-assigns
/// the access-scope columns introduced by R0671 (<c>Solicitant.RegionCode</c> and
/// <c>ServiceApplication.SubdivisionCode</c>). R0671 added those columns NOT
/// back-filled, so existing rows carry NULL and are treated as "national scope"
/// (visible to every scoped caller). This service lets ops bulk-assign the
/// columns after-the-fact via either a QBE filter (R0163) or an explicit Sqid
/// list — or the union of the two.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hard 5000-row cap.</b> Both back-fill operations refuse calls whose matched
/// row set would exceed 5000 (<see cref="ErrorCodes.BulkQuotaExceeded"/>). The cap
/// is per-call — operators that need to back-fill a larger universe issue several
/// narrower calls.
/// </para>
/// <para>
/// <b>Audit invariant.</b> Each successful call emits ONE
/// <see cref="Cnas.Ps.Core.Domain.AuditSeverity.Critical"/> summary audit row
/// (<c>ACCESS_SCOPE.BACKFILL.SOLICITANT</c> or
/// <c>ACCESS_SCOPE.BACKFILL.APPLICATION</c>) carrying the assigned code and the
/// resulting <c>rowsUpdated</c> count. Per-row audit is intentionally deferred:
/// the bulk shape of the operation is what auditors care about.
/// </para>
/// <para>
/// <b>Authorisation.</b> The service does NOT gate by role itself — the
/// controller carries <c>[Authorize(Roles="cnas-admin")]</c> so the gate is
/// declarative. Callers reaching this layer are assumed to be cnas-admin.
/// </para>
/// </remarks>
public interface IAccessScopeBackfillService
{
    /// <summary>
    /// Bulk-assigns <see cref="Cnas.Ps.Core.Domain.Solicitant.RegionCode"/> on the
    /// rows resolved by <paramref name="input"/>.
    /// </summary>
    /// <param name="input">Filter / explicit-Sqid selection + region code.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the row-count + per-row failures,
    /// or <see cref="Result{T}.Failure"/> with a stable error code on validation /
    /// quota / branch-not-found failure.
    /// </returns>
    Task<Result<AccessScopeBackfillResultDto>> AssignSolicitantRegionByPatternAsync(
        AccessScopeSolicitantBackfillInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-assigns <see cref="Cnas.Ps.Core.Domain.ServiceApplication.SubdivisionCode"/>
    /// on the rows resolved by <paramref name="input"/>. The
    /// <see cref="AccessScopeApplicationBackfillInputDto.SubdivisionCode"/> is
    /// cross-checked against the active <c>CnasBranch.Code</c> set (R0512); an
    /// unknown code returns <see cref="ErrorCodes.NotFound"/> with the
    /// <c>BRANCH_NOT_FOUND</c> human message.
    /// </summary>
    /// <param name="input">Filter / explicit-Sqid selection + subdivision code.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the row-count + per-row failures,
    /// or <see cref="Result{T}.Failure"/> with a stable error code.
    /// </returns>
    Task<Result<AccessScopeBackfillResultDto>> AssignServiceApplicationSubdivisionByPatternAsync(
        AccessScopeApplicationBackfillInputDto input,
        CancellationToken cancellationToken = default);
}
