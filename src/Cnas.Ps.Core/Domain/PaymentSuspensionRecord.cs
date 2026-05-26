using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1504 / TOR §3.7-E — CNAS-initiated payment-suspension lifecycle record.
/// Each row tracks a single suspend/resume ceremony against a prior benefit decision,
/// holding the reason at suspend time and the reason at resume time so the audit
/// timeline is fully reconstructable from this aggregate alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> A row is created when CNAS suspends payments for a beneficiary
/// (e.g. expired medical certificate, suspected fraud, failure to confirm residency).
/// The companion <c>DecizieSuspendarePlataTemplate</c> mints a <c>Document</c>
/// of <see cref="DocumentKind.Decision"/> in the same transaction (the cref is a
/// string because Core cannot reference Infrastructure templates). When the
/// suspension cause is cleared, <see cref="ResumedAtUtc"/> + <see cref="ResumedByUserId"/>
/// + <see cref="ResumeReason"/> are stamped and the row terminates.
/// </para>
/// <para>
/// <b>Active vs. terminated.</b> <see cref="ResumedAtUtc"/> being <c>null</c> indicates the
/// suspension is still active; the application service guards against double-suspends by
/// rejecting a new <c>SuspendAsync</c> call when an active row already exists for the
/// given decision. Double-resume is rejected at the service layer as well.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — operators reference
/// individual suspensions by Sqid through the admin REST surface
/// (<c>/api/payment-suspensions/{sqid}/resume</c>); the DTO encodes the surrogate id.
/// </para>
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Notice, EventCodePrefix = "PAYMENT_SUSPENSION")]
public sealed class PaymentSuspensionRecord : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the prior <c>ServiceApplication</c> (decision) that the suspension targets.
    /// Maps to <c>ServiceApplications.Id</c> in the database. Required.
    /// </summary>
    public long DecisionId { get; set; }

    /// <summary>UTC instant the suspension was issued by CNAS.</summary>
    public DateTime SuspendedAtUtc { get; set; }

    /// <summary>
    /// Internal id of the CNAS operator that issued the suspension. Captured at write
    /// time for audit attribution (paired with the Sqid-encoded actor on
    /// <see cref="AuditableEntity.CreatedBy"/>).
    /// </summary>
    public long SuspendedByUserId { get; set; }

    /// <summary>
    /// Free-text reason recorded at suspension time. 3-500 characters; validated by
    /// <c>PaymentSuspensionInputValidator</c>. Surfaced verbatim on the rendered
    /// <c>DecizieSuspendarePlataTemplate</c> DOCX.
    /// </summary>
    public string SuspensionReason { get; set; } = string.Empty;

    /// <summary>
    /// UTC instant the suspension was lifted, when applicable. <c>null</c> while
    /// the suspension is still active.
    /// </summary>
    public DateTime? ResumedAtUtc { get; set; }

    /// <summary>
    /// Internal id of the operator that resumed payments. <c>null</c> while still
    /// suspended.
    /// </summary>
    public long? ResumedByUserId { get; set; }

    /// <summary>
    /// Free-text rationale recorded at resume time. 3-500 characters when present.
    /// <c>null</c> while still suspended. Surfaced on the rendered
    /// <c>DispozitieReluareTemplate</c> DOCX.
    /// </summary>
    public string? ResumeReason { get; set; }

    /// <summary>
    /// Optional FK to the suspension <c>Document</c> row (the rendered Decizie).
    /// <c>null</c> when the document was not minted (test paths / legacy rows).
    /// </summary>
    public long? SuspensionDocumentId { get; set; }

    /// <summary>
    /// Optional FK to the resume <c>Document</c> row (the rendered Dispozitie).
    /// <c>null</c> while still suspended or when the document was not minted.
    /// </summary>
    public long? ResumeDocumentId { get; set; }
}
