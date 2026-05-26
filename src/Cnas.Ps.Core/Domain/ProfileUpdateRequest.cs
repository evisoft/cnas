namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0362 / TOR UC13 — workflow-driven contributor-profile-update request. Piggybacks on
/// the existing <see cref="ServiceApplication"/> lifecycle: when a Solicitant files a
/// service-request form whose <c>WorkflowCode</c> implies a profile mutation, the intake
/// pipeline inserts one row here; an approval handler later deserialises
/// <see cref="RequestedChangesJson"/> and applies it via
/// <c>IContributorLinkedEntitiesService</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate table.</b> The parent <see cref="ServiceApplication"/> already carries
/// the lifecycle Status, dossier, decision link, and audit trail. This child row narrows
/// the surface to "what specific profile change the citizen asked for and how it landed"
/// — keeping the writer/approver code simple without polluting the generic
/// <c>FormPayloadJson</c> with a free-form mutation envelope.
/// </para>
/// <para>
/// <b>One-to-one with the application.</b> The EF configuration enforces a unique index
/// on <see cref="ServiceApplicationId"/>: a single application carries at most one
/// profile-update sub-request. Callers that want to batch multiple profile changes must
/// submit one application per change so each can be approved or rejected independently.
/// </para>
/// <para>
/// <b>Apply-failure capture.</b> When the approver calls the contributor-side writer and
/// validation fails (e.g. malformed Sqid, civil-status enum mismatch), the row is
/// persisted with <see cref="ProfileUpdateRequestStatus.Failed"/> and the full failure
/// envelope is captured in <see cref="ApplicationErrorJson"/>. This makes the failure
/// audit-discoverable rather than silent — the operator can resubmit a corrected
/// payload.
/// </para>
/// </remarks>
public sealed class ProfileUpdateRequest : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the parent <see cref="ServiceApplication"/>. Carries the Solicitant identity,
    /// status, and dossier-level audit trail; this row is purely the typed sub-payload.
    /// </summary>
    public long ServiceApplicationId { get; set; }

    /// <summary>
    /// FK to the <see cref="InsuredPerson"/> (Contributor) whose profile is being updated.
    /// May differ from the Solicitant on the parent application — a power-of-attorney
    /// flow lets one user submit a profile change for another person they represent.
    /// </summary>
    public long TargetContributorId { get; set; }

    /// <summary>
    /// Discriminator selecting which contributor child-table the change targets. The
    /// approver branches on this to choose the matching
    /// <c>IContributorLinkedEntitiesService</c> method.
    /// </summary>
    public ProfileUpdateRequestType Type { get; set; }

    /// <summary>
    /// Verbatim JSON payload matching the input DTO for <see cref="Type"/> — e.g.
    /// <c>ContributorAddressInputDto</c> JSON for <see cref="ProfileUpdateRequestType.Address"/>.
    /// Stored as <c>text</c> (no length cap) because field-level validation happens at
    /// apply time, not persistence time.
    /// </summary>
    public required string RequestedChangesJson { get; set; }

    /// <summary>Lifecycle state. See <see cref="ProfileUpdateRequestStatus"/>.</summary>
    public ProfileUpdateRequestStatus Status { get; set; } = ProfileUpdateRequestStatus.Pending;

    /// <summary>
    /// Free-text rationale captured when the approver rejects the request. Capped at
    /// 1024 chars by the EF mapping. Null for every status other than
    /// <see cref="ProfileUpdateRequestStatus.Rejected"/>.
    /// </summary>
    public string? RejectionReason { get; set; }

    /// <summary>
    /// UTC instant when the approved change was successfully applied to the child table.
    /// Null while the request is pending / rejected / failed.
    /// </summary>
    public DateTime? AppliedAtUtc { get; set; }

    /// <summary>
    /// Raw <c>UserProfile.Id</c> of the administrator that approved the request. Captured
    /// on transitions into <see cref="ProfileUpdateRequestStatus.Applied"/> and
    /// <see cref="ProfileUpdateRequestStatus.Failed"/> (the latter records who attempted
    /// the apply that failed downstream).
    /// </summary>
    public long? ApprovedByUserId { get; set; }

    /// <summary>
    /// Captured failure envelope when the apply step rejected the change (validation
    /// failure on the contributor-side writer). JSON shape:
    /// <c>{"errorCode": "...", "errorMessage": "..."}</c>. Null when the request is not
    /// in <see cref="ProfileUpdateRequestStatus.Failed"/>.
    /// </summary>
    public string? ApplicationErrorJson { get; set; }
}

/// <summary>
/// R0362 — discriminator selecting which contributor child-table the profile-update
/// request targets. Persisted as <c>int</c>; numeric stability is part of the
/// persistence contract — renumbering is a breaking change.
/// </summary>
public enum ProfileUpdateRequestType
{
    /// <summary>Address change (<c>ContributorAddress</c> child).</summary>
    Address = 0,

    /// <summary>Contact-channel change (<c>ContributorContact</c> child).</summary>
    Contact = 1,

    /// <summary>Civil-status change (<c>ContributorCivilStatus</c> child).</summary>
    CivilStatus = 2,

    /// <summary>Activity-period addition (<c>ContributorActivityPeriod</c> child).</summary>
    Activity = 3,

    /// <summary>Voluntary social-insurance contract change (<c>ContributorSocialInsuranceContract</c> child).</summary>
    SocialInsuranceContract = 4,
}

/// <summary>
/// R0362 — lifecycle state of a <see cref="ProfileUpdateRequest"/>. Transitions:
/// <c>Pending → Approved → Applied | Failed</c>; <c>Pending → Rejected</c>; terminal
/// statuses are immutable.
/// </summary>
public enum ProfileUpdateRequestStatus
{
    /// <summary>Submitted; awaiting approval.</summary>
    Pending = 0,

    /// <summary>Approver said yes; transitional state before the writer runs.</summary>
    Approved = 1,

    /// <summary>Approver said no. <see cref="ProfileUpdateRequest.RejectionReason"/> is populated.</summary>
    Rejected = 2,

    /// <summary>The approved change was successfully applied to the child table.</summary>
    Applied = 3,

    /// <summary>The approver said yes but the apply step failed.
    /// <see cref="ProfileUpdateRequest.ApplicationErrorJson"/> captures the failure.</summary>
    Failed = 4,
}
