using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Identity;

/// <summary>
/// Default <see cref="ILocalLoginService"/> implementation. Orchestrates the
/// existing credential primitives — <see cref="IPasswordHasher"/>,
/// <see cref="IJwtTokenIssuer"/>, <see cref="IRefreshTokenService"/>,
/// <see cref="ISessionLimitEnforcer"/>, <see cref="IUserAccountStateService"/>,
/// <see cref="IUserGroupRoleResolver"/> — behind a single
/// account-enumeration-safe entry point. See the interface XML doc for the full
/// flow and the SEC 014 / SEC 017 contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every failure returns the same code.</b> Returning distinct error codes
/// for "unknown login" vs. "wrong password" vs. "wrong role" lets an attacker
/// enumerate valid logins one request at a time. The implementation here writes
/// audit rows that carry the precise outcome (<c>USER.LOGIN.UNKNOWN</c>,
/// <c>USER.LOGIN.BAD_PASSWORD</c>, <c>USER.LOGIN.WRONG_ROLE</c>,
/// <c>USER.LOGIN.ACCOUNT_NOT_ACTIVE</c>, <c>USER.LOGIN.AUTO_LOCKED</c>) so ops
/// dashboards can distinguish them, but the wire response is uniformly
/// <see cref="ErrorCodes.LoginInvalid"/>.
/// </para>
/// <para>
/// <b>Argon2 timing equalisation.</b> When the supplied login is unknown we still
/// burn an Argon2 verification against a dummy hash to keep the response timing
/// distribution indistinguishable from "known login / wrong password". The dummy
/// hash is generated lazily and reused across calls.
/// </para>
/// </remarks>
public sealed class LocalLoginService : ILocalLoginService
{
    /// <summary>Stable role code required by SEC 014 for local-login eligibility.</summary>
    private const string RequiredRole = "utilizator-autorizat";

    /// <summary>Consecutive-failure threshold that triggers the auto-lock (CLAUDE.md §5.3).</summary>
    private const int LockoutFailureThreshold = 5;

    /// <summary>Audit event code emitted on every recognised failure outcome.</summary>
    private const string AuditEventUnknownLogin = "USER.LOGIN.UNKNOWN";
    private const string AuditEventBadPassword = "USER.LOGIN.BAD_PASSWORD";
    private const string AuditEventWrongRole = "USER.LOGIN.WRONG_ROLE";
    private const string AuditEventNotActive = "USER.LOGIN.ACCOUNT_NOT_ACTIVE";
    private const string AuditEventAutoLocked = "USER.LOGIN.AUTO_LOCKED";
    private const string AuditEventSuccess = "USER.LOGIN.SUCCESS";
    private const string AuditEventValidationFailed = "USER.LOGIN.VALIDATION_FAILED";

    /// <summary>Cached dummy hash used to equalise response timing on unknown-login.</summary>
    private static string? _dummyHashCache;

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenIssuer _jwtIssuer;
    private readonly IRefreshTokenService _refreshSvc;
    private readonly ISessionLimitEnforcer _sessionEnforcer;
    private readonly IUserAccountStateService _stateSvc;
    private readonly IUserGroupRoleResolver _groupResolver;
    private readonly IAuditService _audit;
    private readonly IFailedLoginAttemptTracker _failureTracker;
    private readonly ISqidService _sqids;
    private readonly ILogger<LocalLoginService> _logger;
    private readonly LocalLoginInputValidator _validator;

