namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R2264 / R2267 — operator-tunable knobs governing the concurrent-session limit
/// (R2264 / SEC 017) and the idle auto-lock threshold (R2265 / R2267 / SEC 020).
/// Bound from the <c>Cnas:SessionLimit</c> configuration section by
/// <c>InfrastructureServiceCollectionExtensions.AddCnasInfrastructure</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why 3 + 15.</b> The default ceiling of 3 concurrent sessions covers the
/// realistic "laptop + mobile + dedicated terminal" use case for CNAS staff while
/// still blocking the typical credential-stuffing pattern (dozens of sessions from
/// rotating IPs). The 15-minute idle threshold mirrors the cookie expiration set on
/// the auth handler (R2265 / SEC 018) so the JWT-bearer surface lines up with the
/// cookie surface for staff using both. Operators tune both values per environment
/// — production typically keeps 3 / 15, staging may relax to 5 / 30 to ease E2E
/// fixtures.
/// </para>
/// </remarks>
public sealed class SessionLimitOptions
{
    /// <summary>Configuration section name — bind from <c>Cnas:SessionLimit</c>.</summary>
    public const string SectionName = "Cnas:SessionLimit";

    /// <summary>
    /// Maximum number of concurrent live sessions a single user may hold. When a
    /// fresh sign-in would push the count past this ceiling, the
    /// <c>ISessionLimitEnforcer</c> terminates the oldest live session (FIFO).
    /// Default 3.
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = 3;

    /// <summary>
    /// Idle threshold in minutes after which the <c>SessionAutoLockJob</c> flips a
    /// session to <c>IsLocked=true</c>. Comparison uses
    /// <c>UserSession.LastActivityUtc &lt; UtcNow - IdleLockMinutes</c>. Default 15
    /// minutes (matches the staff cookie expiration per R2265 / SEC 018).
    /// </summary>
    public int IdleLockMinutes { get; set; } = 15;
}
