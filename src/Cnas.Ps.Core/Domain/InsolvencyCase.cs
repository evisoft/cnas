using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — one row per insolvency lifecycle case
/// against a <see cref="Contributor"/>. Splits the historical
/// <c>Contributor.IsInsolvent</c> flag (one bit, no history) into a dedicated
/// registry so multiple insolvency events on the same payer are independently
/// tracked, with their own claims (<see cref="InsolvencyClaim"/>) and payments
/// (<see cref="InsolvencyPayment"/>) sub-tables per Annex 1 §8.1.4.5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> A case lands in <see cref="InsolvencyCaseStatus.Open"/> when
/// the lifecycle service opens it (the contributor is concurrently flagged
/// <c>IsInsolvent=true</c>); it transitions to
/// <see cref="InsolvencyCaseStatus.Resolved"/> when the operator records the
/// resolution (and the contributor's <c>IsInsolvent</c> flag is flipped back
/// to false). Both transitions are audited at <c>Critical</c> severity.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the row's
/// id is surfaced to API callers via the Sqid-encoded
/// <c>InsolvencyCaseDto.Id</c> per CLAUDE.md RULE 3. The raw <c>Id</c>
/// never leaves the system boundary.
/// </para>
/// <para>
/// <b>Soft-delete + audit fields.</b> Inherits the standard <c>CreatedAtUtc</c>
/// / <c>UpdatedAtUtc</c> / <c>IsActive</c> columns from
/// <see cref="AuditableEntity"/>. Resolution stamps <see cref="ResolvedAtUtc"/>
/// but the row is never hard-deleted — Annex 1 §8.1.4.5 requires forensic
/// retention of every prior insolvency event for the citizen-facing dashboard.
/// </para>
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Notice, EventCodePrefix = "INSOLVENCY")]
public sealed class InsolvencyCase : AuditableEntity, IExternalId
{
    /// <summary>Foreign-key reference to the owning <see cref="Contributor"/>.</summary>
    public long ContributorId { get; set; }

    /// <summary>
    /// Calendar date the contributor was declared insolvent — typically the
    /// effective date from the court ruling or administrative finding. Must
    /// not lie in the future at open time (validator enforces).
    /// </summary>
    public DateOnly InsolvencyDate { get; set; }

    /// <summary>
    /// Operator-supplied rationale captured at open time (e.g. "Hotărâre
    /// judecătorească nr. 1234/2026"). 3..500 chars per validator.
    /// </summary>
    public required string Reason { get; set; }

    /// <summary>
    /// Lifecycle status — defaults to <see cref="InsolvencyCaseStatus.Open"/>
    /// at open. Resolution flips the column to
    /// <see cref="InsolvencyCaseStatus.Resolved"/>.
    /// </summary>
    public InsolvencyCaseStatus Status { get; set; } = InsolvencyCaseStatus.Open;

    /// <summary>UTC instant the case was opened (mirrors the row's <c>CreatedAtUtc</c>).</summary>
    public DateTime OpenedAtUtc { get; set; }

    /// <summary>UTC instant the case was resolved, or <c>null</c> while the case stays open.</summary>
    public DateTime? ResolvedAtUtc { get; set; }

    /// <summary>
    /// Operator-supplied rationale captured at resolution time
    /// (e.g. "Plătit integral pe 2026-12-01"). 3..500 chars per validator;
    /// null while the case is still <see cref="InsolvencyCaseStatus.Open"/>.
    /// </summary>
    public string? Resolution { get; set; }
}

/// <summary>
/// Lifecycle states for <see cref="InsolvencyCase"/> per R0830 / R0834 / TOR
/// Annex 1 §8.1.4.5. Stored as <c>int</c> so an unknown future enum value can
/// be introduced without breaking the EF mapping.
/// </summary>
public enum InsolvencyCaseStatus
{
    /// <summary>The contributor is currently flagged insolvent under this case.</summary>
    Open = 0,

    /// <summary>The insolvency was resolved — the contributor was restored to solvent status.</summary>
    Resolved = 1,
}

/// <summary>
/// R0834 / TOR Annex 1 §8.1.4.5 — one claim (creanță) lodged against a
/// <see cref="InsolvencyCase"/>. Distinct from <see cref="Claim"/> which models
/// CNAS-issued obligations on the payer side; this aggregate tracks third-party
/// claims that arose during the insolvency proceeding (creditors filing against
/// the insolvent estate).
/// </summary>
/// <remarks>
/// External id is the Sqid form of <see cref="AuditableEntity.Id"/>; the entity
/// is not exposed as a separate IExternalId mapping today (the parent case is
/// the addressable aggregate root for the citizen-facing surface).
/// </remarks>
public sealed class InsolvencyClaim : AuditableEntity, IExternalId
{
    /// <summary>FK to the owning <see cref="InsolvencyCase"/>.</summary>
    public long InsolvencyCaseId { get; set; }

    /// <summary>
    /// Claim amount (in <see cref="Currency"/> units). Must be strictly
    /// positive — overpayment / refund corrections go through the payments
    /// sub-table instead.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// ISO-4217 currency code (e.g. <c>"MDL"</c>, <c>"EUR"</c>). 3 chars,
    /// uppercase, mirrors the EF configuration cap.
    /// </summary>
    public required string Currency { get; set; }

    /// <summary>
    /// Free-form claim description captured at registration (e.g.
    /// "Unpaid contributions Mar-2024"). 3..1000 chars per validator.
    /// </summary>
    public required string Description { get; set; }

    /// <summary>Calendar date the underlying obligation was incurred.</summary>
    public DateOnly IncurredOn { get; set; }
}

/// <summary>
/// R0834 / TOR Annex 1 §8.1.4.5 — one payment received against a
/// <see cref="InsolvencyCase"/> from the insolvent estate's liquidator or via
/// court-ordered distribution.
/// </summary>
public sealed class InsolvencyPayment : AuditableEntity, IExternalId
{
    /// <summary>FK to the owning <see cref="InsolvencyCase"/>.</summary>
    public long InsolvencyCaseId { get; set; }

    /// <summary>
    /// Payment amount (MDL). Strictly positive — corrective writebacks are
    /// modelled as separate downstream adjustments rather than negative rows
    /// so the audit trail stays append-only.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>Calendar date the payment was received.</summary>
    public DateOnly PaymentDate { get; set; }

    /// <summary>
    /// Optional external payment reference (e.g. court-distribution order
    /// number, bank-transfer reference). 0..64 chars per validator.
    /// </summary>
    public string? Reference { get; set; }
}
