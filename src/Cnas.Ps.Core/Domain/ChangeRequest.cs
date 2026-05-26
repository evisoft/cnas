using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2505 / TOR PIR 030-033 — change-management aggregate enforcing the
/// rollback / test-environment-validation / signed-code / four-eyes++ approval
/// process. Each row owns its own strict lifecycle (Submitted → InReview →
/// TestEnvValidated → CodeSigned → ApprovedForProd → Deploying → Deployed
/// → optionally RolledBack; Cancelled from any non-terminal state).
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — change requests
/// are surfaced to operators by Sqid.
/// </para>
/// <para>
/// <b>Auto-numbering.</b> <see cref="ChangeNumber"/> is auto-generated as
/// <c>CHG-{year}-{seq:000000}</c> on create.
/// </para>
/// <para>
/// <b>Four-eyes++ separation.</b> The tester (test-env validator), the signer
/// (code-signature recorder), and the approver MUST each be distinct from the
/// requester AND from each other. The service refuses any transition that
/// violates this rule with <c>Result.Failure(ErrorCodes.Conflict, "CHG.SAME_OPERATOR")</c>.
/// </para>
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Critical, EventCodePrefix = "CHANGE_REQUEST")]
public sealed class ChangeRequest : AuditableEntity, IExternalId, IHistoryTracked
{
    /// <summary>Deterministic change-number in the form <c>CHG-{year}-{seq:000000}</c>; unique.</summary>
    public string ChangeNumber { get; set; } = string.Empty;

    /// <summary>Short title (3..256 chars).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Free-form description (50..8000 chars).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Classification — Standard / Normal / Emergency.</summary>
    public ChangeRequestKind Kind { get; set; }

    /// <summary>Lifecycle state — see <see cref="ChangeRequestStatus"/>.</summary>
    public ChangeRequestStatus Status { get; set; }

    /// <summary>Declared risk band — Low / Medium / High.</summary>
    public ChangeRequestRisk Risk { get; set; }

    /// <summary>Free-text list of impacted systems / sub-systems (≤ 1000 chars).</summary>
    public string ImpactedSystems { get; set; } = string.Empty;

    /// <summary>Mandatory non-trivial rollback plan (50..4000 chars).</summary>
    public string RollbackPlan { get; set; } = string.Empty;

    /// <summary>Operator-supplied note recorded when test-env validation is accepted (≤ 2000 chars).</summary>
    public string? TestEnvironmentValidationNote { get; set; }

    /// <summary>User id of the operator that validated the change in the test environment.</summary>
    public int? TestValidatedByUserId { get; set; }

    /// <summary>UTC instant the test-env validation was recorded.</summary>
    public DateTime? TestValidatedAt { get; set; }

    /// <summary>
    /// Opaque reference to the signed artefact (e.g. <c>sha256:abcdef…</c> or a key id).
    /// Populated when <see cref="Status"/> reaches <see cref="ChangeRequestStatus.CodeSigned"/>.
    /// </summary>
    public string? CodeSignatureReference { get; set; }

    /// <summary>User id of the operator that recorded the code signature.</summary>
    public int? CodeSignedByUserId { get; set; }

    /// <summary>UTC instant the code signature was recorded.</summary>
    public DateTime? CodeSignedAt { get; set; }

    /// <summary>User id of the requester (always populated).</summary>
    public int RequestedByUserId { get; set; }

    /// <summary>User id of the production approver.</summary>
    public int? ApprovedByUserId { get; set; }

    /// <summary>UTC instant the change was approved for production.</summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>UTC instant the production deployment completed.</summary>
    public DateTime? DeployedAt { get; set; }

    /// <summary>UTC instant the change was rolled back.</summary>
    public DateTime? RolledBackAt { get; set; }

    /// <summary>Free-form rollback reason (3..2000 chars).</summary>
    public string? RollbackReason { get; set; }

    /// <summary>Free-form cancellation reason (3..500 chars).</summary>
    public string? CancelReason { get; set; }

    /// <summary>Optional FK to a related <see cref="MaintenanceWindow"/> in which the change will be deployed.</summary>
    public long? RelatedMaintenanceWindowId { get; set; }
}
