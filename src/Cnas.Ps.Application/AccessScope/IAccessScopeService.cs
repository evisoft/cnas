using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.AccessScope;

/// <summary>
/// R0671 / TOR CF 18.06 — read-only façade over the caller's current
/// <see cref="Cnas.Ps.Application.Abstractions.IAccessScope"/>. Backs the
/// <c>GET /api/profile/access-scope</c> endpoint so the UI can introspect the
/// dimensions narrowed for the current operator without leaking the producing
/// infrastructure type into the API project.
/// </summary>
/// <remarks>
/// <para>
/// The service is intentionally narrow — a single <see cref="GetMineAsync"/>
/// method returning the descriptor. Future scope-mutation endpoints (e.g.
/// "as admin, grant geo:CHIS to user X") would land on a separate admin
/// service so the self-service surface stays read-only.
/// </para>
/// <para>
/// Returns <see cref="Result{T}.Success(T)"/> with an unscoped descriptor for
/// anonymous callers (defense in depth — the controller carries
/// <c>[Authorize]</c> so this branch is normally unreachable).
/// </para>
/// </remarks>
public interface IAccessScopeService
{
    /// <summary>
    /// Materialises the caller's effective <see cref="AccessScopeDescriptorDto"/>
    /// from the request-scoped <see cref="Cnas.Ps.Application.Abstractions.IAccessScope"/>.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> carrying the descriptor. Never returns
    /// a failure today; the <see cref="Result{T}"/> envelope is preserved so the
    /// service can grow into permission gates without a breaking-contract change.
    /// </returns>
    Task<Result<AccessScopeDescriptorDto>> GetMineAsync(CancellationToken cancellationToken = default);
}
