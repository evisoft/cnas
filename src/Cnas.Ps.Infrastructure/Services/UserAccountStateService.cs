using System.Collections.Frozen;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="IUserAccountStateService"/> implementation. Validates every
/// transition against the deny-by-default allow-list, writes the mandatory
/// <see cref="AuditSeverity.Critical"/> audit row, and saves both the state mutation
/// and the audit insert in one unit-of-work. See the interface XML doc for the full
/// transition matrix and audit contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit payload contains NO PII.</b> The <c>DetailsJson</c> field carries only
/// the (from, to, reason) trio plus a literal payload schema marker. The user's
/// IDNP / email are explicitly omitted (SEC 044) — the audit row identifies the
/// target by raw <c>TargetEntityId</c> (the user primary key) which can be cross-
/// referenced against the encrypted-at-rest <c>UserProfiles</c> table by an
/// investigator with appropriate clearance.
/// </para>
/// <para>
/// <b>Defense-in-depth role check.</b> The
/// <c>UsersController.ChangeStateAsync</c> endpoint already gates on
/// <c>[Authorize(Policy = CnasAdmin)]</c>, but the service re-checks the role
/// (mirroring <see cref="UserAdministrationService"/>) so an internal caller that
/// invokes the service directly cannot bypass the gate. The auto-lock convenience
/// path <see cref="LockForFailedLoginsAsync"/> deliberately SKIPS the role check
/// because the caller is the framework itself rather than an authenticated admin.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction (scoped per request).</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping (CLAUDE.md RULE 3).</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller; <c>UserSqid</c> becomes the audit actor.</param>
/// <param name="audit">Audit journal façade; critical events mirror to MLog per SEC 056.</param>
public sealed class UserAccountStateService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit) : IUserAccountStateService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;

    /// <summary>Role required for human-driven transitions; auto-lock bypasses this gate.</summary>
    private const string AdminRole = "cnas-admin";

    /// <summary>Sentinel actor id used by the auto-lock pipeline (no human admin in the loop).</summary>
    private const string SystemActor = "system";

    /// <summary>
    /// Static transition matrix — deny-by-default. Each key (current state) maps to the
    /// set of permitted destination states. Stored as a <see cref="FrozenDictionary{TKey, TValue}"/>
    /// so the hot-path lookup is O(1) without per-call allocation.
    /// </summary>
    private static readonly FrozenDictionary<UserAccountState, FrozenSet<UserAccountState>> AllowedTransitions =
        new Dictionary<UserAccountState, FrozenSet<UserAccountState>>
        {
            [UserAccountState.Active] = new[]
            {
                UserAccountState.Suspended,
                UserAccountState.Disabled,
                UserAccountState.Locked,
            }.ToFrozenSet(),
            [UserAccountState.Suspended] = new[]
            {
                UserAccountState.Active,
                UserAccountState.Disabled,
            }.ToFrozenSet(),
            [UserAccountState.Locked] = new[]
            {
                UserAccountState.Active,
                UserAccountState.Disabled,
            }.ToFrozenSet(),
            [UserAccountState.Disabled] = new[]
            {
                UserAccountState.Active,
            }.ToFrozenSet(),
        }.ToFrozenDictionary();

    /// <inheritdoc />
    public async Task<Result> ChangeStateAsync(
        string userSqid,
        UserAccountState newState,
        string? reason,
        CancellationToken ct = default)
    {
        // Defense-in-depth — see class remarks for why we re-check the role here even
        // though the controller's [Authorize] attribute is the primary gate.
        if (!_caller.Roles.Contains(AdminRole))
        {
            return Result.Failure(ErrorCodes.Forbidden, "Caller lacks cnas-admin role.");
        }

        var decoded = _sqids.TryDecode(userSqid);
        if (decoded.IsFailure) return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);

        var user = await _db.UserProfiles
            .SingleOrDefaultAsync(u => u.Id == decoded.Value && u.IsActive, ct)
            .ConfigureAwait(false);
        if (user is null) return Result.Failure(ErrorCodes.NotFound, "User not found.");

        return await TransitionAsync(user, newState, _caller.UserSqid ?? "?", reason, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> LockForFailedLoginsAsync(long userId, CancellationToken ct = default)
    {
        var user = await _db.UserProfiles
            .SingleOrDefaultAsync(u => u.Id == userId && u.IsActive, ct)
            .ConfigureAwait(false);
        if (user is null) return Result.Failure(ErrorCodes.NotFound, "User not found.");

        // Idempotent — re-lock of an already-locked account succeeds silently without a
        // duplicate audit row. The failed-login pipeline calls this on every Nth failure
        // and we don't want N audit rows for the same security event.
        if (user.State == UserAccountState.Locked)
        {
            return Result.Success();
        }

        // Disabled accounts are already non-Active and reject sign-in; auto-locking
        // them would muddle the audit trail without changing security posture.
        if (user.State == UserAccountState.Disabled)
        {
            return Result.Failure(
                ErrorCodes.UserAccountStateTransitionForbidden,
                $"Cannot auto-lock account in state {user.State}.");
        }

        return await TransitionAsync(
                user,
                UserAccountState.Locked,
                SystemActor,
                reason: "auto-lock due to failed-login threshold",
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared transition body — validates the (from → to) pair against
    /// <see cref="AllowedTransitions"/>, mutates the row, writes the audit entry, and
    /// commits in a single <c>SaveChangesAsync</c>. Callers are responsible for
    /// loading the row and any role-based pre-checks.
    /// </summary>
    /// <param name="user">The loaded target user (already filtered by IsActive).</param>
    /// <param name="newState">Desired new state.</param>
    /// <param name="actorId">Audit actor id — admin Sqid for human transitions, <c>"system"</c> for auto-lock.</param>
    /// <param name="reason">Optional free-form reason captured on the audit row; may be null.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<Result> TransitionAsync(
        UserProfile user,
        UserAccountState newState,
        string actorId,
        string? reason,
        CancellationToken ct)
    {
        var currentState = user.State;
        if (!AllowedTransitions.TryGetValue(currentState, out var allowed)
            || !allowed.Contains(newState))
        {
            return Result.Failure(
                ErrorCodes.UserAccountStateTransitionForbidden,
                $"Transition from {currentState} to {newState} is not permitted.");
        }

        var now = _clock.UtcNow;
        user.State = newState;
        user.UpdatedAtUtc = now;
        user.UpdatedBy = _caller.UserSqid ?? actorId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Audit row — stable event code shape is "USER.STATE_CHANGE.<FROM>.<TO>" so
        // dashboards can split by the (from, to) pair without parsing the payload.
        // The payload itself carries the reason and a schema marker; it must NEVER
        // contain PII (SEC 044) — see class remarks.
        var eventCode = $"USER.STATE_CHANGE.{currentState}.{newState}";
        var details = BuildAuditDetails(currentState, newState, reason);

        await _audit.RecordAsync(
            eventCode,
            AuditSeverity.Critical,
            actorId,
            nameof(UserProfile),
            user.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Serialises the audit payload as a compact JSON object. Inline construction via
    /// <see cref="JsonSerializer"/> keeps the dependency surface small and matches the
    /// JSON-as-string convention used by the sibling services. PII is intentionally
    /// absent from the payload — the row is identified by its raw primary key on the
    /// <c>TargetEntityId</c> column.
    /// </summary>
    /// <param name="from">Source state.</param>
    /// <param name="to">Destination state.</param>
    /// <param name="reason">Optional free-form reason; serialised as null when absent.</param>
    /// <returns>JSON object literal, e.g. <c>{"from":"Active","to":"Suspended","reason":"..."}</c>.</returns>
    private static string BuildAuditDetails(UserAccountState from, UserAccountState to, string? reason) =>
        JsonSerializer.Serialize(new
        {
            from = from.ToString(),
            to = to.ToString(),
            reason,
        });

    /// <inheritdoc />
    public async Task<Result<UserAccountStateBulkResultDto>> BulkSuspendAsync(
        IReadOnlyList<string> userSqids,
        string reason,
        CancellationToken ct = default)
        => await RunBulkTransitionAsync(
                userSqids, reason, UserAccountState.Active, UserAccountState.Suspended, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Result<UserAccountStateBulkResultDto>> BulkUnlockAsync(
        IReadOnlyList<string> userSqids,
        string reason,
        CancellationToken ct = default)
        => await RunBulkTransitionAsync(
                userSqids, reason, UserAccountState.Locked, UserAccountState.Active, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Shared bulk-transition body for <see cref="BulkSuspendAsync"/> and
    /// <see cref="BulkUnlockAsync"/>. Iterates the supplied user-sqid list in
    /// submission order, attempts each transition, and assembles a single
    /// <see cref="UserAccountStateBulkResultDto"/> describing per-row success or
    /// failure. The role-gate runs once up-front (defense-in-depth alongside the
    /// controller's <c>[Authorize]</c>); a single failure aborts the entire run with
    /// <see cref="ErrorCodes.Forbidden"/> rather than masking the auth issue per row.
    /// </summary>
    /// <param name="userSqids">Input sqid list (de-duplicated in-place).</param>
    /// <param name="reason">Free-text reason captured on every per-user audit row.</param>
    /// <param name="expectedFrom">Required current state (rows in any other state are reported as failures).</param>
    /// <param name="targetTo">Destination state to flip matching rows to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Bulk result envelope (always returned as <see cref="Result{T}.Success"/> unless the role gate trips).</returns>
    private async Task<Result<UserAccountStateBulkResultDto>> RunBulkTransitionAsync(
        IReadOnlyList<string> userSqids,
        string reason,
        UserAccountState expectedFrom,
        UserAccountState targetTo,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userSqids);
        ArgumentNullException.ThrowIfNull(reason);

        if (!_caller.Roles.Contains(AdminRole))
        {
            return Result<UserAccountStateBulkResultDto>.Failure(
                ErrorCodes.Forbidden, "Caller lacks cnas-admin role.");
        }

        // De-duplicate to protect against operator slip-ups (two copies of the same
        // sqid in the input list) without rejecting the entire call.
        var distinct = new List<string>(userSqids.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sqid in userSqids)
        {
            if (seen.Add(sqid)) distinct.Add(sqid);
        }

        var failures = new List<UserAccountStateBulkResultRowDto>();
        var succeeded = 0;

        foreach (var sqid in distinct)
        {
            // ChangeStateAsync re-validates the role + decodes the sqid + writes the
            // audit row. We re-use it so the audit shape is identical to single-row
            // transitions and so any future logic added there (e.g. additional
            // pre-checks) applies uniformly to bulk runs.
            var outcome = await ChangeStateAsync(sqid, targetTo, reason, ct)
                .ConfigureAwait(false);

            if (outcome.IsSuccess)
            {
                succeeded += 1;
                continue;
            }

            // Distinguish "row was already in the target state" from "row was in a
            // different state we don't want to touch". The state machine surfaces
            // both as UserAccountStateTransitionForbidden; the bulk error message
            // hints at the most likely cause so operators don't have to cross-
            // reference the per-user state to interpret the report.
            var code = outcome.ErrorCode ?? ErrorCodes.Internal;
            var message = outcome.ErrorMessage ?? "Transition refused.";
            if (code == ErrorCodes.UserAccountStateTransitionForbidden)
            {
                // Use a hint when the row probably already holds the target state.
                message = expectedFrom == UserAccountState.Active && targetTo == UserAccountState.Suspended
                    ? "User is not in the Active state (probably already suspended)."
                    : expectedFrom == UserAccountState.Locked && targetTo == UserAccountState.Active
                        ? "User is not in the Locked state (probably already unlocked)."
                        : message;
            }
            failures.Add(new UserAccountStateBulkResultRowDto(sqid, code, message));
        }

        var result = new UserAccountStateBulkResultDto(
            TotalRequested: distinct.Count,
            Succeeded: succeeded,
            Failed: failures.Count,
            Failures: failures);
        return Result<UserAccountStateBulkResultDto>.Success(result);
    }
}
