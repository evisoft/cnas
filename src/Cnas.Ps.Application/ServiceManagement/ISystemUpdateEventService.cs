using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ServiceManagement;

/// <summary>
/// R2504 / TOR PIR 024 — admin-facing service over the
/// <c>SystemUpdateEvent</c> registry. Owns the state-machine:
/// Planned → Notified → Deploying → Deployed (or Cancelled from any
/// non-terminal state). Enforces the parent schedule's notice lead-time
/// requirement at create time.
/// </summary>
public interface ISystemUpdateEventService
{
    /// <summary>Stable audit event code emitted when an event is created.</summary>
    public const string AuditCreated = "UPDATE.EVENT.CREATED";

    /// <summary>Stable audit event code emitted when an event's notice is dispatched.</summary>
    public const string AuditNotified = "UPDATE.NOTIFIED";

    /// <summary>Stable audit event code emitted when the deployment starts.</summary>
    public const string AuditDeploymentStarted = "UPDATE.DEPLOYMENT_STARTED";

    /// <summary>Stable audit event code emitted when the deployment completes.</summary>
    public const string AuditDeploymentCompleted = "UPDATE.DEPLOYMENT_COMPLETED";

    /// <summary>Stable audit event code emitted when an event is cancelled.</summary>
    public const string AuditCancelled = "UPDATE.CANCELLED";

    /// <summary>Creates a new event in Planned state.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created DTO on success.</returns>
    Task<Result<SystemUpdateEventDto>> CreateAsync(
        SystemUpdateEventCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Dispatches the public notice (Planned → Notified).</summary>
    /// <param name="eventSqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SystemUpdateEventDto>> NotifyAsync(
        string eventSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the deployment as started (Notified → Deploying).</summary>
    /// <param name="eventSqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SystemUpdateEventDto>> StartDeploymentAsync(
        string eventSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the deployment as completed (Deploying → Deployed).</summary>
    /// <param name="eventSqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SystemUpdateEventDto>> CompleteDeploymentAsync(
        string eventSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels an event from any non-terminal state.</summary>
    /// <param name="eventSqid">Sqid-encoded event id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SystemUpdateEventDto>> CancelAsync(
        string eventSqid,
        SystemUpdateEventReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one event by Sqid.</summary>
    /// <param name="eventSqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<SystemUpdateEventDto>> GetByIdAsync(
        string eventSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Lists events (paged + filterable).</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<SystemUpdateEventPageDto>> ListAsync(
        SystemUpdateEventFilterDto filter,
        CancellationToken cancellationToken = default);
}
