namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0820 / TOR BP 1.2-K — "closure of management period" anchor row. Captures
/// the moment an operator (typically the chief accountant) declares a reporting
/// month complete; once closed, no new <see cref="Declaration"/> rows may be
/// registered for the month — the service layer refuses with
/// <c>VALIDATION_FAILED / MONTH_CLOSED</c> unless an admin has re-opened the
/// month via <c>IManagementPeriodService.ReopenAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton per month.</b> <see cref="Month"/> is unique (day = 1 by
/// convention). Re-closing an already-closed month yields
/// <c>Conflict / MONTH_ALREADY_CLOSED</c>; closing a month that has been
/// re-opened replays the close path against the same row.
/// </para>
/// <para>
/// <b>Aggregate snapshot.</b> At close time the service captures three
/// generalising-report metrics — <see cref="TotalDeclaredAcrossPayers"/>,
/// <see cref="TotalPaidAcrossPayers"/>, <see cref="PayerCount"/>, and
/// <see cref="DeclarationCount"/> — from the
/// <see cref="MonthlyContributionCalculation"/> rolls. The values are an
/// immutable snapshot per CLAUDE.md "Immutable Snapshots" — later corrections
/// to a declaration do NOT change them, so the generalising report remains
/// reproducible by reading the close row.
/// </para>
/// <para>
/// <b>Re-open workflow.</b> An admin can re-open a closed month via
/// <c>IManagementPeriodService.ReopenAsync</c>; <see cref="IsReopened"/> flips
/// to <c>true</c> with the rationale captured in <see cref="ReopenReason"/>
/// and the actor/timestamp captured for the audit trail. The
/// <c>IsMonthClosedAsync</c> probe treats a re-opened month as open.
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/> because
/// the outbound DTO (<c>Cnas.Ps.Contracts.ManagementPeriodCloseDto.Id</c>)
/// carries the Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class ManagementPeriodClose : AuditableEntity, IExternalId
{
    /// <summary>
    /// Calendar month the closure covers (day = 1 by convention). Unique — one
    /// row per month for the lifetime of the database.
    /// </summary>
    public DateOnly Month { get; set; }

    /// <summary>UTC instant the closure was recorded.</summary>
    public DateTime ClosedAtUtc { get; set; }

    /// <summary>
    /// Foreign-key reference to the <see cref="UserProfile"/> who initiated the
    /// close. Distinct from <see cref="AuditableEntity.CreatedBy"/> which
    /// captures the Sqid string (the FK is preserved for joins).
    /// </summary>
    public long ClosedByUserId { get; set; }

    /// <summary>
    /// Operator-supplied free-form note (≤ 1000 chars when set). Typically the
    /// rationale for closing or the period boundary justification.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Sum of <see cref="MonthlyContributionCalculation.TotalAdjusted"/> across
    /// every payer for the month, captured at close time. Immutable snapshot.
    /// </summary>
    public decimal TotalDeclaredAcrossPayers { get; set; }

    /// <summary>
    /// Sum of paid contributions across every payer for the month, captured at
    /// close time. <b>Pragmatic shortcut</b>: until a payments ledger exists the
    /// service derives this from
    /// <see cref="MonthlyContributionCalculation.TotalAdjusted"/> minus
    /// <see cref="MonthlyContributionCalculation.UnderpaymentAmount"/>; a future
    /// iteration will pull from the receipts table.
    /// </summary>
    public decimal TotalPaidAcrossPayers { get; set; }

    /// <summary>
    /// Distinct payer count covered by the month's
    /// <see cref="MonthlyContributionCalculation"/> rolls. Immutable snapshot.
    /// </summary>
    public int PayerCount { get; set; }

    /// <summary>
    /// Total non-cancelled declarations across every payer for the month.
    /// Immutable snapshot. Sum of
    /// <see cref="MonthlyContributionCalculation.DeclarationCount"/> across
    /// every roll-up row for the month.
    /// </summary>
    public int DeclarationCount { get; set; }

    /// <summary>
    /// True when an admin has re-opened the closed month via
    /// <c>IManagementPeriodService.ReopenAsync</c>. The
    /// <c>IsMonthClosedAsync</c> probe treats a re-opened month as open so the
    /// declaration-registration service-layer guard does NOT refuse new rows.
    /// </summary>
    public bool IsReopened { get; set; }

    /// <summary>
    /// UTC instant the re-open was recorded; <c>null</c> while
    /// <see cref="IsReopened"/> is <c>false</c>.
    /// </summary>
    public DateTime? ReopenedAtUtc { get; set; }

    /// <summary>
    /// Foreign-key reference to the <see cref="UserProfile"/> who initiated the
    /// re-open; <c>null</c> while <see cref="IsReopened"/> is <c>false</c>.
    /// </summary>
    public long? ReopenedByUserId { get; set; }

    /// <summary>
    /// Operator-supplied rationale for the re-open action (3..500 chars when
    /// set). <c>null</c> while <see cref="IsReopened"/> is <c>false</c>.
    /// </summary>
    public string? ReopenReason { get; set; }
}
