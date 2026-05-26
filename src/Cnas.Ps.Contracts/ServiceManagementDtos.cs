using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2501-R2504 / TOR PIR 022-025 — DTOs for the service-management quartet:
// business-hours policy registry, maintenance-window registry, system-update
// schedule registry, and concrete system-update events. All Id fields are
// Sqid-encoded per CLAUDE.md RULE 3.
// ────────────────────────────────────────────────────────────────────────────

// ─────────── R2501 — BusinessHoursPolicy ───────────

/// <summary>R2501 / TOR PIR 024 — outbound projection of a business-hours policy.</summary>
/// <param name="Id">Sqid-encoded policy id.</param>
/// <param name="Code">Stable SCREAMING_SNAKE_CASE policy code.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="OpenTimeLocal">Local-time opening hour (HH:mm).</param>
/// <param name="CloseTimeLocal">Local-time closing hour (HH:mm).</param>
/// <param name="BusinessDaysMask">Bitmask of business days (Mon=1, Sun=64).</param>
/// <param name="TimezoneId">IANA timezone id.</param>
/// <param name="HolidayDatesJson">JSON array of YYYY-MM-DD strings (optional).</param>
/// <param name="IsActive">True when the policy is active.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BusinessHoursPolicyDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Code,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string OpenTimeLocal,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string CloseTimeLocal,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int BusinessDaysMask,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TimezoneId,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? HolidayDatesJson,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsActive);

/// <summary>R2501 / TOR PIR 024 — input envelope for creating a business-hours policy.</summary>
/// <param name="Code">Stable SCREAMING_SNAKE_CASE policy code.</param>
/// <param name="DisplayName">Display name (3..256 chars).</param>
/// <param name="Description">Optional free-form description (≤ 1000 chars).</param>
/// <param name="OpenTimeLocal">Local-time opening hour (HH:mm).</param>
/// <param name="CloseTimeLocal">Local-time closing hour (HH:mm).</param>
/// <param name="BusinessDaysMask">Bitmask of business days (1..127).</param>
/// <param name="TimezoneId">IANA timezone id (default Europe/Chisinau).</param>
/// <param name="HolidayDatesJson">Optional JSON array of YYYY-MM-DD strings.</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record BusinessHoursPolicyCreateInputDto(
    string Code,
    string DisplayName,
    string? Description,
    string OpenTimeLocal,
    string CloseTimeLocal,
    int BusinessDaysMask,
    string TimezoneId,
    string? HolidayDatesJson);

/// <summary>R2501 / TOR PIR 024 — input envelope for modifying an existing policy.</summary>
/// <param name="DisplayName">Display name (3..256 chars).</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="OpenTimeLocal">Local-time opening hour (HH:mm).</param>
/// <param name="CloseTimeLocal">Local-time closing hour (HH:mm).</param>
/// <param name="BusinessDaysMask">Bitmask of business days (1..127).</param>
/// <param name="TimezoneId">IANA timezone id.</param>
/// <param name="HolidayDatesJson">JSON array of YYYY-MM-DD strings.</param>
/// <param name="ChangeReason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record BusinessHoursPolicyModifyInputDto(
    string? DisplayName,
    string? Description,
    string? OpenTimeLocal,
    string? CloseTimeLocal,
    int? BusinessDaysMask,
    string? TimezoneId,
    string? HolidayDatesJson,
    string ChangeReason);

/// <summary>R2501 / TOR PIR 024 — input envelope for reason-bearing transitions.</summary>
/// <param name="Reason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BusinessHoursPolicyReasonInputDto(string Reason);

/// <summary>R2501 / TOR PIR 024 — filter envelope for the policy-list endpoint.</summary>
/// <param name="IsActive">Optional IsActive filter.</param>
/// <param name="Skip">Page offset (default 0; ≥ 0).</param>
/// <param name="Take">Page size (default 50; 1..100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record BusinessHoursPolicyFilterDto(
    bool? IsActive = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2501 / TOR PIR 024 — paged envelope returned by the policy-list endpoint.</summary>
/// <param name="Items">Policies on the requested page.</param>
/// <param name="Total">Total matching rows.</param>
/// <param name="Skip">Page offset applied.</param>
/// <param name="Take">Page size applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BusinessHoursPolicyPageDto(
    IReadOnlyList<BusinessHoursPolicyDto> Items,
    int Total,
    int Skip,
    int Take);

// ─────────── R2502 — MaintenanceWindow ───────────

