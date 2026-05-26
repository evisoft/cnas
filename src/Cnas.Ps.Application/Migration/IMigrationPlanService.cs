using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Migration;

/// <summary>
/// R2430 / TOR M4 — façade over the migration-plan registry. Wraps the CRUD
/// + lifecycle-transition surface and emits the appropriate audit rows for
/// every mutation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b>
/// <list type="bullet">
///   <item>Plans are created in <c>Draft</c>.</item>
///   <item><c>Draft</c> → <c>Approved</c> via <see cref="SubmitForApprovalAsync"/> followed by <see cref="ApproveAsync"/> by a different admin.</item>
///   <item><c>Approved</c> → <c>Active</c> via <see cref="ActivateAsync"/>.</item>
///   <item><c>Active</c> ↔ <c>Suspended</c> via <see cref="SuspendAsync"/> / <see cref="ActivateAsync"/>.</item>
///   <item>Any non-terminal state → <c>Archived</c> via <see cref="ArchiveAsync"/>.</item>
/// </list>
/// </para>
/// </remarks>
public interface IMigrationPlanService
{
    /// <summary>Stable audit code emitted when a plan is created.</summary>
    public const string AuditPlanCreated = "MIGRATION.PLAN_CREATED";

    /// <summary>Stable audit code emitted when a plan is modified.</summary>
    public const string AuditPlanModified = "MIGRATION.PLAN_MODIFIED";

    /// <summary>Stable audit code emitted when a plan transitions through the lifecycle.</summary>
    public const string AuditPlanTransitioned = "MIGRATION.PLAN_TRANSITIONED";

    /// <summary>Stable failure code returned when a transition is not allowed from the current state.</summary>
    public const string InvalidTransitionCode = "MIGRATION.INVALID_TRANSITION";

    /// <summary>Stable failure code returned when the plan-code natural key already exists.</summary>
    public const string DuplicatePlanCodeCode = "MIGRATION.DUPLICATE_PLAN_CODE";

    /// <summary>Creates a new Draft plan.</summary>
    /// <param name="input">Plan-creation input.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The created plan DTO on success.</returns>
    Task<Result<MigrationPlanDto>> CreateAsync(
        MigrationPlanCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Modifies an existing Draft plan.</summary>
    /// <param name="planSqid">Sqid-encoded plan id.</param>
    /// <param name="input">Modification input.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<MigrationPlanDto>> ModifyAsync(
        string planSqid,
        MigrationPlanModifyInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Transitions a plan Draft → Approved (no-op marker; second admin still needs to <see cref="ApproveAsync"/>).</summary>
    /// <param name="planSqid">Sqid-encoded plan id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<MigrationPlanDto>> SubmitForApprovalAsync(
        string planSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Approves a Draft plan (transitions to Approved).</summary>
    /// <param name="planSqid">Sqid-encoded plan id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<MigrationPlanDto>> ApproveAsync(
        string planSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Activates an Approved or Suspended plan.</summary>
    /// <param name="planSqid">Sqid-encoded plan id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<MigrationPlanDto>> ActivateAsync(
        string planSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Suspends an Active plan.</summary>
    /// <param name="planSqid">Sqid-encoded plan id.</param>
    /// <param name="reason">Suspension reason envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<MigrationPlanDto>> SuspendAsync(
        string planSqid,
        MigrationPlanReasonInputDto reason,
        CancellationToken cancellationToken = default);

    /// <summary>Archives a plan (terminal state).</summary>
    /// <param name="planSqid">Sqid-encoded plan id.</param>
    /// <param name="reason">Archive reason envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<MigrationPlanDto>> ArchiveAsync(
        string planSqid,
        MigrationPlanReasonInputDto reason,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a plan by Sqid.</summary>
    /// <param name="planSqid">Sqid-encoded plan id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The plan DTO on success.</returns>
    Task<Result<MigrationPlanDto>> GetByIdAsync(
        string planSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a plan by its stable code.</summary>
    /// <param name="planCode">Plan code (SCREAMING_SNAKE_CASE).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The plan DTO on success.</returns>
    Task<Result<MigrationPlanDto>> GetByCodeAsync(
        string planCode,
        CancellationToken cancellationToken = default);

    /// <summary>Lists plans matching <paramref name="filter"/>.</summary>
    /// <param name="filter">Filter + paging envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Page DTO on success.</returns>
    Task<Result<MigrationPlanPageDto>> ListAsync(
        MigrationPlanFilterDto filter,
        CancellationToken cancellationToken = default);
}
