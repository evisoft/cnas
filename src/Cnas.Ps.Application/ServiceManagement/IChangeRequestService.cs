using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ServiceManagement;

/// <summary>
/// R2505 / TOR PIR 030-033 — admin-facing service over the change-management
/// aggregate. Owns the lifecycle Draft → Submitted → InReview →
/// TestEnvValidated → CodeSigned → ApprovedForProd → Deploying → Deployed,
/// optionally RolledBack from Deploying / Deployed, or Cancelled from any
/// non-terminal state. Enforces four-eyes++ separation between requester,
/// tester, signer, and approver.
/// </summary>
public interface IChangeRequestService
{
    /// <summary>Stable audit event code emitted when a change is created.</summary>
    public const string AuditCreated = "CHG.CREATED";

    /// <summary>Stable audit event code emitted when a change is submitted.</summary>
    public const string AuditSubmitted = "CHG.SUBMITTED";

    /// <summary>Stable audit event code emitted when review begins.</summary>
    public const string AuditReviewStarted = "CHG.REVIEW_STARTED";

    /// <summary>Stable audit event code emitted when test-env validation is recorded.</summary>
    public const string AuditTestEnvValidated = "CHG.TEST_ENV_VALIDATED";

    /// <summary>Stable audit event code emitted when the code signature is recorded.</summary>
    public const string AuditCodeSigned = "CHG.CODE_SIGNED";

    /// <summary>Stable audit event code emitted when the change is approved for prod.</summary>
    public const string AuditApprovedForProd = "CHG.APPROVED_FOR_PROD";

    /// <summary>Stable audit event code emitted when the deployment starts.</summary>
    public const string AuditDeploymentStarted = "CHG.DEPLOYMENT_STARTED";

    /// <summary>Stable audit event code emitted when the deployment completes.</summary>
    public const string AuditDeploymentCompleted = "CHG.DEPLOYMENT_COMPLETED";

    /// <summary>Stable audit event code emitted when the change is rolled back.</summary>
    public const string AuditRolledBack = "CHG.ROLLED_BACK";

    /// <summary>Stable audit event code emitted when the change is cancelled.</summary>
    public const string AuditCancelled = "CHG.CANCELLED";

    /// <summary>Creates a new change request in Draft.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created DTO on success.</returns>
    Task<Result<ChangeRequestDto>> CreateAsync(
        ChangeRequestCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Submits a Draft change request (Draft → Submitted).</summary>
    /// <param name="changeSqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ChangeRequestDto>> SubmitAsync(
        string changeSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Starts technical review (Submitted → InReview).</summary>
    /// <param name="changeSqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ChangeRequestDto>> StartReviewAsync(
        string changeSqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the test-env validation (InReview → TestEnvValidated). The
    /// validator MUST be a different user from the requester.
    /// </summary>
    /// <param name="changeSqid">Sqid-encoded change id.</param>
    /// <param name="input">Validation-note payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ChangeRequestDto>> ValidateTestEnvAsync(
        string changeSqid,
        ChangeRequestTestValidationInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the code signature (TestEnvValidated → CodeSigned). The
    /// signer MUST be a different user from the requester AND the
    /// test-env validator.
    /// </summary>
    /// <param name="changeSqid">Sqid-encoded change id.</param>
    /// <param name="input">Code-signature payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ChangeRequestDto>> SignCodeAsync(
        string changeSqid,
        ChangeRequestSignCodeInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves the change for production (CodeSigned → ApprovedForProd). The
    /// approver MUST differ from requester / tester / signer.
    /// </summary>
    /// <param name="changeSqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ChangeRequestDto>> ApproveAsync(
        string changeSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Starts the production deployment (ApprovedForProd → Deploying).</summary>
    /// <param name="changeSqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ChangeRequestDto>> StartDeploymentAsync(
        string changeSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Completes the production deployment (Deploying → Deployed).</summary>
    /// <param name="changeSqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ChangeRequestDto>> CompleteDeploymentAsync(
        string changeSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Rolls back the change (Deploying / Deployed → RolledBack).</summary>
    /// <param name="changeSqid">Sqid-encoded change id.</param>
    /// <param name="input">Rollback-reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ChangeRequestDto>> RollBackAsync(
        string changeSqid,
        ChangeRequestRollbackInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels the change from any non-terminal state.</summary>
    /// <param name="changeSqid">Sqid-encoded change id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ChangeRequestDto>> CancelAsync(
        string changeSqid,
        ChangeRequestReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a change request by Sqid.</summary>
    /// <param name="changeSqid">Sqid-encoded change id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<ChangeRequestDto>> GetByIdAsync(
        string changeSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Lists change requests (paged + filterable).</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<ChangeRequestPageDto>> ListAsync(
        ChangeRequestFilterDto filter,
        CancellationToken cancellationToken = default);
}
