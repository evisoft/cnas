using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Identity;

/// <summary>
/// R2270 / TOR SEC 023-024 — service façade for the <c>UserGroup</c> registry.
/// Owns the create / modify / disable / enable / delete lifecycle plus the
/// nested-group and direct-membership management endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b> Every successful mutation emits a stable audit
/// event at <see cref="AuditSeverity.Critical"/> severity (CLAUDE.md §5.6 —
/// role/permission change). The cycle-rejection path also fires the
/// <c>cnas.user_group.hierarchy_cycle_attempted</c> counter on the Meter so
/// operators can chart attempted-cycle volume.
/// </para>
/// <para>
/// <b>Sqid round-trip.</b> Every identifier crossing the boundary is
/// Sqid-encoded per CLAUDE.md RULE 3 — the service decodes them internally
/// before touching the DbContext. The group <see cref="UserGroup.Code"/> is
/// NOT a Sqid: it is the stable domain code surfaced verbatim.
/// </para>
/// </remarks>
public interface IUserGroupService
{
    /// <summary>Creates a new user-group with the supplied direct roles.</summary>
    /// <param name="input">Validated create input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The persisted DTO on success; <see cref="ErrorCodes.Conflict"/> on code collision.</returns>
    Task<Result<UserGroupDto>> CreateAsync(UserGroupCreateInputDto input, CancellationToken ct = default);

    /// <summary>Modifies an existing user-group (DisplayName / Description / Kind / Roles).</summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="input">Modify payload (one or more fields + ChangeReason).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated DTO on success; <see cref="ErrorCodes.NotFound"/> when the group is missing.</returns>
    Task<Result<UserGroupDto>> ModifyAsync(string sqid, UserGroupModifyInputDto input, CancellationToken ct = default);

    /// <summary>Flips Status from Active to Disabled.</summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="input">Reason payload (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated DTO on success.</returns>
    Task<Result<UserGroupDto>> DisableAsync(string sqid, UserGroupReasonInputDto input, CancellationToken ct = default);

    /// <summary>Flips Status from Disabled back to Active.</summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="input">Reason payload (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated DTO on success.</returns>
    Task<Result<UserGroupDto>> EnableAsync(string sqid, UserGroupReasonInputDto input, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the user-group (flips <see cref="AuditableEntity.IsActive"/>
    /// to <c>false</c>); the row remains in the DB for traceability.
    /// </summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="input">Reason payload (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns><see cref="Result.Success"/> on success.</returns>
    Task<Result> DeleteAsync(string sqid, UserGroupReasonInputDto input, CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="childSqid"/> as a child of <paramref name="parentSqid"/>.
    /// Rejects self-loops and cycles (where the proposed child already appears
    /// as an ancestor of the proposed parent).
    /// </summary>
    /// <param name="parentSqid">Sqid-encoded id of the parent group.</param>
    /// <param name="childSqid">Sqid-encoded id of the child group.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated parent DTO on success; <see cref="ErrorCodes.Conflict"/> on cycle / self-loop.</returns>
    Task<Result<UserGroupDto>> AddChildAsync(string parentSqid, string childSqid, CancellationToken ct = default);

    /// <summary>Removes the nesting relation between <paramref name="parentSqid"/> and <paramref name="childSqid"/>.</summary>
    /// <param name="parentSqid">Sqid-encoded id of the parent group.</param>
    /// <param name="childSqid">Sqid-encoded id of the child group.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated parent DTO on success; <see cref="ErrorCodes.NotFound"/> when no nesting row exists.</returns>
    Task<Result<UserGroupDto>> RemoveChildAsync(string parentSqid, string childSqid, CancellationToken ct = default);

    /// <summary>Adds <paramref name="userSqid"/> as a direct member of <paramref name="groupSqid"/>.</summary>
    /// <param name="groupSqid">Sqid-encoded id of the group.</param>
    /// <param name="userSqid">Sqid-encoded id of the user-profile.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated group DTO on success.</returns>
    Task<Result<UserGroupDto>> AddMemberAsync(string groupSqid, string userSqid, CancellationToken ct = default);

    /// <summary>Removes a user's direct membership from the supplied group.</summary>
    /// <param name="groupSqid">Sqid-encoded id of the group.</param>
    /// <param name="userSqid">Sqid-encoded id of the user-profile.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated group DTO on success.</returns>
    Task<Result<UserGroupDto>> RemoveMemberAsync(string groupSqid, string userSqid, CancellationToken ct = default);

    /// <summary>Fetches a single user-group by Sqid id.</summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO when found; <see cref="ErrorCodes.NotFound"/> otherwise.</returns>
    Task<Result<UserGroupDto>> GetByIdAsync(string sqid, CancellationToken ct = default);

    /// <summary>Fetches a single user-group by its stable domain code.</summary>
    /// <param name="code">Stable group code.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO when found; <see cref="ErrorCodes.NotFound"/> otherwise.</returns>
    Task<Result<UserGroupDto>> GetByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>Lists user-groups according to the supplied filter envelope.</summary>
    /// <param name="filter">Optional filter envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>A paged DTO; never null.</returns>
    Task<Result<UserGroupListPageDto>> ListAsync(UserGroupListFilterDto filter, CancellationToken ct = default);
}
