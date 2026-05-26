namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0301 / ARH 028 / TOR Annex 1 — change-traceable CAEM (NACE-equivalent) activity
/// classification attached to a <see cref="Contributor"/> (Plătitor). Unlike address
/// and contact, a Payer may carry multiple CONCURRENT activity rows — one primary +
/// any number of secondary activities — so the "one current row per parent" invariant
/// is NOT enforced here. Instead, the filtered unique index is configured on
/// <c>(PayerId, CaemCode) WHERE ValidToUtc IS NULL</c> in
/// <c>PayerActivityCAEMConfiguration</c> so the same code can appear historically
/// but only once concurrently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format.</b> <see cref="CaemCode"/> follows the Moldovan CAEM Rev. 2 syntax
/// <c>X.YY.ZZ</c> (e.g. <c>M.69.10</c> — legal activities). The validator on the input
/// DTO enforces <c>^[A-Z]\.\d{2}\.\d{2}$</c>.
/// </para>
/// <para>
/// <b>Lifecycle.</b> <c>AddActivityCaemAsync</c> inserts a fresh row with
/// <c>ValidFromUtc = now</c>; <c>EndActivityCaemAsync</c> closes a specific row by
/// stamping <c>ValidToUtc = now</c>. There is no "update in place" operation — the
/// supersession pattern (close + insert) is used when an existing activity's metadata
/// (e.g. description) changes.
/// </para>
/// </remarks>
public sealed class PayerActivityCAEM : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="Contributor"/> (Plătitor) row.</summary>
    public long PayerId { get; set; }

    /// <summary>
    /// CAEM Rev. 2 hierarchical code in the canonical <c>X.YY.ZZ</c> form
    /// (e.g. <c>M.69.10</c>). Validated by <c>PayerActivityCaemInputDtoValidator</c>.
    /// </summary>
    public required string CaemCode { get; set; }

    /// <summary>Human-readable description of the activity, captured for display convenience.</summary>
    public required string CaemDescription { get; set; }

    /// <summary>True when this is the Payer's primary activity. At most one primary at a time.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>UTC instant at which this activity row became active.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which this activity was ended. <c>null</c> means still current.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Free-text rationale for adding or ending the activity. Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Sqid string of the operator who recorded the change.</summary>
    public string? RecordedByUserSqid { get; set; }
}
