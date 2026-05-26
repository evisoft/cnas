using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ServiceManagement;

/// <summary>
/// R2502 / TOR PIR 025 — admin-facing service over the maintenance-window
/// registry. Owns the state-machine: Draft → NoticePeriod → Approved →
/// InProgress → Completed (or Cancelled from any non-terminal state) and
/// enforces both the per-kind duration ceilings (Ordinary ≤ 4h, Major ≤ 24h,
/// Urgent ≤ 2h) and the per-kind advance-notice lead times (Ordinary ≥ 5 BD,
/// Major ≥ 10 BD, Urgent immediate).
/// </summary>
public interface IMaintenanceWindowService
{
    /// <summary>Stable audit event code emitted when a window is created.</summary>
    public const string AuditCreated = "MAINT.CREATED";

    /// <summary>Stable audit event code emitted when the public notice is posted.</summary>
    public const string AuditNoticePosted = "MAINT.NOTICE_POSTED";

    /// <summary>Stable audit event code emitted when the window is approved.</summary>
    public const string AuditApproved = "MAINT.APPROVED";

    /// <summary>Stable audit event code emitted when the maintenance starts.</summary>
    public const string AuditStarted = "MAINT.STARTED";

    /// <summary>Stable audit event code emitted when the maintenance completes.</summary>
    public const string AuditCompleted = "MAINT.COMPLETED";

    /// <summary>Stable audit event code emitted when the window is cancelled.</summary>
    public const string AuditCancelled = "MAINT.CANCELLED";

    /// <summary>Maximum allowed duration of an Ordinary window.</summary>
    public const int OrdinaryMaxHours = 4;

    /// <summary>Maximum allowed duration of a Major window.</summary>
    public const int MajorMaxHours = 24;

    /// <summary>Maximum allowed duration of an Urgent window.</summary>
    public const int UrgentMaxHours = 2;

    /// <summary>Minimum business-days notice for an Ordinary window.</summary>
    public const int OrdinaryMinNoticeBusinessDays = 5;

    /// <summary>Minimum business-days notice for a Major window.</summary>
    public const int MajorMinNoticeBusinessDays = 10;

    /// <summary>Creates a new maintenance window in Draft state.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created DTO on success.</returns>
    Task<Result<MaintenanceWindowDto>> CreateAsync(
        MaintenanceWindowCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Posts the public notice (Draft → NoticePeriod). Enforces per-kind lead time.</summary>
    /// <param name="windowSqid">Sqid-encoded window id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<MaintenanceWindowDto>> PostNoticeAsync(
        string windowSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Approves the window (NoticePeriod → Approved).</summary>
    /// <param name="windowSqid">Sqid-encoded window id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<MaintenanceWindowDto>> ApproveAsync(
        string windowSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the maintenance as started (Approved → InProgress).</summary>
    /// <param name="windowSqid">Sqid-encoded window id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<MaintenanceWindowDto>> StartAsync(
        string windowSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the maintenance as completed (InProgress → Completed).</summary>
    /// <param name="windowSqid">Sqid-encoded window id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<MaintenanceWindowDto>> CompleteAsync(
        string windowSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels the window from any non-terminal state.</summary>
    /// <param name="windowSqid">Sqid-encoded window id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<MaintenanceWindowDto>> CancelAsync(
        string windowSqid,
        MaintenanceWindowReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one window by Sqid.</summary>
    /// <param name="windowSqid">Sqid-encoded window id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<MaintenanceWindowDto>> GetByIdAsync(
        string windowSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Lists windows (paged + filterable).</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<MaintenanceWindowPageDto>> ListAsync(
        MaintenanceWindowFilterDto filter,
        CancellationToken cancellationToken = default);
}
