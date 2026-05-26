using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.AccessScope;

/// <summary>
/// R0671 / TOR CF 18.06 — reference implementation of <see cref="IAccessScopeService"/>.
/// Materialises the caller's effective scope envelope into an
/// <see cref="AccessScopeDescriptorDto"/> by copying the four allow-lists out of the
/// request-scoped <see cref="ICallerContext.AccessScope"/>.
/// </summary>
/// <remarks>
/// Scoped lifetime because it depends on the per-request <see cref="ICallerContext"/>;
/// the service is otherwise stateless.
/// </remarks>
/// <param name="caller">Request-scoped caller context that supplies the envelope.</param>
public sealed class AccessScopeService(ICallerContext caller) : IAccessScopeService
{
    private readonly ICallerContext _caller = caller;

    /// <inheritdoc />
    public Task<Result<AccessScopeDescriptorDto>> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var scope = _caller.AccessScope;
        // Copy each allow-list into a fresh ToArray so the DTO does not leak a live
        // reference into the request-scoped envelope — guards against a downstream
        // serializer mutating the underlying collection.
        var descriptor = new AccessScopeDescriptorDto(
            AllowedRegions: scope.AllowedRegions.ToArray(),
            AllowedSubdivisionCodes: scope.AllowedSubdivisionCodes.ToArray(),
            AllowedDocumentCategories: scope.AllowedDocumentCategories.ToArray(),
            AllowedWorkflowCategories: scope.AllowedWorkflowCategories.ToArray(),
            IsUnscoped: scope.IsUnscoped);
        return Task.FromResult(Result<AccessScopeDescriptorDto>.Success(descriptor));
    }
}
