namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0311 / ARH 028 / TOR Annex 2.3 — pre-1999 manual labour-record book (Carnet de muncă)
/// period for an <see cref="InsuredPerson"/>. These entries are read-only HISTORICAL
/// SEEDS — the manual Carnet de muncă was the official labour-record artefact in Moldova
/// before CNAS's electronic system came online in 1999. Each row digitises one period
/// from a citizen's paper booklet.
/// </summary>
/// <remarks>
/// <para>
/// The same supersession columns (<see cref="ValidFromUtc"/>/<see cref="ValidToUtc"/>)
/// are present for API consistency; in practice these rows are inserted once at
/// digitisation time and never re-superseded. The application service treats them
/// effectively as immutable.
/// </para>
/// </remarks>
public sealed class ContributorPre1999PeriodCarnetMunca : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="InsuredPerson"/> row.</summary>
    public long ContributorId { get; set; }

    /// <summary>Carnet de muncă booklet number (typed verbatim from the paper original).</summary>
    public required string CarnetMuncaNumber { get; set; }

    /// <summary>Period start date (inclusive).</summary>
    public DateOnly PeriodStartDate { get; set; }

    /// <summary>Period end date (inclusive).</summary>
    public DateOnly PeriodEndDate { get; set; }

    /// <summary>Employer name as recorded in the booklet.</summary>
    public string? EmployerName { get; set; }

    /// <summary>Position / job title as recorded in the booklet.</summary>
    public string? Position { get; set; }

    /// <summary>UTC instant at which this row was digitised (a.k.a. became active).</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which this row was superseded — practically always null.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Free-text rationale (e.g. typo correction). Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Sqid string of the operator who digitised the entry.</summary>
    public string? RecordedByUserSqid { get; set; }
}
