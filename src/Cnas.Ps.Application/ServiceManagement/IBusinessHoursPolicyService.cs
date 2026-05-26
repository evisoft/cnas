using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ServiceManagement;

/// <summary>
/// R2501 / TOR PIR 024 — admin-facing registry for the
/// <c>BusinessHoursPolicy</c> rows. CRUD + Activate/Deactivate + business-time
/// helpers (<see cref="IsBusinessTimeAsync"/> /
/// <see cref="AddBusinessDaysAsync"/>) consumed by the maintenance-window
/// notice-lead-time enforcement.
/// </summary>
public interface IBusinessHoursPolicyService
{
    /// <summary>Stable audit event code emitted when a policy is created.</summary>
    public const string AuditPolicyCreated = "BUSINESS_HOURS.POLICY_CREATED";

    /// <summary>Stable audit event code emitted when a policy is modified.</summary>
    public const string AuditPolicyModified = "BUSINESS_HOURS.POLICY_MODIFIED";

    /// <summary>Stable audit event code emitted when a policy transitions state.</summary>
    public const string AuditPolicyTransitioned = "BUSINESS_HOURS.POLICY_TRANSITIONED";

    /// <summary>Creates a new business-hours policy.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created DTO on success.</returns>
    Task<Result<BusinessHoursPolicyDto>> CreateAsync(
        BusinessHoursPolicyCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Modifies an existing policy.</summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<BusinessHoursPolicyDto>> ModifyAsync(
        string policySqid,
        BusinessHoursPolicyModifyInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Activates a policy.</summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<BusinessHoursPolicyDto>> ActivateAsync(
        string policySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Deactivates a policy.</summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<BusinessHoursPolicyDto>> DeactivateAsync(
        string policySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one policy by Sqid.</summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<BusinessHoursPolicyDto>> GetByIdAsync(
        string policySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one policy by its stable code.</summary>
    /// <param name="policyCode">Stable policy code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<BusinessHoursPolicyDto>> GetByCodeAsync(
        string policyCode,
        CancellationToken cancellationToken = default);

    /// <summary>Lists policies (paged + filterable).</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<BusinessHoursPolicyPageDto>> ListAsync(
        BusinessHoursPolicyFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true when <paramref name="utcInstant"/> falls within the policy's
    /// business-hours window AND on a business day per the policy's
    /// <c>BusinessDaysMask</c> and holiday list.
    /// </summary>
    /// <param name="policyCode">Stable policy code.</param>
    /// <param name="utcInstant">Instant to test (UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Boolean result.</returns>
    Task<Result<bool>> IsBusinessTimeAsync(
        string policyCode,
        DateTime utcInstant,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds <paramref name="businessDays"/> business days to
    /// <paramref name="utcInstant"/>, skipping non-business weekdays (per the
    /// policy's <c>BusinessDaysMask</c>) and holiday dates.
    /// </summary>
    /// <param name="policyCode">Stable policy code.</param>
    /// <param name="utcInstant">Starting UTC instant.</param>
    /// <param name="businessDays">Number of business days to add (≥ 0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The shifted UTC instant on success.</returns>
    Task<Result<DateTime>> AddBusinessDaysAsync(
        string policyCode,
        DateTime utcInstant,
        int businessDays,
        CancellationToken cancellationToken = default);
}
