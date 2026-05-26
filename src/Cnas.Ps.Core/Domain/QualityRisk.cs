namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2506 / TOR PIR 037-040 — quality-assurance risk registry entry. Captures
/// risks identified during the QA process, the preventive actions linked to
/// each risk, owner attribution, and the last-review timestamp consulted by
/// the annual-review sweep job.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — risks are
/// surfaced to operators by Sqid.
/// </para>
/// <para>
/// <b>Annual review cadence.</b> The Quartz job
/// <c>QualityRiskReviewSweepJob</c> sweeps rows whose
/// <see cref="LastReviewedAt"/> is null or older than 365 days and emits an
/// Information-severity audit row <c>QA_RISK.REVIEW_OVERDUE</c> per overdue
/// risk.
/// </para>
/// </remarks>
public sealed class QualityRisk : AuditableEntity, IExternalId
{
    /// <summary>Stable SCREAMING_SNAKE_CASE code (≤ 64 chars). Unique.</summary>
    public string RiskCode { get; set; } = string.Empty;

    /// <summary>Short title (3..256 chars).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Free-form description (50..4000 chars).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Risk category — Technical / Process / Security / Compliance / External / People.</summary>
    public QualityRiskCategory Category { get; set; }

    /// <summary>Qualitative likelihood band.</summary>
    public QualityRiskLikelihood Likelihood { get; set; }

    /// <summary>Qualitative impact band.</summary>
    public QualityRiskImpact Impact { get; set; }

    /// <summary>Lifecycle state — Open / Mitigating / Closed / Accepted.</summary>
    public QualityRiskStatus Status { get; set; }

    /// <summary>User id of the risk owner (always populated).</summary>
    public int OwnerUserId { get; set; }

    /// <summary>UTC instant the risk was identified.</summary>
    public DateTime IdentifiedAt { get; set; }

    /// <summary>UTC instant of the last review (null until first review).</summary>
    public DateTime? LastReviewedAt { get; set; }

    /// <summary>UTC instant the risk was closed (null unless terminal).</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>Free-form closure reason (3..1000 chars).</summary>
    public string? ClosureReason { get; set; }
}