/// <summary>R2502 / TOR PIR 025 — outbound projection of a maintenance window.</summary>
/// <param name="Id">Sqid-encoded window id.</param>
/// <param name="WindowNumber">Deterministic MW-{year}-{seq} window number.</param>
/// <param name="BusinessHoursPolicySqid">Sqid of the referenced business-hours policy.</param>
/// <param name="WindowKind">Stable enum-name (Ordinary / Major / Urgent).</param>
/// <param name="Title">Short title.</param>
/// <param name="Description">Free-form description.</param>
/// <param name="ScheduledStartUtc">UTC start instant.</param>
/// <param name="ScheduledEndUtc">UTC end instant.</param>
/// <param name="Status">Stable enum-name of lifecycle state.</param>
/// <param name="NoticePostedAt">UTC instant the notice was posted (or null).</param>
/// <param name="ApprovedAt">UTC instant the window was approved (or null).</param>
/// <param name="StartedAt">UTC instant the maintenance actually started (or null).</param>
/// <param name="CompletedAt">UTC instant the maintenance completed (or null).</param>
/// <param name="CancelledAt">UTC instant the window was cancelled (or null).</param>
/// <param name="CancelReason">Free-form cancellation reason (or null).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MaintenanceWindowDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string WindowNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BusinessHoursPolicySqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string WindowKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime ScheduledStartUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime ScheduledEndUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? NoticePostedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? ApprovedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? StartedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? CompletedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? CancelledAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancelReason);

/// <summary>R2502 / TOR PIR 025 — input envelope for creating a maintenance window.</summary>
/// <param name="BusinessHoursPolicyCode">Code of the referenced business-hours policy.</param>
/// <param name="WindowKind">Stable enum-name (Ordinary / Major / Urgent).</param>
/// <param name="Title">Short title (3..256 chars).</param>
/// <param name="Description">Free-form description (3..2000 chars).</param>
/// <param name="ScheduledStartUtc">UTC start instant (must be in the future).</param>
/// <param name="ScheduledEndUtc">UTC end instant (must be strictly after start).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record MaintenanceWindowCreateInputDto(
    string BusinessHoursPolicyCode,
    string WindowKind,
    string Title,
    string Description,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc);

/// <summary>R2502 / TOR PIR 025 — input envelope for reason-bearing transitions.</summary>
/// <param name="Reason">Free-form reason (3..500 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MaintenanceWindowReasonInputDto(string Reason);

/// <summary>R2502 / TOR PIR 025 — filter envelope for the maintenance-window list endpoint.</summary>
/// <param name="Status">Optional status filter (stable enum-name).</param>
/// <param name="WindowKind">Optional kind filter (stable enum-name).</param>
/// <param name="ScheduledStartAfterUtc">Optional lower bound on ScheduledStartUtc.</param>
/// <param name="ScheduledStartBeforeUtc">Optional upper bound on ScheduledStartUtc.</param>
/// <param name="Skip">Page offset (default 0; ≥ 0).</param>
/// <param name="Take">Page size (default 50; 1..100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record MaintenanceWindowFilterDto(
    string? Status = null,
    string? WindowKind = null,
    DateTime? ScheduledStartAfterUtc = null,
    DateTime? ScheduledStartBeforeUtc = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2502 / TOR PIR 025 — paged envelope returned by the maintenance-window list endpoint.</summary>
/// <param name="Items">Windows on the requested page.</param>
/// <param name="Total">Total matching rows.</param>
/// <param name="Skip">Page offset applied.</param>
/// <param name="Take">Page size applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MaintenanceWindowPageDto(
    IReadOnlyList<MaintenanceWindowDto> Items,
    int Total,
    int Skip,
    int Take);

// ─────────── R2503 — SystemUpdateSchedule ───────────

/// <summary>R2503 / TOR PIR 022-023 — outbound projection of a system-update schedule.</summary>
/// <param name="Id">Sqid-encoded schedule id.</param>
/// <param name="ScheduleCode">Stable SCREAMING_SNAKE_CASE schedule code.</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="Cadence">Stable enum-name of the cadence.</param>
/// <param name="NoticeLeadTimeDays">Days of advance notice required.</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="IsActive">True when the schedule is active.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SystemUpdateScheduleDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ScheduleCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Cadence,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int NoticeLeadTimeDays,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsActive);

/// <summary>R2503 / TOR PIR 022-023 — input envelope for creating a system-update schedule.</summary>
/// <param name="ScheduleCode">Stable SCREAMING_SNAKE_CASE schedule code.</param>
/// <param name="Title">Title (3..256 chars).</param>
/// <param name="Cadence">Stable enum-name of the cadence.</param>
/// <param name="NoticeLeadTimeDays">Days of advance notice required (0..730).</param>
/// <param name="Description">Optional free-form description (≤ 2000 chars).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record SystemUpdateScheduleCreateInputDto(
    string ScheduleCode,
    string Title,
    string Cadence,
    int NoticeLeadTimeDays,
    string? Description);

