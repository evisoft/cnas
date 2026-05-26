namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2504 / TOR PIR 024 — single concrete planned system-update event. Each
/// row references a parent <see cref="SystemUpdateSchedule"/> and, optionally,
/// the <see cref="MaintenanceWindow"/> during which the deployment will
/// happen. Lifecycle is enforced strictly inside
/// <c>ISystemUpdateEventService</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — events are
/// surfaced to operators by Sqid.
/// </para>
/// <para>
/// <b>Auto-numbering.</b> <see cref="EventNumber"/> is auto-generated as
/// <c>UPD-{year}-{seq:000000}</c> on create.
/// </para>
/// <para>
/// <b>Notice cadence.</b> The orchestrator job <c>SystemUpdateNotificationJob</c>
/// sweeps rows in <see cref="SystemUpdateEventStatus.Planned"/> whose
/// <see cref="PlannedDeploymentUtc"/> is within the parent schedule's
/// lead-time and flips them to <see cref="SystemUpdateEventStatus.Notified"/>.
/// </para>
/// </remarks>
public sealed class SystemUpdateEvent : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="SystemUpdateSchedule"/>.</summary>
    public long ScheduleId { get; set; }

    /// <summary>
    /// Deterministic event-number string in the form
    /// <c>UPD-{year}-{seq:000000}</c>; bounded to 32 chars; unique across the
    /// system.
    /// </summary>
    public string EventNumber { get; set; } = string.Empty;

    /// <summary>Short title of the update (3..256 chars).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional free-form description (≤ 2000 chars).</summary>
    public string? Description { get; set; }

    /// <summary>UTC instant when the deployment is planned.</summary>
    public DateTime PlannedDeploymentUtc { get; set; }

    /// <summary>Lifecycle state — see <see cref="SystemUpdateEventStatus"/>.</summary>
    public SystemUpdateEventStatus Status { get; set; }

    /// <summary>UTC instant the public notice was issued (null until Notified).</summary>
    public DateTime? NotifiedAt { get; set; }

    /// <summary>UTC instant the deployment actually started.</summary>
    public DateTime? DeploymentStartedAt { get; set; }

    /// <summary>UTC instant the deployment completed.</summary>
    public DateTime? DeploymentCompletedAt { get; set; }

    /// <summary>UTC instant the event was cancelled.</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>Free-form cancellation reason (3..500 chars; null unless cancelled).</summary>
    public string? CancelReason { get; set; }

    /// <summary>Optional FK to the maintenance window during which the deployment will run.</summary>
    public long? MaintenanceWindowId { get; set; }
}
