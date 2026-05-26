using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// UC08 — Examine document and route a dossier through the examiner workflow.
/// Used by CNAS case workers (group <c>cnas-examiner</c>) to record per-document
/// verdicts, request system-generated draft documents (Fișa de calcul, Decizia),
/// and either forward the dossier to the șef-direcție for approval or reject the
/// application outright.
/// </summary>
public interface IDocumentExaminationService
{
    /// <summary>
    /// UC08.02 — Records the examiner's verdict for an attached document
    /// (accepted / rejected / pending).
    /// </summary>
    /// <param name="documentId">Sqid-encoded document id.</param>
    /// <param name="verdict">The examiner's verdict.</param>
    /// <param name="note">Optional free-text note attached to the verdict.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success;
    /// <see cref="ErrorCodes.InvalidSqid"/> when <paramref name="documentId"/> is malformed;
    /// <see cref="ErrorCodes.NotFound"/> when the document does not exist or is inactive.
    /// </returns>
    Task<Result> RecordVerdictAsync(
        string documentId,
        ExaminationVerdict verdict,
        string? note,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// UC08.04 — System auto-generates draft Fișa de calcul and Decizia documents
    /// for the dossier and returns the Sqid identifiers of the two newly-created
    /// <c>Document</c> rows.
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the pair of Sqid identifiers;
    /// <see cref="ErrorCodes.InvalidSqid"/> for a malformed id;
    /// <see cref="ErrorCodes.NotFound"/> when the dossier does not exist or is inactive.
    /// </returns>
    Task<Result<DraftDocumentsResult>> GenerateDraftsAsync(
        string dossierId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// UC08.06 — Examiner forwards the dossier (with its drafts) to the șef-direcție for
    /// final approval (UC10). Flips the application to <c>PendingApproval</c>, closes the
    /// examiner workflow task and opens a new decider task.
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success;
    /// <see cref="ErrorCodes.Forbidden"/> when the caller is not the assigned examiner;
    /// <see cref="ErrorCodes.NotFound"/> when the dossier does not exist or is inactive.
    /// </returns>
    Task<Result> SubmitForApprovalAsync(
        string dossierId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// UC08.06 (alt branch) — Examiner refuses the application outright without sending it for
    /// approval. Marks the application <c>Rejected</c>, stamps the dossier <c>ClosedAtUtc</c>,
    /// and cancels every open workflow task on the dossier.
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="reason">Mandatory human-readable reason recorded on the audit + notification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on success;
    /// <see cref="ErrorCodes.Forbidden"/> when the caller is not the assigned examiner;
    /// <see cref="ErrorCodes.NotFound"/> when the dossier does not exist or is inactive.
    /// </returns>
    Task<Result> RefuseAsync(
        string dossierId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// UC08.05 (R0573) — Examiner emits a brand-new decision document anchored to
    /// the dossier. Distinct from <see cref="GenerateDraftsAsync"/> (which emits the
    /// canonical Fișa+Decizia pair on dossier creation) and from
    /// <see cref="RecordVerdictAsync"/> (which mutates an existing attached
    /// document). The examiner picks the Annex 7 template by its stable
    /// kebab-case code; the service:
    /// <list type="bullet">
    ///   <item>verifies the dossier exists and the parent application is in an
    ///         editable status (not Approved / Rejected / Closed / Withdrawn) —
    ///         otherwise returns <see cref="ErrorCodes.ExaminationNotEditable"/>;</item>
    ///   <item>verifies the requested template code matches one of the registered
    ///         <c>IDocxTemplate</c> implementations — otherwise returns
    ///         <see cref="ErrorCodes.DocumentTemplateNotFound"/>;</item>
    ///   <item>delegates the actual render + persist + audit to
    ///         <see cref="IDocumentGenerationService.GenerateDecisionAsync(string, CancellationToken)"/>;</item>
    ///   <item>emits an additional <c>EXAMINATION.NEW_DECISION_EMITTED</c> audit
    ///         row at <c>AuditSeverity.Notice</c> level with the chosen template
    ///         code + optional note;</item>
    ///   <item>fires the
    ///         <see cref="Cnas.Ps.Application.Notifications.NotificationTriggerKind.ActionResult"/>
    ///         canonical trigger so the solicitant is notified.</item>
    /// </list>
    /// </summary>
    /// <param name="examinationSqid">Sqid-encoded dossier id.</param>
    /// <param name="input">Template code + optional note + optional override amount.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping an
    /// <see cref="EmittedDecisionDto"/> on success; one of
    /// <see cref="ErrorCodes.InvalidSqid"/>, <see cref="ErrorCodes.NotFound"/>,
    /// <see cref="ErrorCodes.Forbidden"/>,
    /// <see cref="ErrorCodes.ExaminationNotEditable"/>,
    /// <see cref="ErrorCodes.DocumentTemplateNotFound"/>, or any code propagated
    /// by the underlying generation service on failure.
    /// </returns>
    Task<Result<EmittedDecisionDto>> EmitNewDecisionAsync(
        string examinationSqid,
        EmitNewDecisionInputDto input,
        CancellationToken cancellationToken = default);
}

/// <summary>Verdict an examiner can record on an individual document.</summary>
public enum ExaminationVerdict
{
    /// <summary>The document is accepted as supplied.</summary>
    Accepted,

    /// <summary>The document is rejected and must be resupplied.</summary>
    Rejected,

    /// <summary>The document is held pending more information.</summary>
    Held,
}

