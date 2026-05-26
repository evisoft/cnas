namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0813 / TOR BP 1.2-D (Annex 8) — per-payer per-month roll-up of every
/// <see cref="Declaration"/> row attributed to a <see cref="Contributor"/>. One
/// row exists for each (ContributorId, Month) tuple that has been calculated
/// at least once; re-running the calculator upserts the same row in place so
/// the natural key is also the idempotency key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Calculation contract.</b> The R0813 aggregator
/// (<c>IMonthlyContributionCalculator</c>):
/// <list type="number">
///   <item>Selects every non-<see cref="DeclarationStatus.Cancelled"/> row for
///     the (contributor, month) tuple via <see cref="Declaration.ReportingMonth"/>.</item>
///   <item>Sums <see cref="Declaration.DeclaredContributionAmount"/> into
///     <see cref="TotalDeclared"/>.</item>
///   <item>Sums <see cref="Declaration.AdjustedContributionAmount"/> when set,
///     falling back to the declared figure when null, into
///     <see cref="TotalAdjusted"/>.</item>
///   <item>Computes <see cref="OverpaymentAmount"/> / <see cref="UnderpaymentAmount"/>
///     from the delta — only one of the two is populated per row.</item>
/// </list>
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b> <c>(ContributorId, Month)</c> is unique — a
/// payer cannot have two competing calculations for the same month. The
/// idempotent calculator upserts in place: re-running for the same key updates
/// the existing row rather than inserting a duplicate.
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/> because
/// the outbound DTO (<c>Cnas.Ps.Contracts.MonthlyContributionCalculationDto.Id</c>)
/// carries the Sqid-encoded surrogate per CLAUDE.md RULE 3 — operators reference
/// an individual calculation when reconciling against the upstream ledger.
/// </para>
/// </remarks>
public sealed class MonthlyContributionCalculation : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign-key reference to the <see cref="Contributor"/> whose declarations
    /// are aggregated by this row.
    /// </summary>
    public long ContributorId { get; set; }

    /// <summary>
    /// Calendar month covered by the calculation. By convention the day
    /// component is always 1 (matches <see cref="Declaration.ReportingMonth"/>).
    /// </summary>
    public DateOnly Month { get; set; }

    /// <summary>
    /// Sum of <see cref="Declaration.DeclaredContributionAmount"/> across every
    /// non-cancelled declaration for the (contributor, month) tuple. Zero when
    /// the payer has no declarations on file.
    /// </summary>
    public decimal TotalDeclared { get; set; }

    /// <summary>
    /// Sum of <see cref="Declaration.AdjustedContributionAmount"/> when set,
    /// falling back to <see cref="Declaration.DeclaredContributionAmount"/>
    /// otherwise, across every non-cancelled declaration. Equals
    /// <see cref="TotalDeclared"/> when no row carries an adjustment.
    /// </summary>
    public decimal TotalAdjusted { get; set; }

    /// <summary>
    /// Positive when <see cref="TotalAdjusted"/> &lt; <see cref="TotalDeclared"/>
    /// (the payer over-declared and is owed a refund). Null otherwise so
    /// reporting queries can use <c>WHERE OverpaymentAmount IS NOT NULL</c> as
    /// the "owes a refund" predicate without false positives for zero.
    /// </summary>
    public decimal? OverpaymentAmount { get; set; }

    /// <summary>
    /// Positive when <see cref="TotalAdjusted"/> &gt; <see cref="TotalDeclared"/>
    /// (the payer under-declared and owes the difference). Null otherwise.
    /// Mutually exclusive with <see cref="OverpaymentAmount"/>.
    /// </summary>
    public decimal? UnderpaymentAmount { get; set; }

    /// <summary>
    /// Number of non-cancelled declarations rolled into this row. Zero when no
    /// declarations exist for the (contributor, month) tuple.
    /// </summary>
    public int DeclarationCount { get; set; }

    /// <summary>
    /// UTC instant the calculation was last produced. Updated on every
    /// idempotent re-run so operators can chart freshness.
    /// </summary>
    public DateTime CalculatedAtUtc { get; set; }
}
