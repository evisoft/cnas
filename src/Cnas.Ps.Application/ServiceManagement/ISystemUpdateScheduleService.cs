using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ServiceManagement;

/// <summary>
/// R2503 / TOR PIR 022-023 — admin-facing registry over the
/// <c>SystemUpdateSchedule</c> rows. CRUD + Activate / Deactivate + lookup +
/// list.
/// </summary>
public interface ISystemUpdateScheduleService
{
    /// <summary>Stable audit event code emitted when a schedule is created.</summary>
    public const string AuditCreated = "UPDATE.SCHEDULE.CREATED";

    /// <summary>Stable audit event code emitted when a schedule is modified.</summary>
    public const string AuditModified = "UPDATE.SCHEDULE.MODIFIED";

    /// <summary>Stable audit event code emitted when a schedule transitions state.</summary>
    public const string AuditTransitioned = "UPDATE.SCHEDULE.TRANSITIONED";

    /// <summary>Creates a new schedule.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created DTO on success.</returns>
    Task<Result<SystemUpdateScheduleDto>> CreateAsync(
        SystemUpdateScheduleCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Modifies an existing schedule.</summary>
    /// <param name="scheduleSqid">Sqid-encoded schedule id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SystemUpdateScheduleDto>> ModifyAsync(
        string scheduleSqid,
        SystemUpdateScheduleModifyInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Activates a schedule.</summary>
    /// <param name="scheduleSqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SystemUpdateScheduleDto>> ActivateAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Deactivates a schedule.</summary>
    /// <param name="scheduleSqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SystemUpdateScheduleDto>> DeactivateAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a schedule by Sqid.</summary>
    /// <param name="scheduleSqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<SystemUpdateScheduleDto>> GetByIdAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a schedule by its stable code.</summary>
    /// <param name="scheduleCode">Stable schedule code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<SystemUpdateScheduleDto>> GetByCodeAsync(
        string scheduleCode,
        CancellationToken cancellationToken = default);

    /// <summary>Lists schedules (paged + filterable).</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<SystemUpdateSchedulePageDto>> ListAsync(
        SystemUpdateScheduleFilterDto filter,
        CancellationToken cancellationToken = default);
}
