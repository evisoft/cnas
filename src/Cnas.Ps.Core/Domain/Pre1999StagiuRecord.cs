namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0922 / TOR Annex 2 §8.2.4 — pre-01.01.1999 <em>stagiu</em> (insurance period)
/// roll-up attached directly to an <see cref="InsuredPerson"/>. One row per
/// citizen-declared, evidence-backed period whose contribution-bearing tally
/// (Years / Months / Days) feeds the pension-calculation pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinction from <see cref="InsuredPersonPre1999Period"/>.</b> The
/// <see cref="InsuredPersonPre1999Period"/> aggregate stores the raw
/// employment timeline (start/end dates per booklet entry) and attaches to
/// the natural-person <see cref="Solicitant"/> (so paper intake can run
/// before an <see cref="InsuredPerson"/> row exists). This entity, by
/// contrast, stores the post-validation Years/Months/Days roll-up that the
/// pension calculator consumes, and attaches directly to the
/// <see cref="InsuredPerson"/> aggregate that owns the personal account.
/// Both tables can coexist — they answer different questions:
/// </para>
/// <list type="bullet">
///   <item><see cref="InsuredPersonPre1999Period"/>: "what did the booklet say?".</item>
///   <item><see cref="Pre1999StagiuRecord"/>: "what counts for the pension?".</item>
/// </list>
/// <para>
/// <b>Pre-1999 invariant.</b> The application-layer validator enforces
/// <c>FromDate &lt; 1999-01-01</c> and <c>ToDate &lt; 1999-01-01</c>. Periods
/// crossing the 1999 boundary must be split at the boundary by the operator
/// before being appended here.
/// </para>
/// <para>
/// <b>Range bounds.</b> The validator additionally enforces
/// <c>Years ∈ [0,70]</c>, <c>Months ∈ [0,11]</c>, <c>Days ∈ [0,30]</c> to
/// catch obvious data-entry errors. The boundaries match the Annex 2 §8.2.4
/// note: a single pre-1999 period must fit within one human working life
/// (≤ 70 years) and the calendar tally is normalized so months ≤ 11 and
/// days ≤ 30.
/// </para>
/// </remarks>
public sealed class Pre1999StagiuRecord : AuditableEntity, IExternalId
{
    /// <summary>FK to the owning <see cref="InsuredPerson"/> aggregate.</summary>
    public long InsuredPersonId { get; set; }

    /// <summary>Inclusive start date of the contribution-bearing period.</summary>
    public DateOnly FromDate { get; set; }

    /// <summary>Inclusive end date of the contribution-bearing period.</summary>
    public DateOnly ToDate { get; set; }

    /// <summary>
    /// Whole-year component of the period's tally. Validator-enforced range
    /// <c>[0, 70]</c>.
    /// </summary>
    public int Years { get; set; }

    /// <summary>
    /// Whole-month component of the period's tally. Validator-enforced range
    /// <c>[0, 11]</c> — Months ≥ 12 must be normalised into Years before
    /// persisting.
    /// </summary>
    public int Months { get; set; }

    /// <summary>
    /// Whole-day component of the period's tally. Validator-enforced range
    /// <c>[0, 30]</c> — Days ≥ 31 must be normalised into Months before
    /// persisting.
    /// </summary>
    public int Days { get; set; }

    /// <summary>
    /// Source attribution per Annex 2 note (free-text reference, e.g. archive
    /// folio number, carnet de muncă page, court-ordered restitution decision).
    /// Capped at 200 chars by the EF configuration.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Free-text annotation. Capped at 500 chars by the EF configuration.</summary>
    public string? Notes { get; set; }
}
