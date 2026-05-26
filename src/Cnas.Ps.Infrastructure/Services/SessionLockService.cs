using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="ISessionLockService"/> — resolves the caller's session via
/// <c>ICallerContext.SessionId</c> and drives the
/// <see cref="UserSession.IsLocked"/> / <see cref="UserSession.IsTerminated"/>
/// flags. See the interface XML doc for the full lock / terminate contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit shape.</b> Every lock / unlock / terminate transition writes a single
/// audit row with a stable event code:
/// </para>
/// <list type="bullet">
///   <item><description><c>USER.SESSION.LOCKED_MANUAL</c> — Notice.</description></item>
///   <item><description><c>USER.SESSION.LOCKED_AUTO</c> — Notice (written by <c>SessionAutoLockJob</c>).</description></item>
///   <item><description><c>USER.SESSION.UNLOCKED_MANUAL</c> — Notice.</description></item>
///   <item><description><c>USER.SESSION.ADMIN_TERMINATED</c> — Critical.</description></item>
/// </list>
/// <para>
/// PII never lands in the audit payload — the row id is the only identifier carried
/// on the DetailsJson; the user and session can be cross-referenced via the
/// <c>TargetEntityId</c> column.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction (scoped per request).</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping.</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller; supplies the audit actor + session id.</param>
/// <param name="audit">Audit journal façade.</param>
public sealed class SessionLockService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit) : ISessionLockService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;

    /// <summary>Role required for the admin force-terminate path; mirrors UsersController.</summary>
    private const string AdminRole = "cnas-admin";

    /// <summary>Termination reason persisted on rows ended by the admin force-terminate path.</summary>
    public const string AdminTerminateReason = "AdminForceTerminate";

    /// <inheritdoc />
    public async Task<Result<UserSessionDto>> LockCurrentSessionAsync(CancellationToken ct = default)
    {
        var sessionId = _caller.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Result<UserSessionDto>.Failure(
                ErrorCodes.Unauthorized, "Caller has no current session id.");
        }

        var row = await _db.UserSessions
            .SingleOrDefaultAsync(s => s.SessionId == sessionId && !s.IsTerminated && s.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<UserSessionDto>.Failure(
                ErrorCodes.NotFound, "Session not found.");
        }

        var now = _clock.UtcNow;
        row.IsLocked = true;
        row.LockedAtUtc = now;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid ?? "?";
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await WriteAuditAsync(
            "USER.SESSION.LOCKED_MANUAL", AuditSeverity.Notice, row.Id, reason: "manual lock", ct)
            .ConfigureAwait(false);

        return Result<UserSessionDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<UserSessionDto>> UnlockCurrentSessionAsync(CancellationToken ct = default)
    {
        var sessionId = _caller.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Result<UserSessionDto>.Failure(
                ErrorCodes.Unauthorized, "Caller has no current session id.");
        }

        var row = await _db.UserSessions
            .SingleOrDefaultAsync(s => s.SessionId == sessionId && !s.IsTerminated && s.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<UserSessionDto>.Failure(
                ErrorCodes.NotFound, "Session not found.");
        }

        var now = _clock.UtcNow;
        row.IsLocked = false;
        row.LockedAtUtc = null;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid ?? "?";
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await WriteAuditAsync(
            "USER.SESSION.UNLOCKED_MANUAL", AuditSeverity.Notice, row.Id, reason: "manual unlock", ct)
            .ConfigureAwait(false);

        return Result<UserSessionDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<bool> IsLockedAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (string.IsNullOrWhiteSpace(sessionId)) return false;

        var row = await _db.UserSessions
            .Where(s => s.SessionId == sessionId && s.IsActive)
            .Select(s => new { s.IsLocked, s.IsTerminated })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null) return false;

        // A terminated session is treated as locked — middleware must reject it
        // regardless of the lock flag's value.
        return row.IsLocked || row.IsTerminated;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<UserSessionDto>>> ListMineAsync(CancellationToken ct = default)
    {
        var userId = _caller.UserId;
        if (userId is null)
        {
            return Result<IReadOnlyList<UserSessionDto>>.Failure(
                ErrorCodes.Unauthorized, "Caller is anonymous.");
        }

        var rows = await _db.UserSessions
            .Where(s => s.UserUserId == userId.Value && !s.IsTerminated && s.IsActive)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);

        IReadOnlyList<UserSessionDto> dtos = rows.Select(ToDto).ToArray();
        return Result<IReadOnlyList<UserSessionDto>>.Success(dtos);
    }

    /// <inheritdoc />
    public async Task<Result> AdminTerminateAsync(
        string userSqid, string sessionSqid, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userSqid);
        ArgumentNullException.ThrowIfNull(sessionSqid);

        if (!_caller.Roles.Contains(AdminRole))
        {
            return Result.Failure(ErrorCodes.Forbidden, "Caller lacks cnas-admin role.");
        }

        var decodedUser = _sqids.TryDecode(userSqid);
        if (decodedUser.IsFailure) return Result.Failure(decodedUser.ErrorCode!, decodedUser.ErrorMessage!);
        var decodedSession = _sqids.TryDecode(sessionSqid);
        if (decodedSession.IsFailure) return Result.Failure(decodedSession.ErrorCode!, decodedSession.ErrorMessage!);

        var row = await _db.UserSessions
            .SingleOrDefaultAsync(s => s.Id == decodedSession.Value
                && s.UserUserId == decodedUser.Value
                && s.IsActive
                && !s.IsTerminated, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Session not found.");
        }

        var now = _clock.UtcNow;
        row.IsTerminated = true;
        row.TerminatedAtUtc = now;
        row.TerminationReason = AdminTerminateReason;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid ?? "?";
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await WriteAuditAsync(
            "USER.SESSION.ADMIN_TERMINATED", AuditSeverity.Critical, row.Id, AdminTerminateReason, ct)
            .ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Writes a single audit row for the supplied <paramref name="eventCode"/> +
    /// <paramref name="severity"/> pair. The DetailsJson carries only the row id +
    /// reason — PII never lands on the audit payload per SEC 044.
    /// </summary>
    /// <param name="eventCode">Stable event code (e.g. <c>USER.SESSION.LOCKED_MANUAL</c>).</param>
    /// <param name="severity">Audit severity (Notice for lock / unlock, Critical for terminate).</param>
    /// <param name="sessionRowId">Raw primary key of the row the audit references.</param>
    /// <param name="reason">Short stable reason captured on the payload.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task WriteAuditAsync(
        string eventCode, AuditSeverity severity, long sessionRowId, string reason, CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            sessionRowId,
            reason,
        });
        await _audit.RecordAsync(
            eventCode,
            severity,
            _caller.UserSqid ?? "?",
            nameof(UserSession),
            sessionRowId,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects a <see cref="UserSession"/> row to the wire-shape
    /// <see cref="UserSessionDto"/>. The opaque <see cref="UserSession.SessionId"/>
    /// is truncated to the first 8 characters so the wire payload never carries a
    /// full credential-equivalent token; the row id is Sqid-encoded per CLAUDE.md
    /// RULE 3.
    /// </summary>
    /// <param name="row">Source EF row.</param>
    /// <returns>The wire-shape DTO.</returns>
    private UserSessionDto ToDto(UserSession row) => new(
        Id: _sqids.Encode(row.Id),
        UserSqid: _sqids.Encode(row.UserUserId),
        SessionId: row.SessionId.Length > 8 ? row.SessionId[..8] : row.SessionId,
        IpAddress: row.IpAddress,
        UserAgent: row.UserAgent,
        CreatedAtUtc: row.CreatedAtUtc,
        LastActivityUtc: row.LastActivityUtc,
        IsLocked: row.IsLocked,
        IsTerminated: row.IsTerminated,
        TerminationReason: row.TerminationReason);
}
