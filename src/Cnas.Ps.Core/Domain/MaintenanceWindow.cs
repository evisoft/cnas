namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2502 / TOR PIR 025 — single planned maintenance window. Per the TOR each
/// window carries a kind-specific duration ceiling and notice-lead-time
/// requirement (Ordinary ≤ 4h / 5 business days; Major ≤ 24h / 10 business
/// days; Urgent ≤ 2h / immediate). Both checks are enforced inside the
/// service layer at create / post-notice time.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — operators and
/// downstream consumers reference windows by Sqid through the admin surface.
/// </para>
/// <para>
/// <b>Auto-numbering.</b> <see cref="WindowNumber"/> is auto-generated as
/// <c>MW-{year}-{seq:000000}</c> by the service layer on create.
/// </para>
/// </remarks>
public sealed class MaintenanceWindow : AuditableEntity, IExternalId
{
    /// <summary>
    /// Deterministic window-number string in the form
    /// <c>MW-{year}-{seq:000000}</c>; bounded to 32 chars; unique across the
    /// system.
    /// </summary>
    public string WindowNumber { get; set; } = string.Empty;

    /// <summary>FK to the <see cref="BusinessHoursPolicy"/> used to compute "business days notice".</summary>
    public long BusinessHoursPolicyId { get; set; }

    /// <summary>Window classification — Ordinary / Major / Urgent.</summary>
    public MaintenanceWindowKind WindowKind { get; set; }

    /// <summary>Short title of the window (3..256 chars).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Free-form description of the work (3..2000 chars).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>UTC instant the window is scheduled to start.</summary>
    public DateTime ScheduledStartUtc { get; set; }

    /// <summary>UTC instant the window is scheduled to end.</summary>
    public DateTime ScheduledEndUtc { get; set; }

    /// <summary>Lifecycle state — see <see cref="MaintenanceWindowStatus"/>.</summary>
    public MaintenanceWindowStatus Status { get; set; }

    /// <summary>Internal id of the operator who created the window.</summary>
    public long RequestedByUserId { get; set; }

    /// <summary>Internal id of the operator who approved the window (null until approved).</summary>
    public long? ApprovedByUserId { get; set; }

    /// <summary>UTC instant the public notice was posted (null while Draft).</summary>
    public DateTime? NoticePostedAt { get; set; }

    /// <summary>UTC instant the window was approved (null pre-approval).</summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>UTC instant the maintenance work actually started (null pre-start).</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>UTC instant the maintenance work completed (null until Completed).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>UTC instant the window was cancelled (null unless cancelled).</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>Free-form cancellation reason (3..500 chars; null unless cancelled).</summary>
    public string? CancelReason { get; set; }
}
