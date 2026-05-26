namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0311 / ARH 028 / TOR Annex 2.3 — change-traceable civil-status record attached to an
/// <see cref="InsuredPerson"/> (Persoană asigurată). Supersession-only updates; one current
/// row per Contributor enforced by the filtered unique index in
/// <c>ContributorCivilStatusConfiguration</c>.
/// </summary>
public sealed class ContributorCivilStatus : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="InsuredPerson"/> row.</summary>
    public long ContributorId { get; set; }

    /// <summary>Current civil-status classification.</summary>
    public CivilStatusType Status { get; set; }

    /// <summary>
    /// Date the civil-status change was recorded in the underlying civil register
    /// (e.g. marriage certificate date). Distinct from <see cref="ValidFromUtc"/>
    /// which records when CNAS learned about the change.
    /// </summary>
    public DateOnly? EffectiveDate { get; set; }

    /// <summary>UTC instant at which this row became active in CNAS records.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which this row was superseded. Null when current.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Free-text rationale for the change. Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Sqid string of the operator who recorded the change.</summary>
    public string? RecordedByUserSqid { get; set; }
}
