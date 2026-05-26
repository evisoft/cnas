namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — a power-user-authored ad-hoc report definition.
/// Captures the registry to query, the JSON-encoded selected-fields list, the JSON-encoded
/// QBE filter (R0163), the JSON-encoded ordering specifications, an optional single
/// group-by field, the owning user, and a sharing flag. The matching
/// <c>IReportEngine</c> executes the template to produce paged result rows and exports
/// the rows via the R0226 universal grid-export pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership and sharing.</b> The <see cref="OwnerUserId"/> is the internal primary
/// key of the <c>UserProfile</c> that created the row. Ownership is unilateral — only
/// the owner (or an administrator) may update, soft-delete, or change the
/// <see cref="IsShared"/> flag. Non-owners with the <c>Reports.View</c> permission
/// receive READ access to rows that the owner has explicitly published
/// (<see cref="IsShared"/> = <c>true</c>); they cannot mutate shared rows in any way.
/// </para>
/// <para>
/// <b>Payload opacity.</b> <see cref="SelectedFieldsJson"/>, <see cref="FilterJson"/>,
/// and <see cref="OrderingJson"/> are stored as opaque JSON blobs. The persistence
/// layer never inspects them — schema-level validation runs at the service-layer
/// (against the R0163 QBE registry schema) so a schema evolution that adds a new
/// queryable field does not require a migration.
/// </para>
/// <para>
/// <b>Code uniqueness.</b> The <see cref="Code"/> is a kebab-case stable identifier
/// (e.g. <c>report.solicitants.active-by-region</c>) that uniquely identifies the
/// template across the system. Enforced via a unique index in
/// <c>ReportTemplateConfiguration</c>. Code collisions surface as
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.Conflict"/> at the service layer rather
/// than as a DB-side unique-constraint violation.
/// </para>
/// <para>
/// <b>Group-by contract.</b> When <see cref="GroupByField"/> is non-null the engine
/// materialises one row per distinct value of that field, projecting only the group key
/// + a <c>count</c> aggregate. Richer aggregates (sum/avg/min/max) are deferred to a
/// later batch — see TODO.md.
/// </para>
/// <para>
/// <b>Soft-delete contract.</b> Inherits <see cref="AuditableEntity.IsActive"/> from
/// <see cref="AuditableEntity"/>. Deletes flip <see cref="AuditableEntity.IsActive"/>
/// to <c>false</c>; the row remains queryable for audit forensics but no longer
/// surfaces through <c>ListAccessibleAsync</c> / <c>GetAsync</c> and cannot be
/// executed.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> The numeric <see cref="AuditableEntity.Id"/> never leaves the
/// system. <see cref="IExternalId"/> is applied because the <c>ReportTemplateDto</c>
/// output DTO surfaces the row's Sqid as the public identifier — CLAUDE.md RULE 3 /
/// ARH 027.
/// </para>
/// </remarks>
public sealed class ReportTemplate : AuditableEntity, IExternalId
{
    /// <summary>
    /// Kebab-case stable identifier (e.g. <c>report.solicitants.active-by-region</c>).
    /// Service-layer validator enforces the pattern
    /// <c>^[a-z][a-z0-9.-]{2,127}$</c>. Treated as case-sensitive ordinal — collisions
    /// surface as <see cref="Cnas.Ps.Core.Common.ErrorCodes.Conflict"/>.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>
    /// User-supplied display label rendered in the report-picker drop-down. Capped at
    /// 128 chars by the EF mapping; the service validator enforces the same cap before
    /// persisting so over-long names surface as
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.ValidationFailed"/>.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Optional human-readable description of what the report shows. Capped at 512
    /// chars by the EF mapping.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Stable registry code (e.g. <c>Solicitant</c>, <c>Cerere</c>) — must be one of
    /// the canonical <c>QueryBudgetRegistries</c> constants. The engine looks the
    /// schema up via <c>IQbeRegistrySchemaProvider</c> at execution time; changing
    /// the registry of an existing template would invalidate every prior
    /// selected-field reference and is therefore disallowed by the service layer.
    /// </summary>
    public required string Registry { get; set; }

    /// <summary>
    /// JSON array of field names selected for projection
    /// (e.g. <c>["Id","DisplayName","CreatedAtUtc"]</c>). The validator enforces ≥1
    /// and ≤25 entries; each name must appear in the registry's QBE schema.
    /// </summary>
    public required string SelectedFieldsJson { get; set; }

    /// <summary>
    /// JSON-encoded <c>QbeFilter</c> applied before the budget guard runs. Empty
    /// envelope ( <c>{"Combinator":"AND","Conditions":[]}</c> ) matches every row.
    /// </summary>
    public required string FilterJson { get; set; }

    /// <summary>
    /// JSON array of <c>{ field, direction }</c> records describing the multi-column
    /// ordering applied to the result rows. The validator enforces ≤5 entries. Each
    /// referenced field must appear in the registry schema. Direction is one of
    /// <c>"ASC"</c> / <c>"DESC"</c> (case-insensitive).
    /// </summary>
    public required string OrderingJson { get; set; }

    /// <summary>
    /// Optional single field name used to group the result rows. When non-null the
    /// engine emits one row per distinct value with a <c>count</c> aggregate column.
    /// The validator enforces that this field also appears in
    /// <see cref="SelectedFieldsJson"/>.
    /// </summary>
    public string? GroupByField { get; set; }

    /// <summary>
    /// Internal <c>UserProfile.Id</c> of the row's owner — the only actor permitted
    /// to mutate the row (apart from administrators). Captured at create time and
    /// never reassigned.
    /// </summary>
    public long OwnerUserId { get; set; }

    /// <summary>
    /// When <c>true</c>, every authenticated CNAS staff member with the
    /// <c>Reports.View</c> permission may execute and export this template. The
    /// owner remains the sole mutator. Default is <c>false</c> — saving is private
    /// until the owner explicitly publishes.
    /// </summary>
    public bool IsShared { get; set; }
}