/// <summary>R2503 / TOR PIR 022-023 — input envelope for modifying an existing schedule.</summary>
/// <param name="Title">Title (3..256 chars).</param>
/// <param name="NoticeLeadTimeDays">Days of advance notice required (0..730).</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="ChangeReason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record SystemUpdateScheduleModifyInputDto(
    string? Title,
    int? NoticeLeadTimeDays,
    string? Description,
    string ChangeReason);

/// <summary>R2503 / TOR PIR 022-023 — input envelope for reason-bearing transitions.</summary>
/// <param name="Reason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SystemUpdateScheduleReasonInputDto(string Reason);

/// <summary>R2503 / TOR PIR 022-023 — filter envelope for the schedule-list endpoint.</summary>
/// <param name="IsActive">Optional IsActive filter.</param>
/// <param name="Cadence">Optional cadence filter (stable enum-name).</param>
/// <param name="Skip">Page offset.</param>
/// <param name="Take">Page size (1..100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record SystemUpdateScheduleFilterDto(
    bool? IsActive = null,
    string? Cadence = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2503 / TOR PIR 022-023 — paged envelope returned by the schedule-list endpoint.</summary>
/// <param name="Items">Schedules on the requested page.</param>
/// <param name="Total">Total matching rows.</param>
/// <param name="Skip">Page offset applied.</param>
/// <param name="Take">Page size applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SystemUpdateSchedulePageDto(
    IReadOnlyList<SystemUpdateScheduleDto> Items,
    int Total,
    int Skip,
    int Take);

// ─────────── R2504 — SystemUpdateEvent ───────────

/// <summary>R2504 / TOR PIR 024 — outbound projection of a system-update event.</summary>
/// <param name="Id">Sqid-encoded event id.</param>
/// <param name="ScheduleSqid">Sqid of the parent schedule.</param>
/// <param name="EventNumber">Deterministic UPD-{year}-{seq} event number.</param>
/// <param name="Title">Title.</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="PlannedDeploymentUtc">UTC deployment instant.</param>
/// <param name="Status">Stable enum-name of lifecycle state.</param>
/// <param name="NotifiedAt">UTC instant the public notice was issued (or null).</param>
/// <param name="DeploymentStartedAt">UTC instant the deployment started (or null).</param>
/// <param name="DeploymentCompletedAt">UTC instant the deployment completed (or null).</param>
/// <param name="CancelledAt">UTC instant the event was cancelled (or null).</param>
/// <param name="CancelReason">Cancellation reason (or null).</param>
/// <param name="MaintenanceWindowSqid">Sqid of the associated maintenance window (or null).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SystemUpdateEventDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ScheduleSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string EventNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime PlannedDeploymentUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? NotifiedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? DeploymentStartedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? DeploymentCompletedAt,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime? CancelledAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancelReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? MaintenanceWindowSqid);

/// <summary>R2504 / TOR PIR 024 — input envelope for creating a system-update event.</summary>
/// <param name="ScheduleCode">Code of the parent schedule.</param>
/// <param name="Title">Title (3..256 chars).</param>
/// <param name="Description">Optional free-form description (≤ 2000 chars).</param>
/// <param name="PlannedDeploymentUtc">UTC deployment instant (must satisfy schedule lead-time).</param>
/// <param name="MaintenanceWindowSqid">Optional Sqid of an existing maintenance window.</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record SystemUpdateEventCreateInputDto(
    string ScheduleCode,
    string Title,
    string? Description,
    DateTime PlannedDeploymentUtc,
    string? MaintenanceWindowSqid);

/// <summary>R2504 / TOR PIR 024 — input envelope for reason-bearing transitions.</summary>
/// <param name="Reason">Free-form reason (3..500 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SystemUpdateEventReasonInputDto(string Reason);

/// <summary>R2504 / TOR PIR 024 — filter envelope for the event-list endpoint.</summary>
/// <param name="Status">Optional status filter (stable enum-name).</param>
/// <param name="ScheduleSqid">Optional Sqid filter.</param>
/// <param name="Skip">Page offset.</param>
/// <param name="Take">Page size (1..100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record SystemUpdateEventFilterDto(
    string? Status = null,
    string? ScheduleSqid = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2504 / TOR PIR 024 — paged envelope returned by the event-list endpoint.</summary>
/// <param name="Items">Events on the requested page.</param>
/// <param name="Total">Total matching rows.</param>
/// <param name="Skip">Page offset applied.</param>
/// <param name="Take">Page size applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record SystemUpdateEventPageDto(
    IReadOnlyList<SystemUpdateEventDto> Items,
    int Total,
    int Skip,
    int Take);
