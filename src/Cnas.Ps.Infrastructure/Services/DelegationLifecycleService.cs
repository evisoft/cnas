using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="IDelegationLifecycleService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. Implements the R0057 / SEC 026 / CF 16.11 time-bounded
/// delegation lifecycle: grant a window-scoped permission from one user to another,
/// revoke it ahead of its natural expiry, and list the active grants issued by a user.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit payload contains NO PII.</b> The <c>DetailsJson</c> field carries only the
/// participating user ids (raw <see cref="long"/>, used to identify rows in encrypted-at-
/// rest <c>UserProfiles</c> by clearance-holder investigators), the window bounds, the
/// scope discriminator, and the <c>SuspendsGrantorRights</c> flag. The IDNP / email /
/// display name of the participants are intentionally absent (SEC 044).
/// </para>
/// <para>
/// <b>Authorisation surface.</b> The grant endpoint is bound to the calling user — the
/// grantor cannot be impersonated. The revoke endpoint enforces grantor-only revocation
/// at the service boundary; an administrator override goes through a future
/// admin-controller surface (out of scope for this iter — TODO[r0057-admin-revoke]).
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction (scoped per request).</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping (CLAUDE.md RULE 3).</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller; <c>UserId</c> is the grantor on grant, the revoker on revoke.</param>
/// <param name="audit">Audit journal façade; Critical events mirror to MLog per SEC 056.</param>
public sealed class DelegationLifecycleService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit) : IDelegationLifecycleService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;

    /// <summary>Stable event code emitted at successful grant.</summary>
    private const string EventGranted = "DELEGATION.GRANTED";

    /// <summary>Stable event code emitted at successful revocation.</summary>
    private const string EventRevoked = "DELEGATION.REVOKED";

    /// <summary>Single, shared validator instance — no per-call state.</summary>
    private static readonly DelegationGrantInputValidator GrantValidator = new();

    /// <summary>Single, shared revoke-input validator — no per-call state.</summary>
    private static readonly DelegationGrantRevokeInputValidator RevokeValidator = new();

    /// <inheritdoc />
    public async Task<Result<DelegationGrantDto>> GrantAsync(
        string delegateeSqid,
        DateTime validFromUtc,
        DateTime validToUtc,
        bool suspendsGrantorRights,
        string scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (_caller.UserId is not long grantorId)
        {
            return Result<DelegationGrantDto>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // Validate the shape (window + scope length) at the service boundary so the
        // controller doesn't need its own validator wiring. Self-delegation is checked
        // separately below because it requires the grantor's id which the DTO does not
        // carry.
        var validation = GrantValidator.Validate(new DelegationGrantInputDto(
            delegateeSqid, validFromUtc, validToUtc, suspendsGrantorRights, scope));
        if (!validation.IsValid)
        {
            return Result<DelegationGrantDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var decoded = _sqids.TryDecode(delegateeSqid);
        if (decoded.IsFailure)
        {
            return Result<DelegationGrantDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var delegateeId = decoded.Value;

        // Self-delegation: no business value AND complicates the audit trail
        // ("X delegated to X" is meaningless). Rejected at the service boundary.
        if (delegateeId == grantorId)
        {
            return Result<DelegationGrantDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Grantor and delegatee must differ.");
        }

        var delegateeExists = await _db.UserProfiles
            .AnyAsync(u => u.Id == delegateeId && u.IsActive, ct)
            .ConfigureAwait(false);
        if (!delegateeExists)
        {
            return Result<DelegationGrantDto>.Failure(
                ErrorCodes.NotFound, "Delegatee user not found.");
        }

        var now = _clock.UtcNow;
        var row = new DelegationGrant
        {
            GrantorUserId = grantorId,
            DelegateeUserId = delegateeId,
            ValidFromUtc = validFromUtc,
            ValidToUtc = validToUtc,
            SuspendsGrantorRights = suspendsGrantorRights,
            Scope = scope,
            GrantedAtUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.DelegationGrants.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Audit AFTER persistence so a SaveChanges throw doesn't leave a "GRANTED" row
        // without a backing grant — the audit chain stays consistent with the DB.
        await _audit.RecordAsync(
            EventGranted,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(DelegationGrant),
            row.Id,
            BuildGrantedPayload(row),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<DelegationGrantDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result> RevokeAsync(
        string grantSqid,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        if (_caller.UserId is not long callerId)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // Validate the reason envelope (3..500 chars) at the service boundary so the
        // controller surface stays declarative.
        var validation = RevokeValidator.Validate(new DelegationGrantRevokeInputDto(reason));
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var decoded = _sqids.TryDecode(grantSqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.DelegationGrants
            .SingleOrDefaultAsync(g => g.Id == decoded.Value && g.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Delegation grant not found.");
        }

        // Only the original grantor may revoke at this surface. Admin revoke lives on
        // a future controller-side surface (TODO[r0057-admin-revoke]).
        if (row.GrantorUserId != callerId)
        {
            return Result.Failure(
                ErrorCodes.Forbidden,
                "Only the grantor may revoke this delegation.");
        }

        // Idempotent: a duplicate revoke on an already-revoked row is a no-op success
        // because the grant has already been closed by definition. Mirroring the
        // pending-action service's "already decided" semantics would surface a 409 here
        // which adds no value to the citizen.
        if (row.RevokedAtUtc is not null)
        {
            return Result.Success();
        }

        var now = _clock.UtcNow;
        row.RevokedAtUtc = now;
        row.RevokeReason = reason;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            EventRevoked,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(DelegationGrant),
            row.Id,
            BuildRevokedPayload(row, reason),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<DelegationGrantDto>>> ListActiveAsync(
        string userSqid,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(userSqid);
        if (decoded.IsFailure)
        {
            return Result<IReadOnlyList<DelegationGrantDto>>.Failure(
                decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var grantorId = decoded.Value;

        // Confirm the grantor row exists before issuing the active-grant probe so an
        // unknown id surfaces as NotFound rather than as an empty list.
        var exists = await _db.UserProfiles
            .AnyAsync(u => u.Id == grantorId && u.IsActive, ct)
            .ConfigureAwait(false);
        if (!exists)
        {
            return Result<IReadOnlyList<DelegationGrantDto>>.Failure(
                ErrorCodes.NotFound, "User not found.");
        }

        var now = _clock.UtcNow;
        var rows = await _db.DelegationGrants
            .Where(g => g.IsActive
                        && g.GrantorUserId == grantorId
                        && g.RevokedAtUtc == null
                        && g.ValidFromUtc <= now
                        && g.ValidToUtc >= now)
            .OrderBy(g => g.ValidFromUtc)
            .ToListAsync(ct).ConfigureAwait(false);

        IReadOnlyList<DelegationGrantDto> items = rows.Select(Project).ToList();
        return Result<IReadOnlyList<DelegationGrantDto>>.Success(items);
    }

    /// <summary>
    /// Projects a <see cref="DelegationGrant"/> row to the boundary-crossing DTO,
    /// Sqid-encoding every long primary key per CLAUDE.md RULE 3.
    /// </summary>
    /// <param name="row">Persisted row to project.</param>
    /// <returns>The DTO carrying Sqid strings.</returns>
    private DelegationGrantDto Project(DelegationGrant row) => new(
        Id: _sqids.Encode(row.Id),
        GrantorUserId: _sqids.Encode(row.GrantorUserId),
        DelegateeUserId: _sqids.Encode(row.DelegateeUserId),
        ValidFromUtc: row.ValidFromUtc,
        ValidToUtc: row.ValidToUtc,
        SuspendsGrantorRights: row.SuspendsGrantorRights,
        Scope: row.Scope,
        GrantedAtUtc: row.GrantedAtUtc,
        RevokedAtUtc: row.RevokedAtUtc,
        RevokeReason: row.RevokeReason);

    /// <summary>
    /// Serialises the audit payload emitted at grant time. PII-free — the participants
    /// are identified by raw user ids; the IDNP / email / display name fields stay in
    /// the encrypted-at-rest <c>UserProfiles</c> table for clearance-holder access.
    /// </summary>
    /// <param name="row">The persisted grant row.</param>
    /// <returns>JSON object literal.</returns>
    private static string BuildGrantedPayload(DelegationGrant row) =>
        JsonSerializer.Serialize(new
        {
            grantorUserId = row.GrantorUserId,
            delegateeUserId = row.DelegateeUserId,
            validFromUtc = row.ValidFromUtc,
            validToUtc = row.ValidToUtc,
            scope = row.Scope,
            suspendsGrantorRights = row.SuspendsGrantorRights,
        });

    /// <summary>
    /// Serialises the audit payload emitted at revoke time. Captures the reason and
    /// the participating user ids so investigators can reconstruct the timeline.
    /// </summary>
    /// <param name="row">The mutated grant row.</param>
    /// <param name="reason">The revocation reason (already validated).</param>
    /// <returns>JSON object literal.</returns>
    private static string BuildRevokedPayload(DelegationGrant row, string reason) =>
        JsonSerializer.Serialize(new
        {
            grantorUserId = row.GrantorUserId,
            delegateeUserId = row.DelegateeUserId,
            scope = row.Scope,
            reason,
        });
}
