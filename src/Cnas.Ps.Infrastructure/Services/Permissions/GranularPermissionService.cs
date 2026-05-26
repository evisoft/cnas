using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Permissions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Permissions;

/// <summary>
/// R0673 / TOR CF 18.12 — default <see cref="IGranularPermissionService"/>
/// implementation. CRUD operations route through
/// <see cref="ICnasDbContext"/>; the hot-path
/// <see cref="HasPermissionAsync"/> probe routes through
/// <see cref="IReadOnlyCnasDbContext"/> so replica-routed deployments do not
/// pay the primary-write cost on every request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defense-in-depth.</b> Every admin write also re-checks the
/// <c>cnas-admin</c> role via <see cref="ICallerContext.Roles"/> — the
/// REST surface gates by policy first, the service double-checks here to
/// honour CLAUDE.md §5.4 ("deny by default").
/// </para>
/// <para>
/// <b>Audit.</b> Assign / Revoke emit a Notice-severity audit row with the
/// stable event code <c>PERMISSION.GRANTED</c> /
/// <c>PERMISSION.REVOKED</c>. The <c>HasPermissionAsync</c> probe is
/// silent — emitting a row per check would flood the audit table.
/// </para>
/// </remarks>
public sealed class GranularPermissionService : IGranularPermissionService
{
    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _readDb;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;

    /// <summary>Constructs the service with its dependencies.</summary>
    /// <param name="db">EF Core write context.</param>
    /// <param name="readDb">Replica-routed read context.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="caller">Authenticated caller — supplies the actor + role envelope.</param>
    /// <param name="audit">Audit sink consulted on every mutation.</param>
    public GranularPermissionService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext readDb,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(readDb);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        _db = db;
        _readDb = readDb;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<GranularPermissionAssignmentDto>> AssignAsync(
        string roleCode,
        string resourceType,
        string permissionVerb,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionVerb);

        if (_caller.UserId is null)
        {
            return Result<GranularPermissionAssignmentDto>.Failure(
                ErrorCodes.Unauthorized, "Not authenticated.");
        }
        if (!_caller.Roles.Contains(RoleCodes.Admin))
        {
            return Result<GranularPermissionAssignmentDto>.Failure(
                ErrorCodes.Forbidden, "Admin role required to mutate the permission matrix.");
        }
        if (!RoleCodes.All.Contains(roleCode))
        {
            return Result<GranularPermissionAssignmentDto>.Failure(
                ErrorCodes.GranularPermissionUnknownRole,
                $"Unknown role code '{roleCode}'.");
        }
        if (!PermissionVerbs.All.Contains(permissionVerb))
        {
            return Result<GranularPermissionAssignmentDto>.Failure(
                ErrorCodes.GranularPermissionUnknownVerb,
                $"Unknown permission verb '{permissionVerb}'.");
        }

        // Idempotency: short-circuit when the triple already exists. This keeps the
        // admin UI safe to re-submit (e.g. double-click on Save) without producing
        // duplicate rows.
        var existing = await _db.GranularPermissionAssignments
            .Where(g => g.RoleCode == roleCode
                && g.ResourceType == resourceType
                && g.PermissionVerb == permissionVerb
                && g.IsActive)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Result<GranularPermissionAssignmentDto>.Success(ToDto(existing));
        }

        var now = _clock.UtcNow;
        var row = new GranularPermissionAssignment
        {
            RoleCode = roleCode,
            ResourceType = resourceType,
            PermissionVerb = permissionVerb,
            GrantedAtUtc = now,
            GrantedByUserId = _caller.UserId,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.GranularPermissionAssignments.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            "PERMISSION.GRANTED",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "system",
            nameof(GranularPermissionAssignment),
            row.Id,
            $"{{\"roleCode\":\"{roleCode}\",\"resourceType\":\"{resourceType}\",\"permissionVerb\":\"{permissionVerb}\"}}",
            sourceIp: null,
            correlationId: null,
            cancellationToken).ConfigureAwait(false);

        return Result<GranularPermissionAssignmentDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result> RevokeAsync(string assignmentSqid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assignmentSqid);
        if (_caller.UserId is null)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }
        if (!_caller.Roles.Contains(RoleCodes.Admin))
        {
            return Result.Failure(ErrorCodes.Forbidden,
                "Admin role required to mutate the permission matrix.");
        }

        var decoded = _sqids.TryDecode(assignmentSqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(ErrorCodes.InvalidSqid, decoded.ErrorMessage!);
        }

        var row = await _db.GranularPermissionAssignments
            .Where(g => g.Id == decoded.Value && g.IsActive)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound,
                $"Permission assignment '{assignmentSqid}' not found or already revoked.");
        }

        row.IsActive = false;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            "PERMISSION.REVOKED",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "system",
            nameof(GranularPermissionAssignment),
            row.Id,
            $"{{\"roleCode\":\"{row.RoleCode}\",\"resourceType\":\"{row.ResourceType}\",\"permissionVerb\":\"{row.PermissionVerb}\"}}",
            sourceIp: null,
            correlationId: null,
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<bool>> HasPermissionAsync(
        string roleCode,
        string resourceType,
        string permissionVerb,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionVerb);

        // Unknown roles / verbs are deny-by-default — never trip a failure here
        // because the hot-path filter relies on a simple boolean answer.
        if (!RoleCodes.All.Contains(roleCode) || !PermissionVerbs.All.Contains(permissionVerb))
        {
            return Result<bool>.Success(false);
        }

        var exists = await _readDb.GranularPermissionAssignments
            .Where(g => g.RoleCode == roleCode
                && g.ResourceType == resourceType
                && g.PermissionVerb == permissionVerb
                && g.IsActive)
            .AnyAsync(cancellationToken).ConfigureAwait(false);
        return Result<bool>.Success(exists);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<GranularPermissionAssignmentDto>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (_caller.UserId is null)
        {
            return Result<IReadOnlyList<GranularPermissionAssignmentDto>>.Failure(
                ErrorCodes.Unauthorized, "Not authenticated.");
        }
        if (!_caller.Roles.Contains(RoleCodes.Admin))
        {
            return Result<IReadOnlyList<GranularPermissionAssignmentDto>>.Failure(
                ErrorCodes.Forbidden, "Admin role required to read the permission matrix.");
        }

        var rows = await _readDb.GranularPermissionAssignments
            .Where(g => g.IsActive)
            .OrderBy(g => g.RoleCode).ThenBy(g => g.ResourceType).ThenBy(g => g.PermissionVerb)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<GranularPermissionAssignmentDto> dtos = rows.Select(ToDto).ToList();
        return Result<IReadOnlyList<GranularPermissionAssignmentDto>>.Success(dtos);
    }

    /// <summary>Projects an entity row to its wire DTO; the grantor user id is encoded if present.</summary>
    /// <param name="row">Entity row to project.</param>
    /// <returns>The Sqid-encoded DTO.</returns>
    private GranularPermissionAssignmentDto ToDto(GranularPermissionAssignment row) => new(
        _sqids.Encode(row.Id),
        row.RoleCode,
        row.ResourceType,
        row.PermissionVerb,
        row.GrantedAtUtc,
        row.GrantedByUserId is { } uid ? _sqids.Encode(uid) : null);
}
