namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0196 / TOR CF 23.02 — operator-configurable registry of audit categories
/// for SI PS. Each row binds a stable SCREAMING_SNAKE_CASE
/// <see cref="Code"/> to a display name, optional description, default
/// severity, and IsActive flag. The catalog is seeded at migration time with
/// the 14 categories TOR CF 23.02 enumerates (AUTH, CRUD,
/// APPLICATION.RECEIVE, APPLICATION.EXAMINE, TASK.EXECUTE,
/// DOCUMENT.ISSUE, APPROVAL, SERVICE_CONFIG, SYSTEM_CONFIG, METADATA,
/// ROLE_GROUP, SYNC, REPORT_ACCESS, DB_QUERY).
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural-key uniqueness.</b> <see cref="Code"/> is the stable identifier
/// (e.g. <c>AUTH</c>, <c>CRUD</c>, <c>DB_QUERY</c>); EF enforces a unique
/// constraint. Audit consumers (operators, SIEM dashboards) reference the
/// category by its Code, not by its surrogate id.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — operators
/// reference categories by Sqid through the admin REST surface (
/// <c>/api/admin/audit/categories</c>). The DTOs encode the surrogate id.
/// </para>
/// <para>
/// <b>Soft delete.</b> <see cref="AuditableEntity.IsActive"/> is the soft-
/// delete flag; deactivating a category keeps the row available for backfill
/// and historical lookups while removing it from the operator-facing pick
/// lists.
/// </para>
/// </remarks>
public sealed class AuditCategory : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable SCREAMING_SNAKE_CASE category code (e.g. <c>AUTH</c>,
    /// <c>CRUD</c>, <c>DB_QUERY</c>). Pattern <c>^[A-Z][A-Z0-9_.]{1,63}$</c>
    /// — allows a single trailing namespace segment via <c>.</c> (e.g.
    /// <c>APPLICATION.RECEIVE</c>) to match the TOR CF 23.02 seed list.
    /// Length ≤ 64. Unique within the system.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable display name. Bounded to 256 characters.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// R0027 / TOR ARH 022 — optional Romanian display name. When null the
    /// <c>ILocalizedNameResolver</c> falls back to <see cref="DisplayName"/>;
    /// when populated it overrides <see cref="DisplayName"/> for the <c>"ro"</c>
    /// culture. Capped at 256 chars at the persistence layer.
    /// </summary>
    public string? NameRo { get; set; }

    /// <summary>
    /// R0027 / TOR ARH 022 — optional Russian display name. Capped at 256 chars
    /// at the persistence layer.
    /// </summary>
    public string? NameRu { get; set; }

    /// <summary>
    /// R0027 / TOR ARH 022 — optional English display name. Capped at 256 chars
    /// at the persistence layer.
    /// </summary>
    public string? NameEn { get; set; }

    /// <summary>Optional free-form description. Bounded to 1000 characters.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Suggested default severity for audit rows that don't pass an explicit
    /// override. Persisted as the enum's stable name string (per the
    /// existing severity-mapping convention used by SupportTicketCategory).
    /// </summary>
    public AuditSeverity DefaultSeverity { get; set; } = AuditSeverity.Information;
}
