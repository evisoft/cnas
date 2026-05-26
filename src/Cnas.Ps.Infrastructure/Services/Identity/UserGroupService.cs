using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Identity;

/// <summary>
/// R2270 / TOR SEC 023-024 — concrete implementation of
/// <see cref="IUserGroupService"/>. Owns the create / modify / disable /
/// enable / delete lifecycle, the nested-group join management, and the
/// direct-membership join management.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cycle prevention.</b> Before persisting a new
/// <see cref="UserGroupParent"/> the service walks the
/// proposed parent's full ancestor set via BFS through
/// <c>UserGroupParents</c>; if the proposed child appears anywhere in that
/// set the request is refused as <see cref="ErrorCodes.Conflict"/> and the
/// <see cref="CnasMeter.UserGroupHierarchyCycleAttempted"/> counter is
/// incremented. Self-loops are rejected up-front before the BFS runs.
/// </para>
/// <para>
/// <b>Audit + metric.</b> Every successful lifecycle transition emits the
/// canonical Critical audit event per CLAUDE.md §5.6 (role/permission
/// change). The OTel meter records creation and cycle-rejection counts.
/// </para>
/// </remarks>
public sealed class UserGroupService : IUserGroupService
{
    /// <summary>Stable audit event code emitted when a group is created.</summary>
    public const string AuditCreated = "USER_GROUP.CREATED";

    /// <summary>Stable audit event code emitted when a group is modified.</summary>
    public const string AuditModified = "USER_GROUP.MODIFIED";

    /// <summary>Stable audit event code emitted when a group is disabled.</summary>
    public const string AuditDisabled = "USER_GROUP.DISABLED";

    /// <summary>Stable audit event code emitted when a group is re-enabled.</summary>
    public const string AuditEnabled = "USER_GROUP.ENABLED";

    /// <summary>Stable audit event code emitted when a group is soft-deleted.</summary>
    public const string AuditDeleted = "USER_GROUP.DELETED";

    /// <summary>Stable audit event code emitted when a child group is linked.</summary>
    public const string AuditChildAdded = "USER_GROUP.CHILD_ADDED";

    /// <summary>Stable audit event code emitted when a child group is unlinked.</summary>
    public const string AuditChildRemoved = "USER_GROUP.CHILD_REMOVED";

    /// <summary>Stable audit event code emitted when a user is added to a group.</summary>
    public const string AuditMemberAdded = "USER_GROUP.MEMBER_ADDED";

    /// <summary>Stable audit event code emitted when a user is removed from a group.</summary>
    public const string AuditMemberRemoved = "USER_GROUP.MEMBER_REMOVED";

    /// <summary>Stable failure message for cycle rejections.</summary>
    public const string CycleRejectedMessage = "USER_GROUP.CYCLE";

    /// <summary>Stable failure message for self-loop rejections.</summary>
    public const string SelfLoopRejectedMessage = "USER_GROUP.SELF_LOOP";

    /// <summary>Stable failure message when the code already exists.</summary>
    public const string CodeDuplicateMessage = "USER_GROUP.CODE_DUPLICATE";

    /// <summary>Cached JSON serializer options shared across audit-payload builders.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IValidator<UserGroupCreateInputDto> _createValidator;
    private readonly IValidator<UserGroupModifyInputDto> _modifyValidator;
    private readonly IValidator<UserGroupReasonInputDto> _reasonValidator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (write surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly from service code.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated-caller information for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="createValidator">Validator for create input.</param>
    /// <param name="modifyValidator">Validator for modify input.</param>
    /// <param name="reasonValidator">Validator for reason input (disable / enable / delete).</param>
    public UserGroupService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<UserGroupCreateInputDto> createValidator,
        IValidator<UserGroupModifyInputDto> modifyValidator,
        IValidator<UserGroupReasonInputDto> reasonValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _createValidator = createValidator;
        _modifyValidator = modifyValidator;
        _reasonValidator = reasonValidator;
    }