    /// <summary>Constructs the service with every collaborator resolved by DI.</summary>
    /// <param name="db">EF Core context — per-request scope.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="hasher">Argon2id password hasher.</param>
    /// <param name="jwtIssuer">JWT access-token issuer.</param>
    /// <param name="refreshSvc">Opaque refresh-token service.</param>
    /// <param name="sessionEnforcer">Session-limit + lifecycle enforcer.</param>
    /// <param name="stateSvc">Account-state service for auto-lock at threshold.</param>
    /// <param name="groupResolver">Group → role resolver for effective-roles union.</param>
    /// <param name="audit">Audit journal façade — critical events mirror to MLog.</param>
    /// <param name="failureTracker">Per-user consecutive failure counter (in-memory by default).</param>
    /// <param name="sqids">Sqid encoder for the user id surfaced on the response.</param>
    /// <param name="logger">Logger for non-PII WARN lines.</param>
    public LocalLoginService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        IPasswordHasher hasher,
        IJwtTokenIssuer jwtIssuer,
        IRefreshTokenService refreshSvc,
        ISessionLimitEnforcer sessionEnforcer,
        IUserAccountStateService stateSvc,
        IUserGroupRoleResolver groupResolver,
        IAuditService audit,
        IFailedLoginAttemptTracker failureTracker,
        ISqidService sqids,
        ILogger<LocalLoginService> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(jwtIssuer);
        ArgumentNullException.ThrowIfNull(refreshSvc);
        ArgumentNullException.ThrowIfNull(sessionEnforcer);
        ArgumentNullException.ThrowIfNull(stateSvc);
        ArgumentNullException.ThrowIfNull(groupResolver);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(failureTracker);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _clock = clock;
        _hasher = hasher;
        _jwtIssuer = jwtIssuer;
        _refreshSvc = refreshSvc;
        _sessionEnforcer = sessionEnforcer;
        _stateSvc = stateSvc;
        _groupResolver = groupResolver;
        _audit = audit;
        _failureTracker = failureTracker;
        _sqids = sqids;
        _logger = logger;
        _validator = new LocalLoginInputValidator();
    }

    /// <inheritdoc />
    public async Task<Result<LocalLoginSuccessDto>> LoginAsync(
        LocalLoginInputDto input,
        string? clientIpAddress,
        string? clientUserAgent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Defensive re-validation. The controller should have rejected malformed
        // input but we re-run the validator so internal callers cannot bypass it.
        var validation = await _validator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            CnasMeter.LocalLoginAttempted.Add(1, new KeyValuePair<string, object?>("outcome", "validation_failed"));
            await RecordAuditAsync(
                eventCode: AuditEventValidationFailed,
                severity: AuditSeverity.Notice,
                actorId: "anonymous",
                targetUserId: null,
                payload: new { reason = "input_validation", clientIpAddress },
                ct).ConfigureAwait(false);
            return Result<LocalLoginSuccessDto>.Failure(
                ErrorCodes.LoginInvalid, "Invalid credentials.");
        }

        var loginLower = input.Login.Trim().ToLowerInvariant();

        // Step 1 — look up the user by case-insensitive LocalLogin. The encrypted
        // columns on UserProfile would defeat a substring match against the column
        // directly, but LocalLogin is intentionally NOT encrypted (it is a login
        // handle, not PII) so the equality match goes through cleanly. EF Core
        // translates the ToLower() call to SQL LOWER(), which is exactly the
        // semantic we want here — the CA1862 suggestion to use
        // string.Equals(..., OrdinalIgnoreCase) cannot be translated to SQL.
#pragma warning disable CA1862 // ToLower required for EF-translatable case-insensitive comparison
        var user = await _db.UserProfiles
            .SingleOrDefaultAsync(
                u => u.IsActive
                    && u.LocalLogin != null
                    && u.LocalLogin.ToLower() == loginLower,
                ct)
            .ConfigureAwait(false);
