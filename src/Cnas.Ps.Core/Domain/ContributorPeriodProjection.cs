namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0153 / TOR CF 19.05 — pre-aggregated period-aware projection row for a single
/// <see cref="InsuredPerson"/> ("Persoană asigurată" / Contributor). Each row
/// represents a slice <c>[<see cref="PeriodStartUtc"/>, <see cref="PeriodEndUtc"/>)</c>
/// during which every projected field held a consistent value. The
/// <c>ContributorPeriodProjectionJob</c> rebuilds the projection daily; reports
/// then resolve "as-of date X" with a single
/// <c>WHERE PeriodStartUtc &lt;= asOf &amp;&amp; asOf &lt; PeriodEndUtc</c> instead
/// of scanning the OLTP supersession chains.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why projections.</b> Reports today scan
/// <see cref="ContributorAddress"/> / <see cref="ContributorContact"/> /
/// <see cref="ContributorActivityPeriod"/> / <see cref="ContributorCivilStatus"/>
/// and resolve "what was the state as-of date X?" ad-hoc. CF 19.05 mandates a
/// period-aware projection layer so reports can issue
/// <c>WHERE date BETWEEN ValidFrom AND ValidTo</c> cleanly. Each slice on this
/// table is a flattened view across every source-table supersession boundary.
/// </para>
/// <para>
/// <b>Open-ended slices.</b> When the underlying source row carries
/// <c><see cref="ContributorAddress.ValidToUtc"/> = null</c> (current row), the
/// projection materialises that as <see cref="DateTime.MaxValue"/> for the
/// outermost slice so the natural query
/// (<c>PeriodStartUtc &lt;= asOf &amp;&amp; asOf &lt; PeriodEndUtc</c>) does not
/// need a NULL-aware branch.
/// </para>
/// <para>
/// <b>Idempotent rebuild.</b> Rebuilding the projection for a contributor is a
/// pure DELETE-then-INSERT — the unique index
/// <c>(ContributorId, PeriodStartUtc)</c> protects against accidental
/// duplication. A re-run produces the same slice set as long as the source
/// rows haven't changed.
/// </para>
/// <para>
/// <b>Sensitivity.</b> Citizen phone and email are sourced into this table to
/// satisfy the "single read for the period" contract, so the DTO surface carries
/// <c>Confidential</c> sensitivity classifications per R0228. The entity itself
/// stores plaintext (the production DbContext does not currently field-encrypt
/// these columns) — the encryption hooks remain available if a future SEC 035
/// follow-up extends the protected-field set.
/// </para>
/// </remarks>
public sealed class ContributorPeriodProjection : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the parent <see cref="InsuredPerson"/> (Persoană asigurată) row.
    /// Matches the <c>ContributorId</c> column on the source supersession
    /// tables (<see cref="ContributorAddress"/>, <see cref="ContributorContact"/>,
    /// etc.) so the projection joins back to the OLTP identity 1:1.
    /// </summary>
    public long ContributorId { get; set; }

    /// <summary>
    /// Inclusive UTC start of the slice. Equal to the earliest
    /// <c>ValidFromUtc</c> boundary across the source rows that contribute
    /// values to this slice.
    /// </summary>
    public DateTime PeriodStartUtc { get; set; }

    /// <summary>
    /// Exclusive UTC end of the slice. Set to <see cref="DateTime.MaxValue"/>
    /// when the underlying source row is open-ended (<c>ValidToUtc = null</c>);
    /// otherwise equal to the earliest <c>ValidToUtc</c> boundary that closes
    /// at least one source row contributing to the slice.
    /// </summary>
    public DateTime PeriodEndUtc { get; set; }

    /// <summary>
    /// Stable string code of the contributor's civil status during the slice
    /// (e.g. <c>"Single"</c>, <c>"Married"</c>, <c>"Divorced"</c>). Sourced
    /// from <see cref="ContributorCivilStatus"/>. Null when no civil-status
    /// row covered the slice midpoint.
    /// </summary>
    public string? CivilStatus { get; set; }

    /// <summary>
    /// Employer-code string (typically the IDNO of a
    /// <see cref="Contributor"/> / Plătitor) for the activity period covering
    /// the slice midpoint. When the citizen held multiple simultaneous
    /// employments, the most-recently-created row wins per the
    /// <c>PeriodSliceBuilder</c> tie-break rule. Null when no activity period
    /// covered the slice.
    /// </summary>
    public string? CurrentEmployerCode { get; set; }

    /// <summary>
    /// Monthly salary at the activity period covering the slice (MDL).
    /// Null when no activity period covered the slice OR when the source row
    /// did not record a salary.
    /// </summary>
    public decimal? MonthlySalary { get; set; }

    /// <summary>City / town from the <see cref="ContributorAddress"/> covering the slice.</summary>
    public string? AddressCity { get; set; }

    /// <summary>Region (raion / county) from the address row covering the slice.</summary>
    public string? AddressRegion { get; set; }

    /// <summary>ISO-3166-1 alpha-2 country code from the address row covering the slice.</summary>
    public string? AddressCountry { get; set; }

    /// <summary>Phone in E.164 format from the <see cref="ContributorContact"/> covering the slice.</summary>
    public string? PhoneE164 { get; set; }

    /// <summary>Email from the contact row covering the slice.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// UTC instant at which this projection row was generated by the
    /// <c>ContributorPeriodProjectionService</c>. Surfaced primarily for
    /// operator observability — comparing
    /// <see cref="AuditableEntity.UpdatedAtUtc"/> against this column reveals
    /// rows that survived an idempotent re-run unchanged.
    /// </summary>
    public DateTime ProjectedAtUtc { get; set; }
}
