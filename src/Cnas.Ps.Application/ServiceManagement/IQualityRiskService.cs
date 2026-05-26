using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ServiceManagement;

/// <summary>
/// R2506 / TOR PIR 037-040 — admin-facing service over the quality-risk
/// registry. Owns the lifecycle Open → Mitigating → Closed (or Accepted),
/// manages linked preventive actions, and exposes the annual-review query
/// consumed by <c>QualityRiskReviewSweepJob</c>.
/// </summary>
public interface IQualityRiskService
{
    /// <summary>Stable audit event code emitted when a risk is created.</summary>
    public const string AuditRiskCreated = "QA_RISK.CREATED";

    /// <summary>Stable audit event code emitted when a risk is modified.</summary>
    public const string AuditRiskModified = "QA_RISK.MODIFIED";

    /// <summary>Stable audit event code emitted when a risk is closed.</summary>
    public const string AuditRiskClosed = "QA_RISK.CLOSED";

    /// <summary>Stable audit event code emitted when a risk is marked Mitigating.</summary>
    public const string AuditRiskMitigating = "QA_RISK.MITIGATING";

    /// <summary>Stable audit event code emitted when a risk is formally accepted.</summary>
    public const string AuditRiskAccepted = "QA_RISK.ACCEPTED";

    /// <summary>Stable audit event code emitted when a review is recorded.</summary>
    public const string AuditRiskReviewed = "QA_RISK.REVIEWED";

    /// <summary>Stable audit event code emitted when a review is overdue (job-emitted).</summary>
    public const string AuditRiskReviewOverdue = "QA_RISK.REVIEW_OVERDUE";

    /// <summary>Stable audit event code emitted when an action is added.</summary>
    public const string AuditActionAdded = "QA_RISK.ACTION_ADDED";

    /// <summary>Stable audit event code emitted when an action is modified.</summary>
    public const string AuditActionModified = "QA_RISK.ACTION_MODIFIED";

    /// <summary>Stable audit event code emitted when an action transitions InProgress.</summary>
    public const string AuditActionInProgress = "QA_RISK.ACTION_IN_PROGRESS";

    /// <summary>Stable audit event code emitted when an action is marked Implemented.</summary>
    public const string AuditActionImplemented = "QA_RISK.ACTION_IMPLEMENTED";

    /// <summary>Stable audit event code emitted when an action is cancelled.</summary>
    public const string AuditActionCancelled = "QA_RISK.ACTION_CANCELLED";

    /// <summary>Default annual-review window in days (365).</summary>
    public const int DefaultReviewWindowDays = 365;

    /// <summary>Creates a new quality risk in Open state.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created DTO on success.</returns>
    Task<Result<QualityRiskDto>> CreateRiskAsync(
        QualityRiskCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Modifies an existing quality risk.</summary>
    /// <param name="riskSqid">Sqid-encoded risk id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<QualityRiskDto>> ModifyRiskAsync(
        string riskSqid,
        QualityRiskModifyInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Closes an open or mitigating risk.</summary>
    /// <param name="riskSqid">Sqid-encoded risk id.</param>
    /// <param name="input">Closure-reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<QualityRiskDto>> CloseRiskAsync(
        string riskSqid,
        QualityRiskReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Flips an Open risk to Mitigating.</summary>
    /// <param name="riskSqid">Sqid-encoded risk id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<QualityRiskDto>> MarkMitigatingAsync(
        string riskSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Formally accepts the risk (Open / Mitigating → Accepted).</summary>
    /// <param name="riskSqid">Sqid-encoded risk id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<QualityRiskDto>> AcceptRiskAsync(
        string riskSqid,
        QualityRiskReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a periodic review. The caller MUST be the risk owner or a
    /// <c>cnas-admin</c> role-holder.
    /// </summary>
    /// <param name="riskSqid">Sqid-encoded risk id.</param>
    /// <param name="input">Review payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<QualityRiskDto>> RecordReviewAsync(
        string riskSqid,
        QualityRiskReviewInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a preventive action to a risk.</summary>
    /// <param name="riskSqid">Sqid-encoded risk id.</param>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created DTO on success.</returns>
    Task<Result<QualityRiskActionDto>> AddPreventiveActionAsync(
        string riskSqid,
        QualityRiskActionCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Modifies a preventive action.</summary>
    /// <param name="actionSqid">Sqid-encoded action id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<QualityRiskActionDto>> ModifyPreventiveActionAsync(
        string actionSqid,
        QualityRiskActionModifyInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Marks an action InProgress (Planned → InProgress).</summary>
    /// <param name="actionSqid">Sqid-encoded action id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<QualityRiskActionDto>> MarkActionInProgressAsync(
        string actionSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Marks an action Implemented (InProgress → Implemented).</summary>
    /// <param name="actionSqid">Sqid-encoded action id.</param>
    /// <param name="input">Completion-note payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<QualityRiskActionDto>> MarkActionImplementedAsync(
        string actionSqid,
        QualityRiskActionImplementInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels an action from a non-terminal state.</summary>
    /// <param name="actionSqid">Sqid-encoded action id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<QualityRiskActionDto>> CancelActionAsync(
        string actionSqid,
        QualityRiskActionReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a risk by Sqid.</summary>
    /// <param name="riskSqid">Sqid-encoded risk id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<QualityRiskDto>> GetRiskByIdAsync(
        string riskSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Lists risks (paged + filterable).</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<QualityRiskPageDto>> ListAsync(
        QualityRiskFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns risks where <c>LastReviewedAt</c> is null OR older than
    /// <paramref name="sinceDays"/> days. Consumed by the annual-review
    /// sweep job.
    /// </summary>
    /// <param name="sinceDays">Review-window size in days (default 365).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Lightweight projections of every overdue risk.</returns>
    Task<Result<IReadOnlyList<QualityRiskDto>>> ListOverdueForReviewAsync(
        int sinceDays = DefaultReviewWindowDays,
        CancellationToken cancellationToken = default);
}
