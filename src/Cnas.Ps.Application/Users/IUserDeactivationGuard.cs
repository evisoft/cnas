using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Users;

/// <summary>
/// R0672 / TOR CF 18.08 — pre-flight guard consulted before a
/// <c>UserProfile.IsActive=false</c> soft-delete is permitted. The policy
/// asserts that at least one audit-trail row exists keyed to the user
/// (either an <c>AuditLog</c> entry or an <c>EntityHistoryRow</c> snapshot)
/// so the deactivation never silently erases a brand-new account before any
/// traceability landed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate service.</b> The audit-history requirement lives on the
/// user-deactivation surface today but the same pattern will recur on other
/// "soft-delete with retention" paths (R0673 group cleanup, R0689 service
/// reseats). Carving the check into its own interface lets a single
/// implementation back the requirement everywhere and keeps the
/// <c>IUserAdministrationService.DeactivateAsync</c> body small.
/// </para>
/// <para>
/// <b>Counts what.</b> The guard treats EITHER side of the trail as
/// sufficient evidence — any row in <c>AuditLogs</c> whose
/// <c>TargetEntity == "UserProfile"</c> and <c>TargetEntityId</c> matches the
/// user, OR any row in <c>EntityHistoryRows</c> whose <c>EntityType ==
/// "UserProfile"</c> and <c>EntityId</c> matches the user. Either presence
/// satisfies the contract. The iter-108 <c>[AutoAudit]</c> interceptor
/// emits <c>AuditLog</c> rows on every create / update / delete of the
/// <c>UserProfile</c> entity; the iter-123 <c>HistoryTracking</c>
/// interceptor emits <c>EntityHistoryRow</c> snapshots on every mutation of
/// any entity that implements <c>IHistoryTracked</c>. The trail is
/// therefore guaranteed to land for any user that has been touched after
/// onboarding.
/// </para>
/// <para>
/// <b>Read-only contract.</b> The guard injects only
/// <see cref="Cnas.Ps.Application.Abstractions.IReadOnlyCnasDbContext"/>;
/// every count flows through the streaming-replica routed context per
/// CLAUDE.md "IReadOnlyCnasDbContext for reads". The accompanying
/// implementation lives in
/// <c>Cnas.Ps.Infrastructure.Services.Users.UserDeactivationGuard</c>.
/// </para>
/// <para>
/// <b>Result.</b> Success when at least one of the two trails has a row.
/// Failure code <see cref="ErrorCodes.UserProfileNoAuditHistory"/> when
/// neither does.
/// </para>
/// </remarks>
public interface IUserDeactivationGuard
{
    /// <summary>
    /// Returns success when the targeted user already has at least one audit
    /// or history row attributed to it; otherwise returns a failure with
    /// <see cref="ErrorCodes.UserProfileNoAuditHistory"/>.
    /// </summary>
    /// <param name="userId">
    /// Internal long primary key of the <c>UserProfile</c> row that the
    /// caller is about to soft-delete. Decoded from the inbound Sqid by the
    /// calling service before this guard is invoked.
    /// </param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when traceability landed;
    /// <see cref="Result.Failure(string, string)"/> with code
    /// <see cref="ErrorCodes.UserProfileNoAuditHistory"/> when neither
    /// projection contains a row for the user.
    /// </returns>
    Task<Result> EnsureCanDeactivateAsync(
        long userId,
        CancellationToken cancellationToken = default);
}