    /// <inheritdoc />
    public async Task<Result<UserGroupDto>> CreateAsync(UserGroupCreateInputDto input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _createValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<UserGroupDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        if (!Enum.TryParse<UserGroupKind>(input.Kind, ignoreCase: false, out var kind))
        {
            return Result<UserGroupDto>.Failure(
                ErrorCodes.ValidationFailed, "Kind must be a known UserGroupKind enum name.");
        }

        var duplicate = await _db.UserGroups
            .AnyAsync(g => g.Code == input.Code, ct)
            .ConfigureAwait(false);
        if (duplicate)
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.Conflict, CodeDuplicateMessage);
        }

        var now = _clock.UtcNow;
        var entity = new UserGroup
        {
            Code = input.Code,
            DisplayName = input.DisplayName,
            Description = input.Description,
            Kind = kind,
            Status = UserGroupStatus.Active,
            Roles = input.Roles?.ToList() ?? [],
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.UserGroups.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitCriticalAuditAsync(AuditCreated, entity.Id, new
        {
            groupSqid = _sqids.Encode(entity.Id),
            code = entity.Code,
            kind = entity.Kind.ToString(),
            roleCount = entity.Roles.Count,
        }, ct).ConfigureAwait(false);

        CnasMeter.UserGroupCreated.Add(1);

        return Result<UserGroupDto>.Success(await ToDtoAsync(entity, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<UserGroupDto>> ModifyAsync(string sqid, UserGroupModifyInputDto input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _modifyValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<UserGroupDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<UserGroupDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var group = await _db.UserGroups
            .SingleOrDefaultAsync(g => g.Id == decoded.Value && g.IsActive, ct)
            .ConfigureAwait(false);
        if (group is null)
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "User-group not found.");
        }

        if (input.DisplayName is not null)
        {
            group.DisplayName = input.DisplayName;
        }
        if (input.Description is not null)
        {
            group.Description = input.Description;
        }
        if (input.Kind is not null)
        {
            if (!Enum.TryParse<UserGroupKind>(input.Kind, ignoreCase: false, out var newKind))
            {
                return Result<UserGroupDto>.Failure(
                    ErrorCodes.ValidationFailed, "Kind must be a known UserGroupKind enum name.");
            }
            group.Kind = newKind;
        }
        if (input.Roles is not null)
        {
            group.Roles = input.Roles.ToList();
        }

        var now = _clock.UtcNow;
        group.UpdatedAtUtc = now;
        group.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitCriticalAuditAsync(AuditModified, group.Id, new
        {
            groupSqid = _sqids.Encode(group.Id),
            code = group.Code,
            changeReason = input.ChangeReason,
        }, ct).ConfigureAwait(false);

        return Result<UserGroupDto>.Success(await ToDtoAsync(group, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task<Result<UserGroupDto>> DisableAsync(string sqid, UserGroupReasonInputDto input, CancellationToken ct = default) =>
        TransitionStatusAsync(sqid, input, UserGroupStatus.Active, UserGroupStatus.Disabled, AuditDisabled, ct);

    /// <inheritdoc />
    public Task<Result<UserGroupDto>> EnableAsync(string sqid, UserGroupReasonInputDto input, CancellationToken ct = default) =>
        TransitionStatusAsync(sqid, input, UserGroupStatus.Disabled, UserGroupStatus.Active, AuditEnabled, ct);

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(string sqid, UserGroupReasonInputDto input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var group = await _db.UserGroups
            .SingleOrDefaultAsync(g => g.Id == decoded.Value && g.IsActive, ct)
            .ConfigureAwait(false);
        if (group is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "User-group not found.");
        }

        var now = _clock.UtcNow;
        group.IsActive = false;
        group.UpdatedAtUtc = now;
        group.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitCriticalAuditAsync(AuditDeleted, group.Id, new
        {
            groupSqid = _sqids.Encode(group.Id),
            code = group.Code,
            reason = input.Reason,
        }, ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<UserGroupDto>> AddChildAsync(string parentSqid, string childSqid, CancellationToken ct = default)
    {
        var parentResult = _sqids.TryDecode(parentSqid);
        if (parentResult.IsFailure)
        {
            return Result<UserGroupDto>.Failure(parentResult.ErrorCode!, parentResult.ErrorMessage!);
        }
        var childResult = _sqids.TryDecode(childSqid);
        if (childResult.IsFailure)
        {
            return Result<UserGroupDto>.Failure(childResult.ErrorCode!, childResult.ErrorMessage!);
        }

        var parentId = parentResult.Value;
        var childId = childResult.Value;

        // Self-loop short-circuit.
        if (parentId == childId)
        {
            CnasMeter.UserGroupHierarchyCycleAttempted.Add(1);
            return Result<UserGroupDto>.Failure(ErrorCodes.Conflict, SelfLoopRejectedMessage);
        }

        var parent = await _db.UserGroups
            .SingleOrDefaultAsync(g => g.Id == parentId && g.IsActive, ct)
            .ConfigureAwait(false);
        if (parent is null)
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "Parent group not found.");
        }
        var childExists = await _db.UserGroups
            .AnyAsync(g => g.Id == childId && g.IsActive, ct)
            .ConfigureAwait(false);
        if (!childExists)
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "Child group not found.");
        }

        // Cycle check — walk ancestors of parent; reject if child is among them.
        var ancestors = await CollectAncestorIdsAsync(parentId, ct).ConfigureAwait(false);
        if (ancestors.Contains(childId))
        {
            CnasMeter.UserGroupHierarchyCycleAttempted.Add(1);
            return Result<UserGroupDto>.Failure(ErrorCodes.Conflict, CycleRejectedMessage);
        }

        // Idempotency — silently succeed when the link already exists.
        var exists = await _db.UserGroupParents
            .AnyAsync(p => p.ParentGroupId == parentId && p.ChildGroupId == childId && p.IsActive, ct)
            .ConfigureAwait(false);
        if (!exists)
        {
            var now = _clock.UtcNow;
            _db.UserGroupParents.Add(new UserGroupParent
            {
                ParentGroupId = parentId,
                ChildGroupId = childId,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            });
            parent.UpdatedAtUtc = now;
            parent.UpdatedBy = _caller.UserSqid;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await EmitCriticalAuditAsync(AuditChildAdded, parent.Id, new
            {
                parentSqid = _sqids.Encode(parent.Id),
                childSqid = _sqids.Encode(childId),
            }, ct).ConfigureAwait(false);
        }

        return Result<UserGroupDto>.Success(await ToDtoAsync(parent, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<UserGroupDto>> RemoveChildAsync(string parentSqid, string childSqid, CancellationToken ct = default)
    {
        var parentResult = _sqids.TryDecode(parentSqid);
        if (parentResult.IsFailure)
        {
            return Result<UserGroupDto>.Failure(parentResult.ErrorCode!, parentResult.ErrorMessage!);
        }
        var childResult = _sqids.TryDecode(childSqid);
        if (childResult.IsFailure)
        {
            return Result<UserGroupDto>.Failure(childResult.ErrorCode!, childResult.ErrorMessage!);
        }

        var parentId = parentResult.Value;
        var childId = childResult.Value;

        var parent = await _db.UserGroups
            .SingleOrDefaultAsync(g => g.Id == parentId && g.IsActive, ct)
            .ConfigureAwait(false);
        if (parent is null)
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "Parent group not found.");
        }

        var link = await _db.UserGroupParents
            .SingleOrDefaultAsync(p => p.ParentGroupId == parentId && p.ChildGroupId == childId && p.IsActive, ct)
            .ConfigureAwait(false);
        if (link is null)
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "Nesting link not found.");
        }

        var now = _clock.UtcNow;
        link.IsActive = false;
        link.UpdatedAtUtc = now;
        link.UpdatedBy = _caller.UserSqid;
        parent.UpdatedAtUtc = now;
        parent.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitCriticalAuditAsync(AuditChildRemoved, parent.Id, new
        {
            parentSqid = _sqids.Encode(parent.Id),
            childSqid = _sqids.Encode(childId),
        }, ct).ConfigureAwait(false);

        return Result<UserGroupDto>.Success(await ToDtoAsync(parent, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<UserGroupDto>> AddMemberAsync(string groupSqid, string userSqid, CancellationToken ct = default)
    {
        var groupResult = _sqids.TryDecode(groupSqid);
        if (groupResult.IsFailure)
        {
            return Result<UserGroupDto>.Failure(groupResult.ErrorCode!, groupResult.ErrorMessage!);
        }
        var userResult = _sqids.TryDecode(userSqid);
        if (userResult.IsFailure)
        {
            return Result<UserGroupDto>.Failure(userResult.ErrorCode!, userResult.ErrorMessage!);
        }

        var groupId = groupResult.Value;
        var userId = userResult.Value;

        var group = await _db.UserGroups
            .SingleOrDefaultAsync(g => g.Id == groupId && g.IsActive, ct)
            .ConfigureAwait(false);
        if (group is null)
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "Group not found.");
        }
        var userExists = await _db.UserProfiles
            .AnyAsync(u => u.Id == userId && u.IsActive, ct)
            .ConfigureAwait(false);
        if (!userExists)
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "User not found.");
        }

        var exists = await _db.UserGroupMemberships
            .AnyAsync(m => m.UserGroupId == groupId && m.UserProfileId == userId && m.IsActive, ct)
            .ConfigureAwait(false);
        if (!exists)
        {
            var now = _clock.UtcNow;
            _db.UserGroupMemberships.Add(new UserGroupMembership
            {
                UserGroupId = groupId,
                UserProfileId = userId,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            });
            group.UpdatedAtUtc = now;
            group.UpdatedBy = _caller.UserSqid;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await EmitCriticalAuditAsync(AuditMemberAdded, group.Id, new
            {
                groupSqid = _sqids.Encode(group.Id),
                userSqid = _sqids.Encode(userId),
            }, ct).ConfigureAwait(false);
        }

        return Result<UserGroupDto>.Success(await ToDtoAsync(group, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<UserGroupDto>> RemoveMemberAsync(string groupSqid, string userSqid, CancellationToken ct = default)
    {
        var groupResult = _sqids.TryDecode(groupSqid);
        if (groupResult.IsFailure)
        {
            return Result<UserGroupDto>.Failure(groupResult.ErrorCode!, groupResult.ErrorMessage!);
        }
        var userResult = _sqids.TryDecode(userSqid);
        if (userResult.IsFailure)
        {
            return Result<UserGroupDto>.Failure(userResult.ErrorCode!, userResult.ErrorMessage!);
        }

        var groupId = groupResult.Value;
        var userId = userResult.Value;

        var group = await _db.UserGroups
            .SingleOrDefaultAsync(g => g.Id == groupId && g.IsActive, ct)
            .ConfigureAwait(false);
        if (group is null)
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "Group not found.");
        }

        var membership = await _db.UserGroupMemberships
            .SingleOrDefaultAsync(m => m.UserGroupId == groupId && m.UserProfileId == userId && m.IsActive, ct)
            .ConfigureAwait(false);
        if (membership is null)
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "Membership not found.");
        }

        var now = _clock.UtcNow;
        membership.IsActive = false;
        membership.UpdatedAtUtc = now;
        membership.UpdatedBy = _caller.UserSqid;
        group.UpdatedAtUtc = now;
        group.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitCriticalAuditAsync(AuditMemberRemoved, group.Id, new
        {
            groupSqid = _sqids.Encode(group.Id),
            userSqid = _sqids.Encode(userId),
        }, ct).ConfigureAwait(false);

        return Result<UserGroupDto>.Success(await ToDtoAsync(group, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<UserGroupDto>> GetByIdAsync(string sqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<UserGroupDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var group = await _db.UserGroups
            .SingleOrDefaultAsync(g => g.Id == decoded.Value && g.IsActive, ct)
            .ConfigureAwait(false);
        return group is null
            ? Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "User-group not found.")
            : Result<UserGroupDto>.Success(await ToDtoAsync(group, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<UserGroupDto>> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.ValidationFailed, "code is required.");
        }

        var group = await _db.UserGroups
            .SingleOrDefaultAsync(g => g.Code == code && g.IsActive, ct)
            .ConfigureAwait(false);
        return group is null
            ? Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "User-group not found.")
            : Result<UserGroupDto>.Success(await ToDtoAsync(group, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<UserGroupListPageDto>> ListAsync(UserGroupListFilterDto filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var skip = filter.Skip < 0 ? 0 : filter.Skip;
        var take = filter.Take <= 0 ? 50 : Math.Min(filter.Take, 200);

        IQueryable<UserGroup> query = _db.UserGroups.Where(g => g.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            if (!Enum.TryParse<UserGroupStatus>(filter.Status, ignoreCase: false, out var status))
            {
                return Result<UserGroupListPageDto>.Failure(
                    ErrorCodes.ValidationFailed,
                    "Status must be a known UserGroupStatus enum name.");
            }
            query = query.Where(g => g.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            if (!Enum.TryParse<UserGroupKind>(filter.Kind, ignoreCase: false, out var kind))
            {
                return Result<UserGroupListPageDto>.Failure(
                    ErrorCodes.ValidationFailed,
                    "Kind must be a known UserGroupKind enum name.");
            }
            query = query.Where(g => g.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(filter.RoleCode))
        {
            // Direct grant only — transitive role filtering would require recursive CTE; out of scope for this iteration.
            var role = filter.RoleCode;
            query = query.Where(g => g.Roles.Contains(role));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query
            .OrderBy(g => g.Code)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = new List<UserGroupDto>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(await ToDtoAsync(row, ct).ConfigureAwait(false));
        }

        return Result<UserGroupListPageDto>.Success(
            new UserGroupListPageDto(items, total, skip, take));
    }

    /// <summary>
    /// Shared transition helper for Disable / Enable. Validates the reason
    /// payload, flips the status, and emits the stable Critical audit event.
    /// </summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="requiredCurrent">Status the group must currently hold for the transition to be valid.</param>
    /// <param name="target">Status to transition into.</param>
    /// <param name="auditCode">Stable audit-event code to emit.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated DTO on success; <see cref="ErrorCodes.Conflict"/> when the precondition fails.</returns>
    private async Task<Result<UserGroupDto>> TransitionStatusAsync(
        string sqid,
        UserGroupReasonInputDto input,
        UserGroupStatus requiredCurrent,
        UserGroupStatus target,
        string auditCode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<UserGroupDto>.Failure(
                ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<UserGroupDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var group = await _db.UserGroups
            .SingleOrDefaultAsync(g => g.Id == decoded.Value && g.IsActive, ct)
            .ConfigureAwait(false);
        if (group is null)
        {
            return Result<UserGroupDto>.Failure(ErrorCodes.NotFound, "User-group not found.");
        }
        if (group.Status != requiredCurrent)
        {
            return Result<UserGroupDto>.Failure(
                ErrorCodes.Conflict,
                $"Group must be {requiredCurrent} to transition to {target}.");
        }

        var now = _clock.UtcNow;
        group.Status = target;
        group.UpdatedAtUtc = now;
        group.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitCriticalAuditAsync(auditCode, group.Id, new
        {
            groupSqid = _sqids.Encode(group.Id),
            code = group.Code,
            status = group.Status.ToString(),
            reason = input.Reason,
        }, ct).ConfigureAwait(false);

        return Result<UserGroupDto>.Success(await ToDtoAsync(group, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// BFS walk upward through <c>UserGroupParents</c> collecting every
    /// ancestor of <paramref name="startGroupId"/>. The traversal short-
    /// circuits on revisits so an in-flight cycle does not spin.
    /// </summary>
    /// <param name="startGroupId">The starting group id; its OWN id is NOT included in the result.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The set of ancestor group ids.</returns>
    private async Task<HashSet<long>> CollectAncestorIdsAsync(long startGroupId, CancellationToken ct)
    {
        var ancestors = new HashSet<long>();
        var frontier = new Queue<long>();
        frontier.Enqueue(startGroupId);
        var visited = new HashSet<long> { startGroupId };

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            var parents = await _db.UserGroupParents
                .Where(p => p.ChildGroupId == current && p.IsActive)
                .Select(p => p.ParentGroupId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var parentId in parents)
            {
                if (visited.Add(parentId))
                {
                    ancestors.Add(parentId);
                    frontier.Enqueue(parentId);
                }
            }
        }

        return ancestors;
    }

    /// <summary>Counts the direct memberships and direct children for the supplied group.</summary>
    /// <param name="group">Loaded group entity.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The projected DTO.</returns>
    private async Task<UserGroupDto> ToDtoAsync(UserGroup group, CancellationToken ct)
    {
        var memberCount = await _db.UserGroupMemberships
            .CountAsync(m => m.UserGroupId == group.Id && m.IsActive, ct)
            .ConfigureAwait(false);
        var childCount = await _db.UserGroupParents
            .CountAsync(p => p.ParentGroupId == group.Id && p.IsActive, ct)
            .ConfigureAwait(false);

        // Effective role count = direct + every ancestor's roles (deduped).
        // For Disabled groups roles do not count toward inheritance (matches
        // resolver semantics) — when the group itself is Disabled its direct
        // grants still contribute (admin-facing count is "what this group
        // declares + its ancestors contribute"). To keep the counter simple
        // and resolver-aligned, when the group is Disabled we still count
        // direct roles so the admin sees the configured set.
        var effective = new HashSet<string>(group.Roles, StringComparer.Ordinal);
        var ancestorIds = await CollectAncestorIdsAsync(group.Id, ct).ConfigureAwait(false);
        if (ancestorIds.Count > 0)
        {
            var ancestorRoles = await _db.UserGroups
                .Where(g => ancestorIds.Contains(g.Id) && g.IsActive && g.Status == UserGroupStatus.Active)
                .Select(g => g.Roles)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var roleList in ancestorRoles)
            {
                foreach (var r in roleList)
                {
                    effective.Add(r);
                }
            }
        }

        return new UserGroupDto(
            Id: _sqids.Encode(group.Id),
            Code: group.Code,
            DisplayName: group.DisplayName,
            Description: group.Description,
            Kind: group.Kind.ToString(),
            Status: group.Status.ToString(),
            Roles: group.Roles.ToList(),
            DirectMemberCount: memberCount,
            DirectChildCount: childCount,
            EffectiveRoleCount: effective.Count);
    }

    /// <summary>Emits a Critical audit row with the supplied stable code.</summary>
    /// <param name="code">Stable audit-event code.</param>
    /// <param name="groupId">Group id stamped on the row.</param>
    /// <param name="details">Anonymous object serialised to the details JSON payload.</param>
    /// <param name="ct">Standard cancellation token.</param>
    private async Task EmitCriticalAuditAsync(string code, long groupId, object details, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            code,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(UserGroup),
            groupId,
            payload,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);
    }
}
