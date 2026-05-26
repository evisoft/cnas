using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Profilul utilizatorului — authorised user's account inside SI PS. TOR §2.3 #8.
/// </summary>
/// <remarks>
/// The user's identity is anchored to their MPass subject id (sub claim); password
/// authentication is permitted only for the "Utilizator autorizat" role per SEC 014.
/// Password hashes use ASP.NET Core's PBKDF2-based identity hashing.
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Notice, EventCodePrefix = "USERPROFILE")]
public sealed class UserProfile : AuditableEntity, IExternalId, IHistoryTracked
{
    /// <summary>MPass subject id when the user authenticates via MPass; null for local accounts.</summary>
    public string? MPassSubject { get; set; }

    /// <summary>Local login (for "Utilizator autorizat" role only — SEC 014).</summary>
    public string? LocalLogin { get; set; }

    /// <summary>
    /// PBKDF2 hash of the password for local accounts. Never store plaintext. Empty for
    /// MPass-only accounts.
    /// </summary>
    public string? LocalPasswordHash { get; set; }

    /// <summary>National id, populated from MPass userinfo.</summary>
    /// <remarks>
    /// <para>
    /// Encrypted at rest via <c>EncryptedStringConverter</c> (CLAUDE.md §5.7 / TOR SEC 035).
    /// Because every encryption samples a fresh nonce, equality lookups against this column
    /// (<c>WHERE NationalId == X</c>) cease to work in production; use the
    /// <see cref="NationalIdHash"/> shadow column instead.
    /// </para>
    /// </remarks>
    public string? NationalId { get; set; }

