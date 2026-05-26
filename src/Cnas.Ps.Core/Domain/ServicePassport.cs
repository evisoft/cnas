namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Pașaport serviciu — definition of an electronic service exposed to citizens / employees. TOR §2.3 #4, UC15.
/// </summary>
/// <remarks>
/// <para>
/// The service passport drives the runtime: which form to render, what workflow to run,
/// which SLA applies, which roles are permitted. Per FLEX 008 the system is configurable
/// without recompilation — service additions/changes happen via this entity.
/// </para>
/// <para>
/// R0142 / CF 15.04 — <b>append-only versioning.</b> Editing the dossier-acceptance shape
/// of a passport NEVER mutates the existing row; instead a new row at
/// <see cref="Version"/> = N+1 is inserted with <see cref="IsCurrent"/> = <c>true</c>, and
/// the prior current row is flipped to <see cref="IsCurrent"/> = <c>false</c>. Together with
/// <see cref="ServiceApplication.PinnedServicePassportVersion"/> this keeps in-flight
/// applications bound to the passport revision they were submitted under (no mid-flight
/// drift). See <c>Cnas.Ps.Application.UseCases.IServicePassportVersioningService</c>
/// for the workflow.
/// </para>
/// </remarks>
public sealed class ServicePassport : AuditableEntity, IExternalId, IExternalCodeOwner
{
    /// <summary>
    /// Stable code (e.g., <c>SP-001-BIRTH</c>) used by external systems and links.
    /// Carrying <see cref="IExternalCodeOwner"/> documents that this string is part of
    /// the public contract — renaming an existing passport's code is a breaking change.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>
    /// R0142 / CF 15.04 — monotonically-increasing version number for the passport
    /// identified by <see cref="Code"/>. The first persist for a code inserts <c>1</c>;
    /// each subsequent semantically-meaningful change inserts the previous maximum + 1.
    /// Together with <see cref="Code"/> this forms the natural key (enforced by the
    /// EF configuration's unique index).
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// R0142 / CF 15.04 — <c>true</c> for exactly one row per <see cref="Code"/> at any
    /// instant. Historical revisions remain in the table with <c>IsCurrent = false</c>.
    /// Distinct from the inherited <see cref="AuditableEntity.IsActive"/> soft-delete
    /// flag — a passport row may be both <c>IsActive = true</c> and <c>IsCurrent = false</c>
    /// (still part of an in-flight application's pinned snapshot, but no longer the
    /// catalogue entry exposed to new submissions).
    /// </summary>
    public bool IsCurrent { get; set; } = true;

    /// <summary>
    /// R0142 / CF 15.04 — FK to the row that superseded this revision. <c>null</c> on
    /// the current row and on rows that have never been superseded; populated on
    /// history rows.
    /// </summary>
    public long? SupersededByPassportId { get; set; }

    /// <summary>
    /// R0142 / CF 15.04 — UTC timestamp at which this row stopped being the current
    /// revision. <c>null</c> on the active row; populated on the superseded row in the
    /// same transaction that flips <see cref="IsCurrent"/> to <c>false</c>.
    /// </summary>
    public DateTime? SupersededAtUtc { get; set; }

    /// <summary>
    /// R0142 / CF 15.04 — FK to the previous-version row in the chain. <c>null</c> on
    /// the very first version (<see cref="Version"/> = 1) and populated on every
    /// subsequent row.
    /// </summary>
    public long? SupersedesPassportId { get; set; }

    /// <summary>
    /// R0502 / TOR CF 01.05 — optional thematic category code used by the public
    /// services-catalog endpoint to narrow the catalogue by domain (e.g.
    /// <c>"PENSIONS"</c>, <c>"FAMILY"</c>, <c>"DISABILITY"</c>). Stable upper-snake-case
    /// identifier so the UI binds localised display labels to it; null when the passport
    /// has not been categorised yet (legacy seed rows). Indexed on the persistence side
    /// because the public catalogue exposes it as a filter dimension.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>Display name in Romanian (default UI language).</summary>
    public required string NameRo { get; set; }

    /// <summary>Display name in English.</summary>
    public string? NameEn { get; set; }

    /// <summary>Display name in Russian.</summary>
    public string? NameRu { get; set; }

    /// <summary>Multi-line description in Romanian.</summary>
    public required string DescriptionRo { get; set; }

    /// <summary>JSON-schema describing the form fields.</summary>
    public string FormSchemaJson { get; set; } = "{}";

    /// <summary>Reference to the workflow definition that runs when an application is submitted.</summary>
    public required string WorkflowCode { get; set; }

    /// <summary>SLA — maximum days the dossier should remain open.</summary>
    public int MaxProcessingDays { get; set; } = 30;

    /// <summary>True when this service is enabled for production submissions.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>True when the service can be initiated proactively by an event (UC14).</summary>
    public bool IsProactive { get; set; }

    /// <summary>
    /// Declarative rule-set (JSON) consumed by <c>IDecisionEngine</c> to compute
    /// eligibility + benefit amount for this service. Defaults to the empty object
    /// <c>"{}"</c> so an unconfigured passport never throws — the engine returns
    /// <c>BAD_RULE</c> when it cannot find required sections.
    /// </summary>
    public string DecisionRulesJson { get; set; } = "{}";

    /// <summary>
    /// R0143 / CF 17.19 — Optional JSON array of mandatory-attachment descriptors used
    /// by the service-passport configuration matrix endpoint. Shape:
    /// <c>[ { "documentTypeCode": "ID_CARD", "cardinalityMin": 1, "cardinalityMax": 1 }, ... ]</c>.
    /// Nullable so legacy passports continue to work unchanged.
    /// </summary>
    public string? MandatoryAttachmentsJson { get; set; }

    /// <summary>
    /// R0143 / CF 17.19 — Optional JSON array of named calc-formula expressions consumed
    /// by the per-passport calculation pipeline. Shape:
    /// <c>[ { "code": "monthlyBenefit", "formula": "base + bonus * 0.1" }, ... ]</c>.
    /// Each formula is interpreted by
    /// <c>Cnas.Ps.Application.Calculations.IExpressionEvaluator</c> (Shunting-yard).
    /// Nullable so legacy passports continue to work unchanged.
    /// </summary>
    public string? CalcFormulasJson { get; set; }
}
