using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R2264 / SEC 017 + R2267 / SEC 020 — projection of a <c>UserSession</c> row for
/// admin / self-service surfaces. Every <c>*Sqid</c> field is the Sqid-encoded form
/// of the corresponding raw <c>long</c> primary key (CLAUDE.md RULE 3); the opaque
/// session token is reduced to its first 8 chars to give operators something to
/// pivot dashboards on without exposing a credential-equivalent value.
/// </summary>
/// <param name="Id">Sqid-encoded id of this session row.</param>
/// <param name="UserSqid">Sqid-encoded id of the session owner.</param>
/// <param name="SessionId">
/// First 8 chars of the opaque <c>SessionId</c> on the row (the underlying
/// <c>jti</c> / family id is otherwise NEVER echoed back). Sufficient as a
/// "search key" for ops dashboards while keeping the full token confidential.
/// </param>
/// <param name="IpAddress">Source IP captured at session creation (may be <c>null</c>).</param>
/// <param name="UserAgent">User-Agent string captured at session creation (may be <c>null</c>).</param>
/// <param name="CreatedAtUtc">UTC instant the session was minted by the auth pipeline.</param>
/// <param name="LastActivityUtc">UTC instant the last authenticated request bound to this session was observed.</param>
/// <param name="IsLocked"><c>true</c> when the session has been manually or auto-locked.</param>
/// <param name="IsTerminated"><c>true</c> when the session has been terminated (logout, limit eviction, admin force).</param>
/// <param name="TerminationReason">Stable termination reason, or <c>null</c> while the session remains live.</param>
[SensitivityClassification(SensitivityLabel.Confidential,
    Reason = "Sessions carry IP + UA which are PII-adjacent under TOR SEC 035.")]
public sealed record UserSessionDto(
    string Id,
    string UserSqid,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Session id (or 8-char prefix) is credential-equivalent metadata per R0228 / SEC 033.")]
    string SessionId,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "IpAddress is PII-adjacent network identifier per TOR SEC 035 / R0228.")]
    string? IpAddress,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "UserAgent is device-fingerprint PII per TOR SEC 035 / R0228.")]
    string? UserAgent,
    DateTime CreatedAtUtc,
    DateTime LastActivityUtc,
    bool IsLocked,
    bool IsTerminated,
    string? TerminationReason);

/// <summary>
/// R2263 / SEC 016 — input body for the bulk admin endpoints
/// <c>POST /api/users/bulk-suspend</c> and <c>POST /api/users/bulk-unlock</c>.
/// Carries the target user Sqids plus a free-text reason captured on each
/// per-user audit row.
/// </summary>
/// <param name="UserSqids">
/// Sqid-encoded ids of the target users. 1..200 entries enforced by validator;
/// duplicates are silently de-duplicated by the service layer.
/// </param>
/// <param name="Reason">
/// Free-text justification (e.g. <c>"Compromised credentials — see SOC ticket #1234"</c>).
/// 3..500 chars per validator; captured on every per-user audit row.
/// </param>
public sealed record UserAccountStateBulkInputDto(
    IReadOnlyList<string> UserSqids,
    string Reason);

/// <summary>
/// R2263 / SEC 016 — one row of <see cref="UserAccountStateBulkResultDto.Failures"/>.
/// Carries the failing user's Sqid plus the stable error code that prevented the
/// transition (e.g. <c>USER_ACCOUNT_STATE_TRANSITION_FORBIDDEN</c> for an already-
/// suspended user on a bulk-suspend run).
/// </summary>
/// <param name="UserSqid">Sqid-encoded id of the user the row attempted to mutate.</param>
/// <param name="ErrorCode">Stable error code from <c>Cnas.Ps.Core.Common.ErrorCodes</c>.</param>
/// <param name="Message">Human-readable failure message; safe to surface in admin UI.</param>
public sealed record UserAccountStateBulkResultRowDto(
    string UserSqid,
    string ErrorCode,
    string Message);

/// <summary>
/// R2263 / SEC 016 — result body for the bulk admin endpoints. Reports the totals
/// and a per-row failure list so the UI can render a partial-success summary
/// ("198 of 200 suspended; 2 failures shown below"). On full success the
/// <see cref="Failures"/> list is empty.
/// </summary>
/// <param name="TotalRequested">Number of distinct user Sqids submitted (post-dedup).</param>
/// <param name="Succeeded">Number of rows the service flipped successfully.</param>
/// <param name="Failed">Number of rows that failed; equals <see cref="Failures"/>.Count.</param>
/// <param name="Failures">
/// Per-user failure details. Empty on full success. The service order matches the
/// input order so the UI can correlate rows positionally if it wishes.
/// </param>
public sealed record UserAccountStateBulkResultDto(
    int TotalRequested,
    int Succeeded,
    int Failed,
    IReadOnlyList<UserAccountStateBulkResultRowDto> Failures);
