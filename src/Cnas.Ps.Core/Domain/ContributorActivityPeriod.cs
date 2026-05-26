namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0311 / ARH 028 / TOR Annex 2.3 — change-traceable employment / activity period
/// attached to an <see cref="InsuredPerson"/> (Persoană asigurată). One row per job —
/// when a person changes employers a new row is inserted; the previous row may already
/// have <see cref="ValidToUtc"/> populated (job ended) or may still be open (concurrent
/// jobs are permitted by deliberately NOT enforcing a single-current-row invariant
/// here — many Moldovan citizens hold two simultaneous part-time positions).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why no filtered unique index.</b> Unlike <see cref="ContributorAddress"/> /
/// <see cref="ContributorContact"/> (a person can only live at one address at a time),
/// it is legal and common to be employed by two employers simultaneously. The migration
/// notes document this deliberate departure from the pattern.
/// </para>
/// </remarks>
public sealed class ContributorActivityPeriod : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="InsuredPerson"/> row.</summary>
    public long ContributorId { get; set; }

    /// <summary>
    /// Stable string reference to the employer — typically the IDNO of a
    /// <see cref="Contributor"/> (Plătitor), but no FK enforcement so historical
    /// rows survive when the underlying employer is purged.
    /// </summary>
    public required string EmployerCode { get; set; }

    /// <summary>Job title at the employer. Max 200 chars.</summary>
    public required string Position { get; set; }

    /// <summary>Monthly salary as recorded at the start of the period (MDL).</summary>
    public decimal? MonthlySalary { get; set; }

    /// <summary>UTC instant at which this employment period became active.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which the employment period ended. Null when still active.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Free-text rationale for ending the period (e.g. resignation, termination). Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Sqid string of the operator who recorded the change.</summary>
    public string? RecordedByUserSqid { get; set; }
}
