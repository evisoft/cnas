namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1906 / TOR Annex 6 — one attempt to fan a finalised
/// <c>ReportRun</c> out to a configured channel via one
/// <see cref="ReportDistributionRule"/>. Each call to
/// <c>IReportDistributionDispatcher.DispatchAsync</c> writes ONE row per
/// matching active rule, with a terminal <c>Status</c>
/// (<see cref="ReportDispatchStatus.Delivered"/> /
/// <see cref="ReportDispatchStatus.Failed"/> /
/// <see cref="ReportDispatchStatus.Skipped"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Snapshot semantics.</b> The dispatch row snapshots the rule's
/// <see cref="Channel"/> and <see cref="RecipientKind"/> at fan-out time
/// — operators investigating a delivery in the past must NOT see the
/// rule's current shape if it has since been edited. The
/// <see cref="RuleId"/> FK still links back to the parent rule for
/// drill-down.
/// </para>
/// <para>
/// <b>Encryption at rest.</b> <see cref="RecipientCode"/> is encrypted at
/// rest with the same converter as on <see cref="ReportDistributionRule"/>.
/// The dispatch table has NO hash shadow column — equality lookups on this
/// table go through <see cref="RuleId"/> and <see cref="ReportRunSqid"/>.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.ReportDistributionDispatchDto.Id</c>)
/// carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>No PII in failure reasons.</b> When the channel handler fails the
/// dispatcher records a sanitised <see cref="FailureReason"/> string — the
/// channel handler's exception type or stable failure code — NEVER the
/// recipient address, the report payload, or any other PII.
/// </para>
/// </remarks>
public sealed class ReportDistributionDispatch : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="ReportDistributionRule"/>.</summary>
    public long RuleId { get; set; }

    /// <summary>
    /// Sqid-encoded id of the report run that triggered the dispatch. Stored
    /// as a string because the underlying report-run id may live in any of
    /// several producer entities (ReportRun / ReportJob / IntegrityCheckRun);
    /// the dispatcher carries the value through opaquely.
    /// </summary>
    public required string ReportRunSqid { get; set; }

    /// <summary>Snapshotted channel at dispatch time (matches the rule's value at fan-out).</summary>
    public ReportDistributionChannel Channel { get; set; }

    /// <summary>Snapshotted recipient kind at dispatch time.</summary>
    public ReportRecipientKind RecipientKind { get; set; }

    /// <summary>
    /// Encrypted-at-rest recipient code, snapshotted at dispatch time.
    /// Interpretation follows <see cref="RecipientKind"/> identically to
    /// the parent rule.
    /// </summary>
    public required string RecipientCode { get; set; }

    /// <summary>Terminal status — <c>Delivered</c> / <c>Failed</c> / <c>Skipped</c> / <c>Pending</c>.</summary>
    public ReportDispatchStatus Status { get; set; }

    /// <summary>UTC instant the dispatch was attempted (matches <see cref="AuditableEntity.CreatedAtUtc"/>).</summary>
    public DateTime DispatchedAt { get; set; }

    /// <summary>UTC instant the channel handler confirmed delivery; null for <c>Failed</c> / <c>Skipped</c>.</summary>
    public DateTime? DeliveredAt { get; set; }

    /// <summary>
    /// Sanitised reason populated when <see cref="Status"/> is
    /// <see cref="ReportDispatchStatus.Failed"/> or
    /// <see cref="ReportDispatchStatus.Skipped"/>. Capped at 500 characters;
    /// must NEVER contain PII (recipient addresses, IDNP, IBAN, etc.).
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>Number of retry attempts made before reaching the terminal status. Defaults to 0.</summary>
    public int RetryCount { get; set; }
}
