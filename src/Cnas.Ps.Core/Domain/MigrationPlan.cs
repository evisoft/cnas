namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2430 / TOR M4 — declarative description of what to migrate from the
/// legacy social-protection system into the new one. Each plan binds a
/// source-kind, a target aggregate name, an opaque JSON mapping descriptor,
/// and a batch-size knob. The lifecycle is
/// <see cref="MigrationPlanStatus.Draft"/> → <see cref="MigrationPlanStatus.Approved"/>
/// → <see cref="MigrationPlanStatus.Active"/>
/// (optionally pausing through <see cref="MigrationPlanStatus.Suspended"/>)
/// → <see cref="MigrationPlanStatus.Archived"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural-key uniqueness.</b> <see cref="PlanCode"/> is the stable
/// SCREAMING_SNAKE_CASE identifier of the plan; the EF configuration enforces
/// a unique constraint so operators cannot register two plans for the same
/// migration cohort.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because operators
/// reference a plan by Sqid through the admin surface.
/// </para>
/// <para>
/// <b>No PII.</b> The plan row carries only configuration metadata —
/// nothing in the row is sensitive. The plan's source data may carry PII at
/// streaming time, but the per-source rows are NEVER persisted onto the
/// plan aggregate.
/// </para>
/// </remarks>
public sealed class MigrationPlan : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable SCREAMING_SNAKE_CASE plan code, e.g. <c>LEGACY_PENSIONS_2026</c>.
    /// Pattern <c>^[A-Z][A-Z0-9_.]{1,63}$</c>, length ≤ 64. Unique within the system.
    /// </summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>Human-readable plan title. Bounded to 256 characters.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional free-form description. Bounded to 2000 characters.</summary>
    public string? Description { get; set; }

    /// <summary>Origin of the source data the plan consumes.</summary>
    public MigrationSourceKind SourceKind { get; set; }

    /// <summary>
    /// Symbolic name of the destination aggregate, e.g. <c>Pension</c>,
    /// <c>Contributor</c>, <c>BenefitDecision</c>. Bounded to 128 characters.
    /// The mapper registry picks the concrete <c>IMigrationRecordMapper</c> by
    /// this value.
    /// </summary>
    public string TargetEntityName { get; set; } = string.Empty;

    /// <summary>
    /// Opaque JSON describing column → field mapping + transforms.
    /// Bounded to 16384 characters. Validated upstream as well-formed JSON;
    /// schema is mapper-specific.
    /// </summary>
    public string? MappingDescriptorJson { get; set; }

    /// <summary>Rows-per-batch knob — default 1000, valid range 10..10000.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Current lifecycle status; defaults to <see cref="MigrationPlanStatus.Draft"/>.</summary>
    public MigrationPlanStatus Status { get; set; } = MigrationPlanStatus.Draft;

    /// <summary>Internal id of the operator who registered the plan.</summary>
    public long RegisteredByUserId { get; set; }

    /// <summary>Internal id of the operator who approved the plan, or null while still in Draft.</summary>
    public long? ApprovedByUserId { get; set; }

    /// <summary>UTC instant the plan was approved; null while still in Draft.</summary>
    public DateTime? ApprovedAt { get; set; }
}
