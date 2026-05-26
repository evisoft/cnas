using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="ISessionLimitEnforcer"/> — inserts the new session row, counts
/// live rows for the user, and force-terminates the oldest row when the configured
/// ceiling is exceeded. See the interface XML doc for the full FIFO eviction
/// contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Persistence.</b> Both the insert and the (optional) eviction land in a single
/// <c>SaveChangesAsync</c>. The eviction is a row-level mutation so EF Core's
/// optimistic-concurrency token guards against a concurrent terminator clobbering
/// the row; if the SaveChanges call fails the entire registration fails (we don't
/// want to leave a "dangling" new row without enforcing the limit).
/// </para>
/// <para>
/// <b>Audit.</b> Each eviction writes a Critical-severity row with event code
/// <c>USER.SESSION.TERMINATED_BY_LIMIT</c> so dashboards can chart per-user
/// "too many sessions" pressure separately from manual sign-out activity.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction (scoped per request).</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller; supplies the audit-actor sqid.</param>
/// <param name="audit">Audit journal façade.</param>
/// <param name="options">Tunable session-limit knobs.</param>
public sealed class SessionLimitEnforcer(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    IOptions<SessionLimitOptions> options) : ISessionLimitEnforcer
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly SessionLimitOptions _options = options.Value;

    /// <summary>Sentinel actor id used by the enforcement pipeline (no human admin in the loop).</summary>
    private const string SystemActor = "system";

    /// <summary>Termination reason persisted on rows evicted by the enforcer.</summary>
    public const string EvictionReason = "ConcurrentLimitExceeded";

    /// <inheritdoc />
    public async Task<Result> RegisterNewSessionAsync(
        long userId,
        string sessionId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "sessionId is required.");
        }

        var now = _clock.UtcNow;

        var actorSqid = _caller.UserSqid ?? SystemActor;
        var row = new UserSession
        {
            UserUserId = userId,
            SessionId = sessionId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            LastActivityUtc = now,
            CreatedAtUtc = now,
            CreatedBy = actorSqid,
            IsActive = true,
            IsLocked = false,
            IsTerminated = false,
        };
        _db.UserSessions.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Count live (non-terminated) sessions including the row we just inserted.
        // EF Core resolves this against the underlying provider — Postgres uses the
        // composite (UserUserId, IsTerminated, CreatedAtUtc) index.
        var live = await _db.UserSessions
            .Where(s => s.UserUserId == userId && !s.IsTerminated && s.IsActive)
            .OrderBy(s => s.CreatedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);

        // Evict overflow oldest-first. The loop is bounded by (live.Count - max) so a
        // single sign-in cannot evict more than one row in the steady-state — but the
        // arithmetic also covers historical drift if the ceiling was lowered after
        // the user accumulated more sessions than the new cap allows.
        var maxAllowed = Math.Max(1, _options.MaxConcurrentSessions);
        var overflow = live.Count - maxAllowed;
        var evictionsAudit = new List<(long Id, long UserId)>(overflow > 0 ? overflow : 0);
        if (overflow > 0)
        {
            for (var i = 0; i < overflow; i++)
            {
                var victim = live[i];
                victim.IsTerminated = true;
                victim.TerminatedAtUtc = now;
                victim.TerminationReason = EvictionReason;
                victim.UpdatedAtUtc = now;
                victim.UpdatedBy = actorSqid;
                evictionsAudit.Add((victim.Id, victim.UserUserId));
            }
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Audit each eviction AFTER persistence — if SaveChanges threw we never
        // reach this block, so the audit count and the DB state stay in lock-step.
        foreach (var (id, _) in evictionsAudit)
        {
            var details = JsonSerializer.Serialize(new
            {
                userId,
                evictedSessionId = id,
                reason = EvictionReason,
            });
            await _audit.RecordAsync(
                "USER.SESSION.TERMINATED_BY_LIMIT",
                AuditSeverity.Critical,
                actorSqid,
                nameof(UserSession),
                id,
                details,
                _caller.SourceIp,
                _caller.CorrelationId,
                ct).ConfigureAwait(false);
        }

        return Result.Success();
    }
}
