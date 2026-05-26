namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0301 / ARH 028 / TOR Annex 1 — append-only audit-style history log for
/// <see cref="Contributor"/> (Plătitor) field changes that are NOT captured by the
/// dedicated child tables (<see cref="PayerAddress"/>, <see cref="PayerContact"/>,
/// <see cref="PayerActivityCAEM"/>). One row per changed field, recorded by the
/// service layer at the same instant the parent mutation lands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only.</b> Unlike the supersession-based child tables, this entity has no
/// <c>ValidFromUtc</c>/<c>ValidToUtc</c> — each row is a permanent record of a single
/// field-level change. Rows are NEVER updated, only inserted; the
/// <c>(PayerId, ChangedAtUtc DESC)</c> index gives investigators a chronologically
/// ordered list per Payer.
/// </para>
/// <para>
/// <b>Distinct from <c>AuditLog</c>.</b> The system-wide <c>AuditLog</c> records
/// service-call boundaries with redacted detail JSON; <see cref="PayerHistory"/> is a
/// per-Payer structured journal whose primary consumer is the operator detail screen
/// (so they can see "this is what was changed and when" without sifting through the
/// audit log). Both must be written by the service layer — they are not redundant
/// because the audit log carries actor/severity/correlation info while this table
/// carries field-level before/after values.
/// </para>
/// </remarks>
public sealed class PayerHistory : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="Contributor"/> (Plătitor) row.</summary>
    public long PayerId { get; set; }

    /// <summary>Name of the parent field that changed (e.g. <c>Denumire</c>, <c>CfojCode</c>).</summary>
    public required string FieldName { get; set; }

    /// <summary>Stringified previous value, or <c>null</c> if the field was previously unset.</summary>
    public string? OldValue { get; set; }

    /// <summary>Stringified new value, or <c>null</c> if the field is now unset.</summary>
    public string? NewValue { get; set; }

    /// <summary>Free-text rationale for the change. Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>UTC instant at which the change was recorded.</summary>
    public DateTime ChangedAtUtc { get; set; }

    /// <summary>Sqid string of the operator who recorded the change.</summary>
    public string? RecordedByUserSqid { get; set; }
}
