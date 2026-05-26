using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — admin-facing registry for
/// <c>BackupPolicy</c> rows. Hosts CRUD + the
/// <c>Active</c> ↔ <c>Inactive</c> ↔ <c>Archived</c> lifecycle. Soft-deletes
/// only (the row sticks around with <c>IsActive=false</c> so historical
/// <c>BackupRun</c> entries keep their FK target).
/// </summary>
public interface IBackupPolicyService
{
    /// <summary>Stable failure code: a policy with the same <c>PolicyCode</c> already exists.</summary>
    public const string DuplicatePolicyCodeCode = "BACKUP.DUPLICATE_POLICY_CODE";

    /// <summary>Stable failure code: the requested transition is not legal for the policy's current state.</summary>
    public const string InvalidTransitionCode = "BACKUP.INVALID_TRANSITION";

    /// <summary>Stable audit event code emitted when a policy is created.</summary>
    public const string AuditPolicyCreated = "BACKUP.POLICY_CREATED";

    /// <summary>Stable audit event code emitted when a policy is modified.</summary>
    public const string AuditPolicyModified = "BACKUP.POLICY_MODIFIED";

    /// <summary>Stable audit event code emitted when a policy transitions state.</summary>
    public const string AuditPolicyTransitioned = "BACKUP.POLICY_TRANSITIONED";

    /// <summary>Creates a new backup policy.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created DTO on success.</returns>
    Task<Result<BackupPolicyDto>> CreateAsync(
        BackupPolicyCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Modifies an existing non-Archived policy.</summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<BackupPolicyDto>> ModifyAsync(
        string policySqid,
        BackupPolicyModifyInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Activates a policy (Inactive → Active).</summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<BackupPolicyDto>> ActivateAsync(
        string policySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Deactivates a policy (Active → Inactive).</summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<BackupPolicyDto>> DeactivateAsync(
        string policySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Archives (soft-deletes) the policy with a reason.</summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="reason">Transition reason payload (3..1000 chars).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<BackupPolicyDto>> ArchiveAsync(
        string policySqid,
        BackupPolicyReasonInputDto reason,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one policy by Sqid.</summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<BackupPolicyDto>> GetByIdAsync(
        string policySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one policy by its stable <c>PolicyCode</c>.</summary>
    /// <param name="policyCode">Stable policy code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<BackupPolicyDto>> GetByCodeAsync(
        string policyCode,
        CancellationToken cancellationToken = default);

    /// <summary>Lists policies (paged + filterable).</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<BackupPolicyPageDto>> ListAsync(
        BackupPolicyFilterDto filter,
        CancellationToken cancellationToken = default);
}