    /// <summary>
    /// Deterministic HMAC-SHA256 of the canonicalized <see cref="NationalId"/>, or
    /// <c>null</c> when <see cref="NationalId"/> is null. Backs equality lookups that the
    /// encrypted plaintext column can no longer support. Stored as base64 (44 chars).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Synchronization is the application layer's responsibility.</b> Every site that
    /// writes <see cref="NationalId"/> MUST also write this column via
    /// <c>Cnas.Ps.Application.Abstractions.IDeterministicHasher.ComputeHash</c> on
    /// the same value, OR null when the plaintext is null. See
    /// <c>Solicitant.NationalIdHash</c> remarks for the rationale.
    /// </para>
    /// </remarks>
    public string? NationalIdHash { get; set; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Email — used for in-app notifications via MNotify.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// Citizen-supplied contact phone number in canonical E.164 form
    /// (leading <c>+</c>, then 2-15 digits). <c>null</c> when not provided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PII per TOR SEC 035 / CLAUDE.md §5.7 — stored encrypted at rest via
    /// <c>EncryptedStringConverter</c> wired in <c>CnasDbContext.OnModelCreating</c>.
    /// The conversion happens at the ORM boundary; consumers always see plaintext.
    /// </para>
    /// <para>
    /// No hash shadow column is provided: phone is a display field carried by the
    /// self-service profile DTO, never a search key, so the loss of equality lookups
    /// on the encrypted column is acceptable (mirrors the documented Idnp / Idno
    /// design but without the shadow-column counterpart).
    /// </para>
    /// <para>
    /// Format-validate via <see cref="Cnas.Ps.Core.ValueObjects.PhoneE164.TryCreate"/>
    /// at the service boundary — never accept-and-drop malformed input.
    /// </para>
    /// </remarks>
    public string? PhoneE164 { get; set; }

    /// <summary>Preferred language (ro/en/ru) — UI 003.</summary>
    public string PreferredLanguage { get; set; } = "ro";

    /// <summary>Set of role codes the user has been granted (RBAC, UC18).</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>Set of group codes the user belongs to (groups aggregate roles).</summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>UTC timestamp of the last successful login.</summary>
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>
    /// JSON-serialised <c>NotificationPreferences</c> for this user (R0171, CF 22.02 / CF 04.08).
    /// Persisted as a <c>jsonb</c> column on PostgreSQL and treated as an opaque string by the
    /// EF Core InMemory provider used in tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null = default.</b> A NULL column value means "use the dispatcher default" — every
    /// channel is opted IN. This keeps the migration cheap (no back-fill) and lets a new
    /// user receive notifications until they explicitly opt out via
    /// <c>PUT /api/profile/notification-preferences</c>.
    /// </para>
    /// <para>
    /// <b>Never parse here.</b> The application service is the only layer that touches the
    /// JSON — use <c>Cnas.Ps.Core.ValueObjects.NotificationPreferencesJson.Parse</c> on the
    /// way in and <c>Serialize</c> on the way out. The dispatcher contract is fail-open:
    /// malformed JSON falls back to default-opt-in so notifications are never silently
    /// dropped because the preference schema drifted.
    /// </para>
    /// <para>
    /// <b>No PII.</b> The JSON contains only boolean flags and category code strings; it
    /// does NOT carry national identifiers, addresses, or any other personal data. The
    /// column therefore does NOT participate in the encrypted-at-rest set
    /// (CLAUDE.md §5.7 / TOR SEC 035).
    /// </para>
    /// </remarks>
    public string? NotificationPreferences { get; set; }

    /// <summary>
    /// JSON-serialised <c>UserLayoutPreferences</c> for this user (R0535, CF 04.07-08).
    /// Persisted as a <c>jsonb</c> column on PostgreSQL and treated as an opaque string by
    /// the EF Core InMemory provider used in tests. Stores grid column visibility / order,
    /// page-size defaults, and dashboard widget order keyed per stable grid/widget code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null = default.</b> A NULL column value means "use system defaults" — every grid
    /// renders with its registry-declared columns and the system-wide page size. The
    /// migration is therefore back-fill-free; existing users see the same UI until they
    /// explicitly save a layout via <c>PUT /api/profile/layout-preferences</c>.
    /// </para>
    /// <para>
    /// <b>Never parse here.</b> The application service is the only layer that touches the
    /// JSON — use <c>Cnas.Ps.Core.ValueObjects.UserLayoutPreferencesJson.Parse</c> on the
    /// way in and <c>Serialize</c> on the way out. Malformed JSON falls back to defaults
    /// (fail-open) and increments <c>cnas.user_layout.parse_failure</c> so operators
    /// chart silent drift.
    /// </para>
    /// <para>
    /// <b>No PII.</b> The JSON contains only column / widget identifier strings and
    /// integer page-size values; it does NOT carry national identifiers, addresses, or
    /// any other personal data. The column therefore does NOT participate in the
    /// encrypted-at-rest set (CLAUDE.md §5.7 / TOR SEC 035).
    /// </para>
    /// </remarks>
    public string? LayoutPreferences { get; set; }

    /// <summary>
    /// Lifecycle state of the account per TOR SEC 016 (R0059). Default
    /// <see cref="UserAccountState.Active"/> for newly-created profiles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Authentication gate.</b> Authentication is permitted only when
    /// <see cref="State"/> is <see cref="UserAccountState.Active"/>; every other state
    /// (<see cref="UserAccountState.Suspended"/>, <see cref="UserAccountState.Disabled"/>,
    /// <see cref="UserAccountState.Locked"/>) rejects sign-in. Distinct from
    /// <see cref="AuditableEntity.IsActive"/> (the soft-delete row marker) — a
    /// soft-deleted row may carry any state, but only <c>State == Active</c> permits
    /// authentication.
    /// </para>
    /// <para>
    /// <b>Audit obligation.</b> Every flip of this property MUST produce a corresponding
    /// <c>AuditLog</c> row with event code <c>USER.STATE_CHANGE.&lt;FROM&gt;.&lt;TO&gt;</c>
    /// and severity <see cref="AuditSeverity.Critical"/>. The canonical writer is
    /// <c>IUserAccountStateService.ChangeStateAsync</c> — direct property mutations from
    /// other paths must mirror the audit pattern explicitly. See SEC 016 for the
    /// regulatory rationale and the <see cref="UserAccountState"/> enum XML doc for the
    /// transition matrix.
    /// </para>
    /// </remarks>
    public UserAccountState State { get; set; } = UserAccountState.Active;
}