#pragma warning restore CA1862

        if (user is null)
        {
            // Unknown login. Burn Argon2 verification against a dummy hash so the
            // timing distribution matches "known login / wrong password".
            _ = _hasher.Verify(input.Password, GetDummyHash());

            CnasMeter.LocalLoginAttempted.Add(1, new KeyValuePair<string, object?>("outcome", "unknown_login"));
            await RecordAuditAsync(
                eventCode: AuditEventUnknownLogin,
                severity: AuditSeverity.Notice,
                actorId: "anonymous",
                targetUserId: null,
                payload: new { loginPrefix = loginLower.Length > 2 ? loginLower[..2] : loginLower, clientIpAddress },
                ct).ConfigureAwait(false);
            return Result<LocalLoginSuccessDto>.Failure(
                ErrorCodes.LoginInvalid, "Invalid credentials.");
        }

        // Step 2 — account-state gate (SEC 016). Non-Active accounts cannot sign in
        // — but we evaluate this AFTER the user lookup so unknown-login and
        // non-Active-known-login both flow through the same generic error code.
        if (user.State != UserAccountState.Active)
        {
            CnasMeter.LocalLoginAttempted.Add(1, new KeyValuePair<string, object?>("outcome", "account_not_active"));
            await RecordAuditAsync(
                eventCode: AuditEventNotActive,
                severity: AuditSeverity.Critical,
                actorId: _sqids.Encode(user.Id),
                targetUserId: user.Id,
                payload: new { state = user.State.ToString(), clientIpAddress },
                ct).ConfigureAwait(false);
            return Result<LocalLoginSuccessDto>.Failure(
                ErrorCodes.LoginInvalid, "Invalid credentials.");
        }

        // Step 3 — password verification. Use the stored hash; on accounts without a
        // local password (MPass-only) Verify returns false on an empty hash, which
        // collapses to the same generic "Invalid credentials" — we don't disclose
        // that the user exists but isn't enrolled for local login.
        if (string.IsNullOrEmpty(user.LocalPasswordHash)
            || !_hasher.Verify(input.Password, user.LocalPasswordHash))
        {
            var failureCount = _failureTracker.RecordFailure(user.Id);
            CnasMeter.LocalLoginAttempted.Add(1, new KeyValuePair<string, object?>("outcome", "bad_password"));
            await RecordAuditAsync(
                eventCode: AuditEventBadPassword,
                severity: AuditSeverity.Critical,
                actorId: _sqids.Encode(user.Id),
                targetUserId: user.Id,
                payload: new { consecutiveFailures = failureCount, clientIpAddress },
                ct).ConfigureAwait(false);

            // Auto-lock at the threshold via the canonical state-machine service.
            if (failureCount >= LockoutFailureThreshold)
            {
                var lockResult = await _stateSvc.LockForFailedLoginsAsync(user.Id, ct).ConfigureAwait(false);
                if (lockResult.IsSuccess)
                {
                    CnasMeter.LocalLoginAttempted.Add(1, new KeyValuePair<string, object?>("outcome", "account_locked"));
                    await RecordAuditAsync(
                        eventCode: AuditEventAutoLocked,
                        severity: AuditSeverity.Critical,
                        actorId: "system",
                        targetUserId: user.Id,
                        payload: new { reason = "failed_login_threshold", threshold = LockoutFailureThreshold, clientIpAddress },
                        ct).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning(
                        "Auto-lock failed for userId={UserId} after {Count} consecutive failures: {Code}",
                        user.Id, failureCount, lockResult.ErrorCode);
                }
            }

            return Result<LocalLoginSuccessDto>.Failure(
                ErrorCodes.LoginInvalid, "Invalid credentials.");
        }

        // Step 4 — role gate (SEC 014). Resolve the effective-role union (direct +
        // group-inherited) and confirm the user holds UtilizatorAutorizat. The
        // group resolver returns a structured envelope; we materialise the role
        // code set for both the gate check and the JWT-claim payload.
        var effectiveRoleSet = new HashSet<string>(user.Roles, StringComparer.OrdinalIgnoreCase);
        var groupResult = await _groupResolver.ResolveEffectiveRolesAsync(user.Id, ct).ConfigureAwait(false);
        if (groupResult.IsSuccess && groupResult.Value is not null)
        {
            foreach (var role in groupResult.Value.Roles)
            {
                effectiveRoleSet.Add(role.RoleCode);
            }
        }

        if (!effectiveRoleSet.Contains(RequiredRole))
        {
            CnasMeter.LocalLoginAttempted.Add(1, new KeyValuePair<string, object?>("outcome", "wrong_role"));
            await RecordAuditAsync(
                eventCode: AuditEventWrongRole,
                severity: AuditSeverity.Critical,
                actorId: _sqids.Encode(user.Id),
                targetUserId: user.Id,
                payload: new { requiredRole = RequiredRole, clientIpAddress },
                ct).ConfigureAwait(false);
            return Result<LocalLoginSuccessDto>.Failure(
                ErrorCodes.LoginInvalid, "Invalid credentials.");
        }

        // Step 5 — happy path. Reset the failure counter, issue refresh + access
        // tokens, register the session for concurrent-limit enforcement, audit
        // the success, and return the envelope.
        _failureTracker.Reset(user.Id);

        var refreshResult = await _refreshSvc.IssueAsync(user.Id, ct).ConfigureAwait(false);
        if (refreshResult.IsFailure || refreshResult.Value is null)
        {
            // Refresh-token issuance has no failure modes today but we keep the
            // belt-and-braces branch so a future change cannot silently produce a
            // half-authenticated session.
            _logger.LogError(
                "Refresh-token issue failed for userId={UserId}: {Code}",
                user.Id, refreshResult.ErrorCode);
            return Result<LocalLoginSuccessDto>.Failure(
                ErrorCodes.Internal, "Token issuance failed.");
        }

        var effectiveRoles = effectiveRoleSet.ToArray();
        var (jwt, accessExpires) = _jwtIssuer.IssueAccessToken(
            userId: user.Id,
            roles: effectiveRoles,
            groups: user.Groups);

        // Register the session so SEC 017 concurrent-session cap applies. We use
        // the refresh-token family id as the session identifier so the existing
        // UserSession.SessionId column lines up with the JWT/refresh world.
        var sessionId = refreshResult.Value.FamilyId.ToString("N");
        var sessionRegister = await _sessionEnforcer.RegisterNewSessionAsync(
            userId: user.Id,
            sessionId: sessionId,
            ipAddress: clientIpAddress,
            userAgent: clientUserAgent,
            ct: ct).ConfigureAwait(false);
        if (sessionRegister.IsFailure)
        {
            _logger.LogWarning(
                "Session registration failed for userId={UserId}: {Code}",
                user.Id, sessionRegister.ErrorCode);
        }

        // Stamp the last-login moment for audit/forensic purposes — using the
        // injected clock, never DateTime.UtcNow.
        user.LastLoginUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        CnasMeter.LocalLoginAttempted.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
        await RecordAuditAsync(
            eventCode: AuditEventSuccess,
            severity: AuditSeverity.Notice,
            actorId: _sqids.Encode(user.Id),
            targetUserId: user.Id,
            payload: new { roleCount = effectiveRoles.Length, sessionId, clientIpAddress },
            ct).ConfigureAwait(false);

        var envelope = new LocalLoginSuccessDto(
            AccessToken: jwt,
            AccessTokenExpiresAtUtc: accessExpires,
            RefreshToken: refreshResult.Value.OpaqueToken,
            RefreshTokenExpiresAtUtc: refreshResult.Value.ExpiresAtUtc,
            UserSqid: _sqids.Encode(user.Id),
            DisplayName: user.DisplayName,
            EffectiveRoles: effectiveRoles);
        return Result<LocalLoginSuccessDto>.Success(envelope);
    }

    /// <summary>
    /// Returns (and lazily generates) the dummy Argon2id hash used to equalise
    /// response timing on the unknown-login path. The dummy plaintext is a fixed
    /// internal constant; the hash is computed once per process and cached so
    /// subsequent unknown-login calls pay only the verify cost, not the hash cost.
    /// </summary>
    /// <returns>PHC-formatted Argon2id hash string.</returns>
    private string GetDummyHash()
    {
        // Double-checked locking is unnecessary here because Argon2id.Hash is
        // deterministic only up to a per-call random salt; if two threads race
        // they both compute valid PHC strings and either is fine. We just keep
        // the last writer's value.
        if (_dummyHashCache is null)
        {
            _dummyHashCache = _hasher.Hash("dummy-password-for-timing-equalisation");
        }
        return _dummyHashCache;
    }

    /// <summary>
    /// Writes an audit row with a JSON payload that NEVER contains PII (per
    /// SEC 044 / CLAUDE.md §5.6). Serialises the payload inline so callers do not
    /// need to manage <see cref="JsonSerializer"/> themselves.
    /// </summary>
    /// <param name="eventCode">Stable audit event code (USER.LOGIN.* family).</param>
    /// <param name="severity">Audit severity — Critical for security-relevant failures.</param>
    /// <param name="actorId">Audit actor id — user sqid for known users, <c>"anonymous"</c> otherwise.</param>
    /// <param name="targetUserId">Target user id (raw primary key), or null for unknown login.</param>
    /// <param name="payload">Anonymous object serialised as the audit JSON payload.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RecordAuditAsync(
        string eventCode,
        AuditSeverity severity,
        string actorId,
        long? targetUserId,
        object payload,
        CancellationToken ct)
    {
        var detailsJson = JsonSerializer.Serialize(payload);
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: severity,
            actorId: actorId,
            targetEntity: nameof(UserProfile),
            targetEntityId: targetUserId,
            detailsJson: detailsJson,
            sourceIp: null,
            correlationId: null,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
