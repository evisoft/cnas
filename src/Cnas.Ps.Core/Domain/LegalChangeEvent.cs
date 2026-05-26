using System.Collections.Generic;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1503 / TOR §3.7-D — a legal-framework change captured by an operator that
/// will trigger a mass-recalculation of every active benefit decision in
/// scope. Each row carries the change-set as opaque JSON; the per-benefit-
/// kind <c>IBenefitRecalculationStrategy</c> is the only consumer that parses
/// the payload — the engine is intentionally pipeline-pluggable so the row
/// shape never has to grow with new benefit kinds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> An operator authors the event in
/// <see cref="LegalChangeEventStatus.Draft"/>, flips it to
/// <see cref="LegalChangeEventStatus.Ready"/>, the engine drives the row
/// through <see cref="LegalChangeEventStatus.Recalculating"/> →
/// <see cref="LegalChangeEventStatus.ReviewPending"/> →
/// <see cref="LegalChangeEventStatus.Applied"/>; any non-Applied state may
/// be flipped to <see cref="LegalChangeEventStatus.Cancelled"/>.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.LegalChangeEventDto.Id</c>) carries a
/// Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>No PII.</b> The event row stores the change set only — beneficiary
/// identities never appear here.
/// </para>
/// </remarks>
public sealed class LegalChangeEvent : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable external code identifying this legal-change event. Pattern
    /// <c>^[A-Z][A-Z0-9_.]{1,63}$</c>; unique across the registry. Auto-
    /// generated as <c>LCE-{year}-{seq:000000}</c> when the caller does not
    /// supply one.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Short operator-facing title (≤ 256 chars).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional longer rationale (≤ 2000 chars).</summary>
    public string? Description { get; set; }

    /// <summary>First month for which the new legal rule applies (UTC).</summary>
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>Coarse scope (drives the snapshot of <see cref="BenefitTypesInScope"/>).</summary>
    public LegalChangeScope Scope { get; set; }

    /// <summary>
    /// Snapshot of the explicit benefit-type enum-name strings in scope at
    /// insert time. Populated by the engine on insert: when <see cref="Scope"/>
    /// is <see cref="LegalChangeScope.All"/> the orchestrator stamps every
    /// known <see cref="BenefitType"/> enum-name. Persisted as a PG
    /// <c>text[]</c> via the EF configuration.
    /// </summary>
    public List<string> BenefitTypesInScope { get; set; } = new();

    /// <summary>
    /// Opaque JSON describing the change (e.g.
    /// <c>{"minimumPensionMdl": 3200.00, "previousMinimumMdl": 3000.00}</c>).
    /// The engine treats this as opaque; the registered strategy parses it.
    /// </summary>
    public string? ChangePayloadJson { get; set; }

    /// <summary>Lifecycle state — defaults to <see cref="LegalChangeEventStatus.Draft"/>.</summary>
    public LegalChangeEventStatus Status { get; set; } = LegalChangeEventStatus.Draft;

    /// <summary>Internal id of the <see cref="UserProfile"/> that registered the event.</summary>
    public int RegisteredByUserId { get; set; }

    /// <summary>
    /// Operator-supplied rationale populated when <see cref="Status"/> is
    /// flipped to <see cref="LegalChangeEventStatus.Cancelled"/>. Required
    /// length 3..500 chars when present.
    /// </summary>
    public string? CancellationReason { get; set; }
}
