namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0516 / TOR CF 02.04 — one contribution row attributed to a
/// <see cref="PersonalAccount"/>. Each entry represents the social-insurance
/// contribution recorded for a specific (Year, Month) bucket from a specific
/// source (employer report, DEC 100 self-declaration, ad-hoc individual
/// payment, ...). The personal-account extract aggregates these rows per
/// year for the citizen self-service surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal entity, no IExternalId.</b> Entries never cross the API
/// boundary by their surrogate id; the extract DTO projects entry data
/// inline. They are an implementation detail of the
/// <see cref="PersonalAccount"/> aggregate per the
/// architecture-test heuristic on <see cref="IExternalId"/>.
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b> The combination
/// <c>(PersonalAccountId, Year, Month, SourceCode)</c> is unique — the same
/// account cannot receive two entries from the same source covering the same
/// month. Enforced via a composite unique index in the EF configuration.
/// </para>
/// <para>
/// <b>Currency.</b> Both monetary columns are MDL (Moldovan leu). The schema
/// stores them as plain decimal — no <c>Money</c> value-object wrapping —
/// because the entry is never used as an arithmetic operand outside the
/// CNAS local currency context.
/// </para>
/// </remarks>
public sealed class PersonalAccountEntry : AuditableEntity
{
    /// <summary>
    /// Foreign-key reference to the owning <see cref="PersonalAccount"/>.
    /// </summary>
    public long PersonalAccountId { get; set; }

    /// <summary>
    /// Calendar year (Gregorian) of the contribution. Stored as int so the
    /// composite unique index can range-scan efficiently.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Calendar month of the contribution, 1..12. Bounds checked by the
    /// application layer before insert; storage is plain int for portability.
    /// </summary>
    public int Month { get; set; }

    /// <summary>
    /// Gross income subject to contribution for the month (MDL). Distinct
    /// from <see cref="ContributionPaidAmount"/> because the calculator's
    /// "average monthly contribution base" projects this column, not the
    /// paid amount.
    /// </summary>
    public decimal ContributionBaseAmount { get; set; }

    /// <summary>
    /// Actual contribution paid for the month (MDL). Aggregated by the
    /// extract into the year-level total and the grand total.
    /// </summary>
    public decimal ContributionPaidAmount { get; set; }

    /// <summary>
    /// Source code identifying the system / process that produced this row.
    /// Stable strings — typically <c>"EMPLOYER_REPORT"</c>, <c>"DEC100"</c>,
    /// or <c>"INDIVIDUAL_PAYMENT"</c>. Part of the natural-key tuple so two
    /// sources can cover the same month without conflict.
    /// </summary>
    public required string SourceCode { get; set; }
}
