using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Users;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Users;

/// <summary>
/// R0672 / TOR CF 18.08 — pure-read implementation of
/// <see cref="IUserDeactivationGuard"/>. Runs at most two cheap
/// <c>EXISTS</c>-shaped count queries against the replica-routed read
/// context to decide whether the user has any audit trail.
/// </summary>
/// <remarks>
/// <para>
/// <b>Short-circuit semantics.</b> The two checks are sequential — the
/// first hit (Audit OR History) is enough to return success without
/// running the second query. The history projection is checked first
/// because the iter-123 <c>HistoryTrackingInterceptor</c> emits a row on
/// every insert, including the very initial <c>UserProfile</c> create, so
/// in practice almost every active user trips the first probe.
/// </para>
/// <para>
/// <b>Stable target-entity strings.</b> Both probes filter on the literal
/// <c>"UserProfile"</c> entity name — this is the CLR type name written by
/// both interceptors and consumed by every existing timeline query. Drift
/// would silently break the guard; the integration tests pin the string
/// against the live <c>nameof(UserProfile)</c> so a rename can never sneak
/// in unnoticed.
/// </para>
/// </remarks>
public sealed class UserDeactivationGuard : IUserDeactivationGuard
{
    /// <summary>Read-replica routed DbContext (per-request scope).</summary>
    private readonly IReadOnlyCnasDbContext _readDb;

    /// <summary>Stable target-entity discriminator string. See the type remarks.</summary>
    private const string UserProfileTypeName = nameof(UserProfile);

    /// <summary>
    /// Constructs the guard with its single dependency.
    /// </summary>
    /// <param name="readDb">Read-replica routed DbContext.</param>
    public UserDeactivationGuard(IReadOnlyCnasDbContext readDb)
    {
        ArgumentNullException.ThrowIfNull(readDb);
        _readDb = readDb;
    }

    /// <inheritdoc />
    public async Task<Result> EnsureCanDeactivateAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        // Probe 1 — EntityHistoryRows. Cheap O(log n) lookup on the
        // (EntityType, EntityId) covering index. The HistoryTrackingInterceptor
        // emits a row on Insert / Update / Delete of any IHistoryTracked entity;
        // UserProfile implements IHistoryTracked so an Insert was recorded at
        // user-creation time for everything past iter 123.
        var hasHistory = await _readDb.EntityHistoryRows
            .Where(h => h.EntityType == UserProfileTypeName && h.EntityId == userId)
            .AnyAsync(cancellationToken).ConfigureAwait(false);
        if (hasHistory)
        {
            return Result.Success();
        }

        // Probe 2 — AuditLogs. Backstop for pre-iter-123 users whose history
        // snapshot never landed; the [AutoAudit] interceptor (iter 108) still
        // would have stamped at least one row keyed (TargetEntity ==
        // "UserProfile", TargetEntityId == userId).
        var hasAudit = await _readDb.AuditLogs
            .Where(a => a.TargetEntity == UserProfileTypeName && a.TargetEntityId == userId)
            .AnyAsync(cancellationToken).ConfigureAwait(false);
        if (hasAudit)
        {
            return Result.Success();
        }

        // Neither projection has a row — the user has done nothing auditable
        // since being created. Refuse the soft-delete so we never silently
        // erase a brand-new account without leaving any trail behind.
        return Result.Failure(
            ErrorCodes.UserProfileNoAuditHistory,
            $"User #{userId} has no audit-history rows; soft-delete refused so a trail is preserved.");
    }
}
