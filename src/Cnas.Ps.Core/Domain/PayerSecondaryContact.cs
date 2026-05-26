namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0803 / ARH 028 / TOR BP 1.1-D — change-traceable additional contact person attached
/// to a <see cref="Contributor"/> (Plătitor). Distinct from the primary
/// <see cref="PayerContact"/> row (R0301): a Payer may carry multiple concurrent
/// secondary contacts (e.g. one Accountant, one Legal counsel) — there is no
/// <c>IsPrimary</c> flag on this entity and no filtered-unique-current-row
/// constraint. Each row is closed (<see cref="ValidToUtc"/>=now) via the service's
/// close-row method; new contacts are simply appended.
/// </summary>
public sealed class PayerSecondaryContact : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="Contributor"/> (Plătitor) row.</summary>
    public long PayerId { get; set; }

    /// <summary>Free-text contact-person name (1..200 chars).</summary>
    public required string ContactPersonName { get; set; }

    /// <summary>
    /// Optional role descriptor (e.g. <c>"Accountant"</c>, <c>"Legal"</c>,
    /// <c>"Authorised Representative"</c>). Max 100 chars.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>Optional phone in E.164 form.</summary>
    public string? PhoneE164 { get; set; }

    /// <summary>Optional email address.</summary>
    public string? Email { get; set; }

    /// <summary>UTC instant at which this row became active.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which this row was closed. Null when still current.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Free-text rationale for the change. Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Sqid string of the operator who recorded the change.</summary>
    public string? RecordedByUserSqid { get; set; }
}
