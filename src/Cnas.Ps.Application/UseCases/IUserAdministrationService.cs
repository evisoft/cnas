using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC18 — Administer users + access control. RBAC per SEC 021-026.</summary>
public interface IUserAdministrationService
{
    /// <summary>Lists user profiles with paging.</summary>
    Task<Result<PagedResult<UserListItem>>> ListAsync(PageRequest page, CancellationToken cancellationToken = default);

    /// <summary>Grants a role to a user.</summary>
    Task<Result> GrantRoleAsync(string userId, string role, CancellationToken cancellationToken = default);

    /// <summary>Revokes a role from a user.</summary>
    Task<Result> RevokeRoleAsync(string userId, string role, CancellationToken cancellationToken = default);

    /// <summary>Locks a user account (manual override).</summary>
    Task<Result> LockAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Unlocks a previously-locked account.</summary>
    Task<Result> UnlockAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0672 / TOR CF 18.08 — soft-deletes the supplied user
    /// (<c>UserProfile.IsActive=false</c>). The flip is gated by
    /// <c>Cnas.Ps.Application.Users.IUserDeactivationGuard.EnsureCanDeactivateAsync</c>:
    /// the user MUST already have at least one audit-history row
    /// (<see cref="Cnas.Ps.Core.Domain.EntityHistoryRow"/> or
    /// <see cref="Cnas.Ps.Core.Domain.AuditLog"/>) keyed to them. Brand-new
    /// accounts that have done nothing auditable yet cannot be deactivated —
    /// the policy guarantees every soft-delete leaves a trail behind.
    /// </summary>
    /// <param name="userId">Sqid-encoded user-profile id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the row was flipped (or was
    /// already inactive — idempotent);
    /// <see cref="ErrorCodes.Forbidden"/> when the caller lacks the
    /// <c>cnas-admin</c> role;
    /// <see cref="ErrorCodes.InvalidSqid"/> on a malformed Sqid;
    /// <see cref="ErrorCodes.NotFound"/> when no matching user exists;
    /// <see cref="ErrorCodes.UserProfileNoAuditHistory"/> when the guard
    /// refuses because no trail row was found.
    /// </returns>
    Task<Result> DeactivateAsync(string userId, CancellationToken cancellationToken = default);
}
