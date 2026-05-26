namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2506 / TOR PIR 037-040 — preventive action linked to a
/// <see cref="QualityRisk"/>. Each row tracks the planned mitigation, its
/// status, due date, assignment, and completion.
/// </summary>
/// <remarks>
/// Implements <see cref="IExternalId"/> — actions are surfaced to operators by Sqid.
/// </remarks>
public sealed class QualityRiskPreventiveAction : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="QualityRisk"/>.</summary>
    public long RiskId { get; set; }

    /// <summary>Description of the planned mitigation (≤ 2000 chars).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Lifecycle state — Planned / InProgress / Implemented / Cancelled.</summary>
    public QualityRiskActionStatus Status { get; set; }

    /// <summary>Calendar due date for the action.</summary>
    public DateOnly DueDate { get; set; }

    /// <summary>User id of the assignee responsible for the action.</summary>
    public int AssignedToUserId { get; set; }

    /// <summary>UTC instant the action was marked Implemented (null until then).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Free-form completion note (≤ 1000 chars).</summary>
    public string? CompletionNote { get; set; }
}
