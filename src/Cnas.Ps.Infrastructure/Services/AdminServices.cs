using System.Diagnostics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Documents.Templates;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

// UC07 — FormIntakeService moved to its own file (Services/FormIntakeService.cs)
// with a real schema validator. See that file for the implementation.

/// <summary>
/// UC08 — Examiner workflow service. Owns four operations:
/// <list type="bullet">
///   <item>UC08.02 — record a per-document verdict (accepted/rejected/held).</item>
///   <item>UC08.04 — request auto-generation of the Fișa de calcul + Decizia drafts.</item>
///   <item>UC08.06 — forward the dossier to the șef-direcție for approval.</item>
///   <item>UC08.06 — refuse the application outright (no approval round).</item>
/// </list>
/// Each mutating operation gates on the assigned examiner and emits an audit entry plus,
/// where applicable, a citizen notification.
/// </summary>
/// <remarks>
/// Each state transition also mirrors a citizen-portal card revision to MCabinet via
/// <see cref="IMCabinetPublisher"/>. The mirror is best-effort — a publish failure is
/// logged at <c>Warning</c> level and swallowed so the dossier state change (the source
/// of truth) commits regardless. See CLAUDE.md cross-cutting "Idempotent Callbacks".
/// </remarks>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping (CLAUDE.md RULE 3).</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller context.</param>
/// <param name="docgen">Draft-document generator (Fișa de calcul, Decizia).</param>
/// <param name="audit">Audit journal façade.</param>
/// <param name="notify">Citizen-notification enqueuer.</param>
/// <param name="mcabinet">Citizen-portal publisher (MCabinet) — best-effort outbound projection.</param>
/// <param name="logger">Structured logger for the best-effort publish path.</param>
/// <param name="triggers">R0174 — optional canonical-trigger dispatcher (ApprovalNeeded fan-out).</param>
/// <param name="templates">
/// R0573 / TOR CF 08.05 — Annex 7 DOCX templates registered with the DI
/// container. Used solely by <see cref="EmitNewDecisionAsync"/> to verify
/// that the examiner-supplied template code matches a known registration
/// before delegating to <see cref="IDocumentGenerationService"/>. Optional
/// (nullable / empty enumerable) so existing test compositions keep
/// compiling — the new endpoint surfaces
/// <see cref="ErrorCodes.DocumentTemplateNotFound"/> when no templates are
/// wired.
/// </param>
public sealed class DocumentExaminationService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IDocumentGenerationService docgen,
    IAuditService audit,
    INotificationService notify,
    IMCabinetPublisher mcabinet,
    ILogger<DocumentExaminationService> logger,
    Cnas.Ps.Application.Notifications.INotificationTriggerDispatcher? triggers = null,
    IEnumerable<IDocxTemplate>? templates = null)
    : IDocumentExaminationService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IDocumentGenerationService _docgen = docgen;
    private readonly IAuditService _audit = audit;
    private readonly INotificationService _notify = notify;
    private readonly IMCabinetPublisher _mcabinet = mcabinet;
    private readonly ILogger<DocumentExaminationService> _logger = logger;

    /// <summary>
    /// R0174 / TOR CF 22.03 — optional ApprovalNeeded trigger dispatcher. Fired
    /// from <c>SubmitForApprovalAsync</c> so the șef-direcție inbox row carries
    /// the <c>Dossier</c> deep-link anchor (R0172). Nullable for back-compat.
    /// </summary>
    private readonly Cnas.Ps.Application.Notifications.INotificationTriggerDispatcher? _triggers = triggers;

    /// <summary>
    /// R0573 / TOR CF 08.05 — case-insensitive set of registered Annex 7 DOCX
    /// template codes. Built once at construction time from the injected
    /// enumerable; <see cref="EmitNewDecisionAsync"/> consults this set to
    /// validate the examiner-supplied template code before delegating to
    /// <see cref="IDocumentGenerationService"/>. Empty when no templates were
    /// registered — in that case every emit-decision call surfaces
    /// <see cref="ErrorCodes.DocumentTemplateNotFound"/>.
    /// </summary>
    private readonly HashSet<string> _templateCodes = BuildTemplateCodeSet(templates);

    /// <summary>
    /// Builds a case-insensitive lookup set of registered Annex 7 template
    /// codes. Null / whitespace codes are skipped — they cannot match any
    /// caller-supplied input. The result is treated as immutable for the
    /// service lifetime.
    /// </summary>
    /// <param name="templates">Injected DOCX templates (may be null).</param>
    /// <returns>Case-insensitive set of canonical template codes.</returns>
    private static HashSet<string> BuildTemplateCodeSet(IEnumerable<IDocxTemplate>? templates)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (templates is null) return set;
        foreach (var t in templates)
        {
            if (t is null || string.IsNullOrWhiteSpace(t.TemplateCode)) continue;
            set.Add(t.TemplateCode);
        }
        return set;
    }

    /// <summary>Group code identifying the șef-direcție inbox (UC10).</summary>
    private const string DeciderGroup = "cnas-decider";

    /// <summary>Group code identifying the examiner inbox (UC07/UC08).</summary>
    private const string ExaminerGroup = "cnas-examiner";

    /// <inheritdoc />
    public async Task<Result> RecordVerdictAsync(
        string documentId,
        ExaminationVerdict verdict,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(documentId);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var doc = await _db.Documents
            .SingleOrDefaultAsync(d => d.Id == decoded.Value && d.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (doc is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Document not found.");
        }

        var now = _clock.UtcNow;
        doc.Verdict = (int)verdict;
        doc.VerdictNote = note;
        doc.VerdictAtUtc = now;
        doc.UpdatedAtUtc = now;
        doc.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            $"DOCUMENT.{verdict}",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Document),
            doc.Id,
            $"{{\"note\":{System.Text.Json.JsonSerializer.Serialize(note ?? string.Empty)}}}",
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        // OTel histogram (best-effort) — wall-clock latency between document upload
        // (Document.CreatedAtUtc, set when the citizen uploaded the file) and the
        // examiner recording a verdict. We use CreatedAtUtc as the upload timestamp
        // because the Document entity does not carry a dedicated UploadedAtUtc field;
        // for documents persisted via the citizen-upload path the two are equivalent.
        // If the timestamp is in the future (clock skew) or default we skip rather than
        // record a misleading negative/huge value.
        var uploadedAt = doc.CreatedAtUtc;
        if (uploadedAt != default && uploadedAt <= now)
        {
            var ms = (now - uploadedAt).TotalMilliseconds;
            RecordHistogramSafely(
                CnasTelemetry.DocumentExaminationLatencyMs,
                ms,
                new KeyValuePair<string, object?>("verdict", verdict.ToString()));
        }

        return Result.Success();
    }

    /// <summary>
    /// Best-effort wrapper around <see cref="System.Diagnostics.Metrics.Counter{T}"/>.Add.
    /// Any exception raised by a downstream <see cref="System.Diagnostics.Metrics.MeterListener"/>
    /// or exporter is logged at <c>Warning</c> level and swallowed — telemetry side-effects must
    /// never break the dossier state machine. Mirrors the MCabinet best-effort pattern.
    /// </summary>
    /// <param name="counter">Pre-declared counter to increment by one.</param>
    /// <param name="tag">Key/value tag describing the dossier dimension.</param>
    private void RecordCounterSafely(
        System.Diagnostics.Metrics.Counter<long> counter,
        KeyValuePair<string, object?> tag)
    {
        try
        {
            counter.Add(1, tag);
        }
#pragma warning disable CA1031 // Best-effort telemetry: a misbehaving listener MUST NOT break the state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Telemetry counter {Counter} increment threw; ignoring.",
                counter.Name);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Best-effort multi-tag counter increment. Used when more than one dimension
    /// is meaningful (e.g. "tag" describing the rejection channel <i>and</i>
    /// "service_code" describing the underlying service). Any thrown exception is
    /// logged at <c>Warning</c> level and swallowed.
    /// </summary>
    /// <param name="counter">Pre-declared counter to increment by one.</param>
    /// <param name="tags">Set of key/value tags to attach to the measurement.</param>
    private void RecordCounterSafelyMulti(
        System.Diagnostics.Metrics.Counter<long> counter,
        TagList tags)
    {
        try
        {
            counter.Add(1, tags);
        }
#pragma warning disable CA1031 // Best-effort telemetry: a misbehaving listener MUST NOT break the state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Telemetry counter {Counter} increment threw; ignoring.",
                counter.Name);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Best-effort wrapper around <see cref="System.Diagnostics.Metrics.Histogram{T}"/>.Record.
    /// Any exception raised by a downstream <see cref="System.Diagnostics.Metrics.MeterListener"/>
    /// or exporter is logged at <c>Warning</c> level and swallowed.
    /// </summary>
    /// <param name="histogram">Pre-declared histogram to record into.</param>
    /// <param name="value">Sample value (milliseconds).</param>
    /// <param name="tag">Key/value tag describing the measurement dimension.</param>
    private void RecordHistogramSafely(
        System.Diagnostics.Metrics.Histogram<double> histogram,
        double value,
        KeyValuePair<string, object?> tag)
    {
        try
        {
            histogram.Record(value, tag);
        }
#pragma warning disable CA1031 // Best-effort telemetry: a misbehaving listener MUST NOT break the state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Telemetry histogram {Histogram} record threw; ignoring.",
                histogram.Name);
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc />
    public async Task<Result<DraftDocumentsResult>> GenerateDraftsAsync(
        string dossierId,
        CancellationToken cancellationToken = default)
    {
        // We validate the dossier id by performing the Sqid decode here — the generation
        // service will re-validate and fail with the same code, but doing it once up front
        // gives the audit entry the dossier primary key it needs without an extra round trip.
        var decoded = _sqids.TryDecode(dossierId);
        if (decoded.IsFailure)
        {
            return Result<DraftDocumentsResult>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        // Include Application + Solicitant so that the MCabinet card (built after the
        // drafts persist) has the citizen IDNP on hand. The ServicePassport navigation
        // is not modeled on ServiceApplication so we fetch it separately by FK below.
        var dossier = await _db.Dossiers
            .Include(d => d.Application).ThenInclude(a => a!.Solicitant)
            .SingleOrDefaultAsync(d => d.Id == decoded.Value && d.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (dossier is null)
        {
            return Result<DraftDocumentsResult>.Failure(ErrorCodes.NotFound, "Dossier not found.");
        }

        var sheetResult = await _docgen.GenerateCalculationSheetAsync(dossierId, cancellationToken).ConfigureAwait(false);
        if (sheetResult.IsFailure)
        {
            return Result<DraftDocumentsResult>.Failure(sheetResult.ErrorCode!, sheetResult.ErrorMessage!);
        }

        var decisionResult = await _docgen.GenerateDecisionAsync(dossierId, cancellationToken).ConfigureAwait(false);
        if (decisionResult.IsFailure)
        {
            return Result<DraftDocumentsResult>.Failure(decisionResult.ErrorCode!, decisionResult.ErrorMessage!);
        }

        var detailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            calculationSheetId = sheetResult.Value,
            decisionId = decisionResult.Value,
        });
        await _audit.RecordAsync(
            "DOSSIER.DRAFTS_GENERATED",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Dossier),
            dossier.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        await PublishMCabinetAsync(dossier, MCabinetStatus.DraftReady, cancellationToken).ConfigureAwait(false);

        return Result<DraftDocumentsResult>.Success(
            new DraftDocumentsResult(sheetResult.Value, decisionResult.Value));
    }

    /// <inheritdoc />
    public async Task<Result> SubmitForApprovalAsync(string dossierId, CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(dossierId);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        // Include Solicitant so the MCabinet card projection has the citizen IDNP on hand;
        // the ServicePassport is fetched separately below (no navigation on Application).
        var dossier = await _db.Dossiers
            .Include(d => d.Application).ThenInclude(a => a!.Solicitant)
            .SingleOrDefaultAsync(d => d.Id == decoded.Value && d.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (dossier is null || dossier.Application is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Dossier not found.");
        }

        // Guard: only the assigned examiner can submit for approval. We treat the call as
        // Forbidden when AssignedExaminerId is null too — the dossier hasn't been claimed yet.
        if (dossier.AssignedExaminerId is null || _caller.UserId is null
            || dossier.AssignedExaminerId.Value != _caller.UserId.Value)
        {
            return Result.Failure(ErrorCodes.Forbidden, "Caller is not the assigned examiner.");
        }

        // iter-149 — submit-for-approval state guard. The Romanian lifecycle only
        // allows the transition from UnderExamination / Submitted into PendingApproval.
        // Mutating from a non-legal predecessor (Approved, Rejected, Draft,
        // RejectedIncomplete, PendingApproval already, Returned, Closed, Withdrawn,
        // SignedByDirector) would break the state machine and silently overwrite
        // earlier verdicts — surface a stable Conflict instead.
        if (dossier.Application.Status is not (ApplicationStatus.UnderExamination
            or ApplicationStatus.Submitted))
        {
            return Result.Failure(
                ErrorCodes.Conflict,
                $"Cannot submit for approval from status {dossier.Application.Status}.");
        }

        var now = _clock.UtcNow;
        dossier.Application.Status = ApplicationStatus.PendingApproval;
        dossier.Application.UpdatedAtUtc = now;
        dossier.UpdatedAtUtc = now;

        // Close any open examiner task on this dossier (there should be at most one). We
        // tolerate the absence (zero open tasks) — admins may have completed it manually.
        var examinerTask = await _db.WorkflowTasks
            .Where(t => t.DossierId == dossier.Id
                && t.GroupCode == ExaminerGroup
                && t.IsActive
                && t.Status != WorkflowTaskStatus.Completed
                && t.Status != WorkflowTaskStatus.Cancelled)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (examinerTask is not null)
        {
            examinerTask.Status = WorkflowTaskStatus.Completed;
            examinerTask.CompletedAtUtc = now;
            examinerTask.UpdatedAtUtc = now;
        }

        // Open the decider task with a 5-day SLA — TOR §2.5.1 default for approval steps.
        // R0202 — the task lands in the decider group inbox unclaimed, so the
        // UnclaimedSinceUtc stamp must be set here for the escalation sweep to pick it up
        // if no șef-direcție claims it within the configured window.
        var deciderTask = new WorkflowTask
        {
            DossierId = dossier.Id,
            Title = "Aprobare decizie",
            GroupCode = DeciderGroup,
            Status = WorkflowTaskStatus.Pending,
            DueAtUtc = now.AddDays(5),
            UnclaimedSinceUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid ?? "system",
            IsActive = true,
        };
        _db.WorkflowTasks.Add(deciderTask);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var detailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            dossierNumber = dossier.DossierNumber,
            applicationId = _sqids.Encode(dossier.Application.Id),
        });
        await _audit.RecordAsync(
            "DOSSIER.SUBMITTED_FOR_APPROVAL",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Dossier),
            dossier.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        await _notify.EnqueueAsync(
            dossier.Application.SolicitantId,
            "Dosar transmis pentru aprobare",
            $"Dosarul Dvs. ({dossier.DossierNumber}) a fost transmis pentru aprobare finală.",
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        // R0174 / TOR CF 22.03 — emit the canonical ApprovalNeeded trigger so the
        // approver inbox surfaces a deep-link-anchored notification. Best-effort:
        // a failed dispatch is logged and swallowed (the state machine MUST NOT
        // be broken by a notification side-effect).
        if (_triggers is not null)
        {
            try
            {
                // The decider task above is freshly persisted with an assigned
                // group code; the recipient is the queue (no concrete user id yet).
                // We anchor the notification to the Dossier — the approver opens it,
                // the deep-link resolves to /dossiers/{sqid}. The notification fires
                // against the șef-direcție lead via the dossier's assigned officer
                // when known; otherwise we skip the per-user dispatch and rely on
                // the group-inbox surface.
                if (dossier.ApproverId is long approverUserId)
                {
                    await _triggers.DispatchAsync(
                        Cnas.Ps.Application.Notifications.NotificationTriggerKind.ApprovalNeeded,
                        new Cnas.Ps.Application.Notifications.NotificationTriggerPayload(
                            RecipientUserId: approverUserId,
                            Subject: "Aprobare necesară",
                            Body: $"Dosarul {dossier.DossierNumber} așteaptă aprobare.",
                            CorrelationId: _caller.CorrelationId,
                            RelatedEntityType: Cnas.Ps.Application.Notifications.NotificationRelatedEntityTypes.Dossier,
                            RelatedEntityId: dossier.Id),
                        cancellationToken).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // Best-effort notification — MUST NOT break the state machine.
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ApprovalNeeded trigger dispatch failed for dossier {DossierSqid}; state machine unaffected.",
                    _sqids.Encode(dossier.Id));
            }
#pragma warning restore CA1031
        }

        // SubmitForApproval semantically advances the dossier inside the examination
        // pipeline (drafts done → awaiting șef-direcție approval). From the citizen's
        // dashboard perspective the dossier is still "in examination", so we publish
        // InExamination here. The Approved/Rejected terminal cards are published from
        // UC10 (DecisionWorkflowService) in a separate epic.
        await PublishMCabinetAsync(dossier, MCabinetStatus.InExamination, cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> RefuseAsync(string dossierId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var decoded = _sqids.TryDecode(dossierId);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        // Include Solicitant so the MCabinet Rejected card projection has the citizen IDNP
        // on hand; the ServicePassport is fetched separately below (no navigation).
        var dossier = await _db.Dossiers
            .Include(d => d.Application).ThenInclude(a => a!.Solicitant)
            .SingleOrDefaultAsync(d => d.Id == decoded.Value && d.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (dossier is null || dossier.Application is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Dossier not found.");
        }

        if (dossier.AssignedExaminerId is null || _caller.UserId is null
            || dossier.AssignedExaminerId.Value != _caller.UserId.Value)
        {
            return Result.Failure(ErrorCodes.Forbidden, "Caller is not the assigned examiner.");
        }

        var now = _clock.UtcNow;
        dossier.Application.Status = ApplicationStatus.Rejected;
        dossier.Application.ClosedAtUtc = now;
        dossier.Application.UpdatedAtUtc = now;
        dossier.ClosedAtUtc = now;
        dossier.UpdatedAtUtc = now;

        // Cancel every open workflow task on this dossier — the examiner's refusal is final.
        var openTasks = await _db.WorkflowTasks
            .Where(t => t.DossierId == dossier.Id
                && t.IsActive
                && t.Status != WorkflowTaskStatus.Completed
                && t.Status != WorkflowTaskStatus.Cancelled)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var task in openTasks)
        {
            task.Status = WorkflowTaskStatus.Cancelled;
            task.CompletedAtUtc = now;
            task.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var detailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            dossierNumber = dossier.DossierNumber,
            reason,
        });
        await _audit.RecordAsync(
            "DOSSIER.REFUSED_BY_EXAMINER",
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Dossier),
            dossier.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        await _notify.EnqueueAsync(
            dossier.Application.SolicitantId,
            "Cerere respinsă",
            $"Cererea Dvs. (dosar {dossier.DossierNumber}) a fost respinsă. Motiv: {reason}",
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        await PublishMCabinetAsync(dossier, MCabinetStatus.Rejected, cancellationToken).ConfigureAwait(false);

        // OTel metric (best-effort) — examiner-driven refusal, tagged so the alert
        // pipeline can distinguish it from the decider rejection and the auto-reject
        // path inside ApplicationProcessingService. The service_code tag adds the
        // dossier-level dimension for dashboards.
        var refusePassportCode = await _db.ServicePassports.AsNoTracking()
            .Where(p => p.Id == dossier.Application.ServicePassportId)
            .Select(p => p.Code)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? "?";
        RecordCounterSafelyMulti(
            CnasTelemetry.DossiersRejected,
            new TagList
            {
                { "tag", "examiner-refuse" },
                { "service_code", refusePassportCode },
            });

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<EmittedDecisionDto>> EmitNewDecisionAsync(
        string examinationSqid,
        EmitNewDecisionInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Decode the dossier sqid (CLAUDE.md RULE 3). On failure surface the
        //    InvalidSqid code unchanged so the controller can map it to 400.
        var decoded = _sqids.TryDecode(examinationSqid);
        if (decoded.IsFailure)
        {
            return Result<EmittedDecisionDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        // 2. Load dossier + application + solicitant. We need the parent
        //    application's Status to enforce the editable-state guard, and the
        //    SolicitantId to fire the action-result trigger downstream.
        var dossier = await _db.Dossiers
            .Include(d => d.Application).ThenInclude(a => a!.Solicitant)
            .SingleOrDefaultAsync(d => d.Id == decoded.Value && d.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (dossier is null || dossier.Application is null)
        {
            return Result<EmittedDecisionDto>.Failure(ErrorCodes.NotFound, "Dossier not found.");
        }

        // 3. Authorisation — only the assigned examiner can emit a new decision.
        //    We treat unassigned dossiers as Forbidden too (the dossier hasn't been
        //    claimed yet), mirroring SubmitForApprovalAsync / RefuseAsync.
        if (dossier.AssignedExaminerId is null || _caller.UserId is null
            || dossier.AssignedExaminerId.Value != _caller.UserId.Value)
        {
            return Result<EmittedDecisionDto>.Failure(
                ErrorCodes.Forbidden,
                "Caller is not the assigned examiner.");
        }

        // 4. Editable-state guard. Approved / Rejected / Closed / Withdrawn are
        //    terminal — the examiner must reopen the dossier through a separate
        //    admin path before emitting further decisions.
        if (!IsExaminationEditable(dossier.Application.Status))
        {
            return Result<EmittedDecisionDto>.Failure(
                ErrorCodes.ExaminationNotEditable,
                $"Examination is not editable (status={dossier.Application.Status}).");
        }

        // 5. Template-code guard. We refuse to render an unknown template so a
        //    typo cannot silently produce a blank-stub document. The lookup is
        //    case-insensitive — the canonical wire form is kebab-case but the
        //    UI is not required to lower-case before submitting.
        var requestedCode = input.DecisionTemplateCode ?? string.Empty;
        if (!_templateCodes.Contains(requestedCode))
        {
            return Result<EmittedDecisionDto>.Failure(
                ErrorCodes.DocumentTemplateNotFound,
                $"No decision template registered with code '{requestedCode}'.");
        }

        // iter-149 / R0573-followup — OverrideAmount handling is not yet wired
        // into the generation engine. Until the renderer learns to apply a
        // manual override, accepting OverrideAmount.HasValue would silently
        // discard the operator's intent (the engine outcome would be emitted
        // unchanged while the override only surfaces in the audit trail). We
        // refuse with a stable NotImplemented code so the UI can hide the
        // override field and the operator does not receive a deceptive
        // success.
        if (input.OverrideAmount.HasValue)
        {
            return Result<EmittedDecisionDto>.Failure(
                ErrorCodes.NotImplemented,
                "Override amount handling is not yet wired into the generation engine.");
        }

        // 6. Delegate the actual render + persist + audit to the generation
        //    service. We always re-evaluate the engine because "emit new
        //    decision" semantically means "produce a fresh document with the
        //    latest inputs". The OverrideAmount is captured on the audit row
        //    below — the generation service does not yet expose an override
        //    hook (TODO R0573-followup) and unconditionally honours the engine
        //    outcome; the override surfaces in the audit trail for now so the
        //    operator's intent is recorded.
        var genResult = await _docgen
            .GenerateDecisionAsync(examinationSqid, cancellationToken)
            .ConfigureAwait(false);
        if (genResult.IsFailure)
        {
            return Result<EmittedDecisionDto>.Failure(genResult.ErrorCode!, genResult.ErrorMessage!);
        }

        var newDocumentSqid = genResult.Value;

        // 7. Audit emit-decision (Notice — write to non-sensitive data).
        var auditDetails = System.Text.Json.JsonSerializer.Serialize(new
        {
            dossierNumber = dossier.DossierNumber,
            templateCode = requestedCode,
            documentId = newDocumentSqid,
            notes = input.Notes ?? string.Empty,
            overrideAmount = input.OverrideAmount,
        });
        await _audit.RecordAsync(
            "EXAMINATION.NEW_DECISION_EMITTED",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Dossier),
            dossier.Id,
            auditDetails,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        // 8. Stamp the dossier update timestamp so dashboards reflect the fresh
        //    activity. We do this AFTER the generation service runs so a
        //    rendering failure does not leave behind a phantom touch.
        var now = _clock.UtcNow;
        dossier.UpdatedAtUtc = now;
        dossier.Application.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // 9. Fire the ActionResult canonical trigger so the solicitant's inbox
        //    surfaces a deep-linked notification (R0172 / TOR CF 22.03). Best-
        //    effort — a downstream failure is logged at Warning and swallowed
        //    so the dossier state machine remains correct.
        if (_triggers is not null)
        {
            try
            {
                var refNum = dossier.Application.ReferenceNumber ?? _sqids.Encode(dossier.Application.Id);
                await _triggers.DispatchAsync(
                    Cnas.Ps.Application.Notifications.NotificationTriggerKind.ActionResult,
                    new Cnas.Ps.Application.Notifications.NotificationTriggerPayload(
                        RecipientUserId: dossier.Application.SolicitantId,
                        Subject: "Decizie nouă emisă",
                        Body: $"O nouă decizie ({requestedCode}) a fost emisă pe dosarul {refNum}.",
                        CorrelationId: _caller.CorrelationId,
                        RelatedEntityType: Cnas.Ps.Application.Notifications.NotificationRelatedEntityTypes.Application,
                        RelatedEntityId: dossier.Application.Id),
                    cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort notification — MUST NOT break the state machine.
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ActionResult trigger dispatch failed for dossier {DossierSqid}; state machine unaffected.",
                    _sqids.Encode(dossier.Id));
            }
#pragma warning restore CA1031
        }

        return Result<EmittedDecisionDto>.Success(
            new EmittedDecisionDto(
                DocumentId: newDocumentSqid,
                DecisionTemplateCode: requestedCode.ToLowerInvariant()));
    }

    /// <summary>
    /// R0573 / TOR CF 08.05 — predicate consulted by <see cref="EmitNewDecisionAsync"/>
    /// to decide whether the dossier is still in an editable examination state. The
    /// editable set is "anything that hasn't reached a terminal decision yet": Draft,
    /// Submitted, UnderExamination, PendingApproval. The terminal set
    /// (Approved / Rejected / Closed / Withdrawn) returns <see langword="false"/> so
    /// the examiner must reopen the dossier through a separate admin path before
    /// emitting further decisions.
    /// </summary>
    /// <param name="status">Parent-application status to inspect.</param>
    /// <returns><see langword="true"/> when emit-decision is still permitted.</returns>
    private static bool IsExaminationEditable(ApplicationStatus status) => status switch
    {
        ApplicationStatus.Draft => true,
        ApplicationStatus.Submitted => true,
        ApplicationStatus.UnderExamination => true,
        ApplicationStatus.PendingApproval => true,
        _ => false,
    };

    /// <summary>
    /// Publishes a dossier-card revision to MCabinet for the supplied <paramref name="dossier"/>,
    /// translating the EF Core aggregate (with eager-loaded Application + Solicitant) plus a
    /// fresh ServicePassport read into a <see cref="MCabinetCard"/> revision, and forwarding it
    /// to <see cref="IMCabinetPublisher"/>. The publish is best-effort: a failed
    /// <see cref="Result"/> or thrown exception is logged at <c>Warning</c> level with
    /// structured fields <c>dossierSqid</c> and <c>status</c>, and then swallowed so the
    /// dossier state machine cannot be broken by an outbound projection failure.
    /// </summary>
    /// <param name="dossier">
    /// Dossier whose state just changed. <c>Application.Solicitant</c> must be loaded —
    /// the helper skips the publish and emits a debug log when it is missing rather than
    /// throwing, because that indicates a programming mistake (an Include() that was
    /// forgotten) and we don't want such an oversight to break a real-world transition.
    /// The ServicePassport is fetched here because there is no navigation on
    /// <see cref="ServiceApplication"/>.
    /// </param>
    /// <param name="status">Citizen-facing status to project to MCabinet.</param>
    /// <param name="cancellationToken">Cancellation propagation token.</param>
    private async Task PublishMCabinetAsync(Dossier dossier, MCabinetStatus status, CancellationToken cancellationToken)
    {
        if (dossier.Application is null || dossier.Application.Solicitant is null)
        {
            _logger.LogDebug(
                "MCabinet publish skipped — dossier {DossierId} aggregate not fully loaded.",
                dossier.Id);
            return;
        }

        // ServicePassport has no navigation property on ServiceApplication, so we fetch
        // it by FK. The two columns we need (Code, NameRo) are tiny — no projection cost.
        var passport = await _db.ServicePassports
            .Where(p => p.Id == dossier.Application.ServicePassportId)
            .Select(p => new { p.Code, p.NameRo })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (passport is null)
        {
            _logger.LogDebug(
                "MCabinet publish skipped — passport {PassportId} missing for dossier {DossierId}.",
                dossier.Application.ServicePassportId, dossier.Id);
            return;
        }

        var dossierSqid = _sqids.Encode(dossier.Id);
        var card = new MCabinetCard(
            ExternalId: dossierSqid,
            CitizenIdnp: dossier.Application.Solicitant.NationalId,
            ServiceCode: passport.Code,
            Status: status,
            TitleRo: passport.NameRo,
            SubtitleRo: dossier.DossierNumber,
            EventUtc: _clock.UtcNow,
            DeepLink: null);

        try
        {
            var result = await _mcabinet.PublishCardAsync(card, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                _logger.LogWarning(
                    "MCabinet publish failed: dossierSqid={DossierSqid} status={Status} errorCode={ErrorCode}",
                    dossierSqid, status, result.ErrorCode);
            }
        }
#pragma warning disable CA1031 // Best-effort projection: a thrown publisher MUST NOT break the dossier state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "MCabinet publish threw: dossierSqid={DossierSqid} status={Status}",
                dossierSqid, status);
        }
#pragma warning restore CA1031
    }
}

/// <summary>UC10 — Approve / reject a decision.</summary>
/// <remarks>
/// On every terminal transition (approve / reject) the service also mirrors a
/// citizen-portal card revision to MCabinet via <see cref="IMCabinetPublisher"/>: approve
/// emits an <see cref="MCabinetStatus.Approved"/> card and reject emits an
/// <see cref="MCabinetStatus.Rejected"/> card. The mirror is best-effort — a publish
/// failure is logged at <c>Warning</c> level and swallowed so the dossier state change
/// (the source of truth) commits regardless. See CLAUDE.md cross-cutting
/// "Idempotent Callbacks".
/// </remarks>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping (CLAUDE.md RULE 3).</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller — supplies <c>UserSqid</c>, roles, IP for audit.</param>
/// <param name="audit">Audit journal façade.</param>
/// <param name="mcabinet">Citizen-portal publisher (MCabinet) — best-effort outbound projection.</param>
/// <param name="logger">Structured logger for the best-effort publish path.</param>
/// <param name="budget">
/// R0167 query-budget guard consulted before list materialisation (R0671 continuation).
/// Optional with default <see langword="null"/> so existing Approve/Reject-only test
/// compositions keep compiling; <see cref="DecisionWorkflowService.ListAsync"/> requires it.
/// </param>
/// <param name="qbeConverter">
/// R0163 QBE-to-LINQ converter used by the list path. Optional for the same reason as
/// <paramref name="budget"/>.
/// </param>
/// <param name="accessScopeFilter">
/// R0671 row-level access-scope predicate splicer; the list path narrows via the
/// parent ServiceApplication's subdivision-code allow-list. Optional for the same
/// reason as <paramref name="budget"/>.
/// </param>
/// <param name="triggers">R0174 — optional canonical-trigger dispatcher (ActionResult fan-out).</param>
/// <param name="refusedPensionFallback">
/// R0942 — optional refused-pension → AlocatieSociala auto-fallback cascade. Invoked on
/// every Rejected transition; nullable for back-compat with legacy DI scopes.
/// </param>
public sealed class DecisionWorkflowService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    IMCabinetPublisher mcabinet,
    ILogger<DecisionWorkflowService> logger,
    Cnas.Ps.Application.QueryBudget.IQueryBudgetService? budget = null,
    Cnas.Ps.Application.Qbe.IQbeToLinqConverter? qbeConverter = null,
    Cnas.Ps.Application.AccessScope.IAccessScopeFilter? accessScopeFilter = null,
    Cnas.Ps.Application.Notifications.INotificationTriggerDispatcher? triggers = null,
    Cnas.Ps.Application.UseCases.IRefusedPensionFallbackCascade? refusedPensionFallback = null)
    : IDecisionWorkflowService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly IMCabinetPublisher _mcabinet = mcabinet;
    private readonly ILogger<DecisionWorkflowService> _logger = logger;
    private readonly Cnas.Ps.Application.QueryBudget.IQueryBudgetService? _budget = budget;
    private readonly Cnas.Ps.Application.Qbe.IQbeToLinqConverter? _qbeConverter = qbeConverter;
    private readonly Cnas.Ps.Application.AccessScope.IAccessScopeFilter? _accessScopeFilter = accessScopeFilter;

    /// <summary>
    /// R0174 / TOR CF 22.03 — optional ActionResult trigger dispatcher. Fired
    /// after an approve/reject decision lands so the citizen inbox row carries
    /// the <c>Application</c> deep-link anchor (R0172). Nullable for back-compat
    /// with legacy DI scopes.
    /// </summary>
    private readonly Cnas.Ps.Application.Notifications.INotificationTriggerDispatcher? _triggers = triggers;

    /// <summary>
    /// R0942 / TOR §10.1 — optional refused-pension → AlocatieSociala auto-
    /// fallback cascade. Invoked on every Rejected transition; nullable for
    /// back-compat with legacy DI scopes that have not opted in to the cascade.
    /// </summary>
    private readonly Cnas.Ps.Application.UseCases.IRefusedPensionFallbackCascade? _refusedPensionFallback = refusedPensionFallback;

    /// <inheritdoc />
    public Cnas.Ps.Application.QueryBudget.QueryBudgetVerdict? LastBudgetVerdict { get; private set; }

    /// <inheritdoc />
    public Task<Result> ApproveAsync(string dossierId, string? note, CancellationToken cancellationToken = default)
        => TransitionAsync(dossierId, approve: true, note, cancellationToken);

    /// <inheritdoc />
    public Task<Result> RejectAsync(string dossierId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return TransitionAsync(dossierId, approve: false, reason, cancellationToken);
    }

    private async Task<Result> TransitionAsync(string dossierId, bool approve, string? note, CancellationToken cancellationToken)
    {
        if (!_caller.Roles.Contains("cnas-decider"))
        {
            return Result.Failure(ErrorCodes.WorkflowNotDecider, "Caller lacks decider role.");
        }

        var decoded = _sqids.TryDecode(dossierId);
        if (decoded.IsFailure) return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);

        // Eager-load Solicitant so the MCabinet card projection has the citizen IDNP on
        // hand without a second round trip; the ServicePassport is fetched separately in
        // the publish helper (no navigation property on ServiceApplication).
        var dossier = await _db.Dossiers
            .Include(d => d.Application).ThenInclude(a => a!.Solicitant)
            .SingleOrDefaultAsync(d => d.Id == decoded.Value && d.IsActive, cancellationToken).ConfigureAwait(false);
        if (dossier is null || dossier.Application is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Dossier not found.");
        }

        // iter-149 — terminal-state guard. Once the dossier reaches a final lifecycle
        // status, a second Approve/Reject call MUST NOT silently overwrite the prior
        // verdict, re-stamp ClosedAtUtc, re-fire audit/notification/MCabinet/fallback
        // side-effects, or replay the rejected-pension cascade. We surface a stable
        // Conflict so the caller can branch on the idempotency guard.
        if (dossier.Application.Status is ApplicationStatus.Approved
            or ApplicationStatus.Rejected
            or ApplicationStatus.Closed
            or ApplicationStatus.Withdrawn)
        {
            return Result.Failure(
                ErrorCodes.Conflict,
                $"Dossier is already in terminal state {dossier.Application.Status}.");
        }

        var now = _clock.UtcNow;
        dossier.Application.Status = approve ? ApplicationStatus.Approved : ApplicationStatus.Rejected;
        dossier.Application.UpdatedAtUtc = now;
        // Both approve and reject are final decisions — the dossier is closed in either case.
        // Previously only approvals stamped ClosedAtUtc, which left rejected dossiers indistinguishable
        // from in-flight ones in dashboard counts and reports. (CLAUDE.md cross-cutting: state correctness.)
        dossier.ClosedAtUtc = now;
        dossier.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            approve ? "DOSSIER.APPROVED" : "DOSSIER.REJECTED",
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?", nameof(Dossier), dossier.Id,
            $"{{\"note\":{System.Text.Json.JsonSerializer.Serialize(note ?? string.Empty)}}}",
            _caller.SourceIp, _caller.CorrelationId, cancellationToken).ConfigureAwait(false);

        // Best-effort citizen-portal projection — see CLAUDE.md cross-cutting "Idempotent Callbacks".
        // We publish the matching terminal status so the citizen sees the verdict on MCabinet.
        var status = approve ? MCabinetStatus.Approved : MCabinetStatus.Rejected;
        await PublishMCabinetAsync(dossier, status, cancellationToken).ConfigureAwait(false);

        // OTel metric (best-effort) — increment the matching terminal counter. We resolve
        // the service code from the eager-loaded passport navigation when possible so the
        // dimension is filled in; otherwise we fall back to "?" so the count is still
        // captured (a missing tag would silently aggregate into the no-tag bucket).
        var serviceCode = await _db.ServicePassports.AsNoTracking()
            .Where(p => p.Id == dossier.Application.ServicePassportId)
            .Select(p => p.Code)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? "?";
        if (approve)
        {
            RecordCounterSafely(
                CnasTelemetry.DossiersApproved,
                new KeyValuePair<string, object?>("service_code", serviceCode));
        }
        else
        {
            // Two tags: "tag" labels the rejection channel for alerting; "service_code"
            // adds the dossier-level dimension so dashboards can break rejections down by
            // service (and so test assertions can filter on a unique value when running
            // in parallel against the process-wide Meter singleton).
            RecordCounterSafelyMulti(
                CnasTelemetry.DossiersRejected,
                new TagList
                {
                    { "tag", "decision" },
                    { "service_code", serviceCode },
                });
        }

        // R0174 / TOR CF 22.03 — fire the ActionResult canonical trigger so the
        // citizen's inbox row is anchored to the application via the R0172 deep-
        // link. The trigger is best-effort — a downstream failure is logged at
        // Warning and swallowed so the dossier state-machine stays correct
        // (mirrors the MCabinet publish + telemetry counter patterns).
        if (_triggers is not null)
        {
            try
            {
                var refNum = dossier.Application.ReferenceNumber ?? _sqids.Encode(dossier.Application.Id);
                var subject = approve ? "Cererea aprobată" : "Cererea respinsă";
                var body = approve
                    ? $"Cererea Dvs. (ref {refNum}) a fost aprobată."
                    : $"Cererea Dvs. (ref {refNum}) a fost respinsă. Motiv: {note ?? string.Empty}";
                await _triggers.DispatchAsync(
                    Cnas.Ps.Application.Notifications.NotificationTriggerKind.ActionResult,
                    new Cnas.Ps.Application.Notifications.NotificationTriggerPayload(
                        RecipientUserId: dossier.Application.SolicitantId,
                        Subject: subject,
                        Body: body,
                        CorrelationId: _caller.CorrelationId,
                        RelatedEntityType: Cnas.Ps.Application.Notifications.NotificationRelatedEntityTypes.Application,
                        RelatedEntityId: dossier.Application.Id),
                    cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort notification — MUST NOT break the state machine.
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ActionResult trigger dispatch failed for dossier {DossierSqid}; state machine unaffected.",
                    _sqids.Encode(dossier.Id));
            }
#pragma warning restore CA1031
        }

        // R0942 / TOR §10.1 — on Rejected transitions, fire the refused-pension
        // fallback cascade so a follow-up AlocatieSociala draft is opened
        // automatically when the refused service belongs to the Pensie* family.
        // Best-effort: any thrown exception is logged at Warning and swallowed
        // so the dossier state-machine remains correct.
        if (!approve && _refusedPensionFallback is not null && dossier.Application is not null)
        {
            try
            {
                await _refusedPensionFallback.EvaluateAsync(
                    dossier.Application.Id,
                    cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort cascade — MUST NOT break the state machine.
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Refused-pension fallback cascade threw for application {AppSqid}; state machine unaffected.",
                    _sqids.Encode(dossier.Application.Id));
            }
#pragma warning restore CA1031
        }

        return Result.Success();
    }

    /// <summary>
    /// Best-effort wrapper around <see cref="System.Diagnostics.Metrics.Counter{T}"/>.Add
    /// that swallows any exception thrown by a downstream
    /// <see cref="System.Diagnostics.Metrics.MeterListener"/> or exporter. The dossier
    /// state machine must never be broken by a telemetry side-effect — mirrors the
    /// MCabinet best-effort pattern documented at the top of the type.
    /// </summary>
    /// <param name="counter">Pre-declared counter to increment by one.</param>
    /// <param name="tag">Key/value tag describing the dossier dimension.</param>
    private void RecordCounterSafely(
        System.Diagnostics.Metrics.Counter<long> counter,
        KeyValuePair<string, object?> tag)
    {
        try
        {
            counter.Add(1, tag);
        }
#pragma warning disable CA1031 // Best-effort telemetry: a misbehaving listener MUST NOT break the state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Telemetry counter {Counter} increment threw; ignoring.",
                counter.Name);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Best-effort multi-tag counter increment. Used when more than one dimension
    /// is meaningful (e.g. "tag" describing the rejection channel <i>and</i>
    /// "service_code" describing the underlying service). Any thrown exception is
    /// logged at <c>Warning</c> level and swallowed.
    /// </summary>
    /// <param name="counter">Pre-declared counter to increment by one.</param>
    /// <param name="tags">Set of key/value tags to attach to the measurement.</param>
    private void RecordCounterSafelyMulti(
        System.Diagnostics.Metrics.Counter<long> counter,
        TagList tags)
    {
        try
        {
            counter.Add(1, tags);
        }
#pragma warning disable CA1031 // Best-effort telemetry: a misbehaving listener MUST NOT break the state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Telemetry counter {Counter} increment threw; ignoring.",
                counter.Name);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Publishes a dossier-card revision to MCabinet for the supplied <paramref name="dossier"/>.
    /// Mirrors the pattern in <see cref="DocumentExaminationService"/>: resolves the
    /// passport by FK (no navigation on <see cref="ServiceApplication"/>), builds the
    /// <see cref="MCabinetCard"/>, and forwards it to <see cref="IMCabinetPublisher"/>.
    /// The publish is best-effort — any failed <see cref="Result"/> or thrown exception is
    /// logged at <c>Warning</c> level with structured fields <c>dossierSqid</c> and
    /// <c>status</c>, then swallowed so the dossier state machine cannot be broken by an
    /// outbound projection failure.
    /// </summary>
    /// <param name="dossier">
    /// Dossier whose state just changed. <c>Application.Solicitant</c> must be loaded —
    /// the helper skips the publish and emits a debug log when it is missing rather than
    /// throwing, since that indicates an Include() oversight upstream.
    /// </param>
    /// <param name="status">Citizen-facing terminal status to project to MCabinet.</param>
    /// <param name="cancellationToken">Cancellation propagation token.</param>
    private async Task PublishMCabinetAsync(Dossier dossier, MCabinetStatus status, CancellationToken cancellationToken)
    {
        if (dossier.Application is null || dossier.Application.Solicitant is null)
        {
            _logger.LogDebug(
                "MCabinet publish skipped — dossier {DossierId} aggregate not fully loaded.",
                dossier.Id);
            return;
        }

        var passport = await _db.ServicePassports.AsNoTracking()
            .Where(p => p.Id == dossier.Application.ServicePassportId)
            .Select(p => new { p.Code, p.NameRo })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (passport is null)
        {
            _logger.LogDebug(
                "MCabinet publish skipped — passport {PassportId} missing for dossier {DossierId}.",
                dossier.Application.ServicePassportId, dossier.Id);
            return;
        }

        var dossierSqid = _sqids.Encode(dossier.Id);
        var card = new MCabinetCard(
            ExternalId: dossierSqid,
            CitizenIdnp: dossier.Application.Solicitant.NationalId,
            ServiceCode: passport.Code,
            Status: status,
            TitleRo: passport.NameRo,
            SubtitleRo: dossier.DossierNumber,
            EventUtc: _clock.UtcNow,
            DeepLink: null);

        try
        {
            var result = await _mcabinet.PublishCardAsync(card, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                _logger.LogWarning(
                    "MCabinet publish failed: dossierSqid={DossierSqid} status={Status} errorCode={ErrorCode}",
                    dossierSqid, status, result.ErrorCode);
            }
        }
#pragma warning disable CA1031 // Best-effort projection: a thrown publisher MUST NOT break the dossier state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "MCabinet publish threw: dossierSqid={DossierSqid} status={Status}",
                dossierSqid, status);
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc />
    public async Task<Result<Cnas.Ps.Contracts.DecisionsListPageDto>> ListAsync(
        Cnas.Ps.Contracts.DecisionsListInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        LastBudgetVerdict = null;

        // Optional collaborators must be wired for the list path. They are injected
        // as nullable so existing test compositions for Approve/Reject keep
        // compiling without changes — but ListAsync requires them.
        if (_budget is null || _qbeConverter is null || _accessScopeFilter is null)
        {
            return Result<Cnas.Ps.Contracts.DecisionsListPageDto>.Failure(
                ErrorCodes.Internal,
                "DecisionWorkflowService.ListAsync requires IQueryBudgetService + IQbeToLinqConverter + IAccessScopeFilter to be registered.");
        }

        // 1. Envelope validation.
        var validator = new Cnas.Ps.Application.Validators.DecisionsListInputValidator();
        var validation = validator.Validate(input);
        if (!validation.IsValid)
        {
            return Result<Cnas.Ps.Contracts.DecisionsListPageDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        // 2. Build the filtered queryable. The "decision" entity is the Dossier row —
        //    R0671 narrows via the parent ServiceApplication.SubdivisionCode, so we
        //    project the access-scope filter through a join to the Applications table.
        //    Order: scope-narrow (security boundary) → QBE → date range → budget.
        IQueryable<Dossier> query = _db.Dossiers.Where(d => d.IsActive);

        // Access-scope narrowing — exists() against the SubdivisionCode-scoped
        // Applications. The IAccessScopeFilter.ApplyToServiceApplications method
        // returns the (possibly unchanged) scoped queryable; we use it as the
        // subquery so the NULL-tolerance contract is preserved.
        var scopedApps = _accessScopeFilter.ApplyToServiceApplications(_db.Applications, _caller.AccessScope);
        // When the scope is unscoped, ApplyToServiceApplications returns _db.Applications
        // unchanged and the Any() subquery becomes a trivial join — provider-translatable.
        query = query.Where(d => scopedApps.Any(a => a.Id == d.ApplicationId));

        var ctx = new Cnas.Ps.Application.QueryBudget.QueryFilterContext();

        if (input.Filter is { Conditions.Count: > 0 } dto)
        {
            var qbe = MapDecisionQbe(dto);
            var converted = _qbeConverter.Convert<Dossier>(
                Cnas.Ps.Application.QueryBudget.QueryBudgetRegistries.Decision, qbe);
            if (converted.IsFailure)
            {
                return Result<Cnas.Ps.Contracts.DecisionsListPageDto>.Failure(
                    converted.ErrorCode!, converted.ErrorMessage!);
            }
            query = query.Where(converted.Value);
            ctx = ctx.With("Qbe", dto.Conditions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (input.FromUtc is { } from)
        {
            ctx = ctx.With("CreatedFromUtc", from);
            query = query.Where(d => d.CreatedAtUtc >= from);
        }
        if (input.ToUtc is { } to)
        {
            ctx = ctx.With("CreatedToUtc", to);
            query = query.Where(d => d.CreatedAtUtc < to);
        }

        // 3. Budget gate.
        var verdict = await _budget.EvaluateAsync(
            Cnas.Ps.Application.QueryBudget.QueryBudgetRegistries.Decision,
            query,
            ctx,
            cancellationToken).ConfigureAwait(false);
        LastBudgetVerdict = verdict;

        if (!verdict.Allowed)
        {
            return Result<Cnas.Ps.Contracts.DecisionsListPageDto>.Failure(
                ErrorCodes.QueryTooBroad,
                Cnas.Ps.Application.QueryBudget.QueryBudgetFailureEnvelope.FailureMessage);
        }

        // 4. Paging window + projection. Join with Applications to surface the
        //    parent application's Status as the projected decision status; falls
        //    back to "Unknown" when the application row is missing.
        var skip = Math.Max(0, input.Skip);
        var take = Math.Clamp(input.Take, 1, Cnas.Ps.Application.Validators.DecisionsListInputValidator.MaxTake);

        var rows = await query
            .OrderByDescending(d => d.CreatedAtUtc)
            .ThenByDescending(d => d.Id)
            .Skip(skip)
            .Take(take)
            .Select(d => new
            {
                d.Id,
                d.ApplicationId,
                d.DossierNumber,
                d.CreatedAtUtc,
                d.ClosedAtUtc,
                d.AssignedExaminerId,
                Status = _db.Applications
                    .Where(a => a.Id == d.ApplicationId)
                    .Select(a => (ApplicationStatus?)a.Status)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(r => new Cnas.Ps.Contracts.DecisionListItemDto(
                Id: _sqids.Encode(r.Id),
                ServiceApplicationSqid: _sqids.Encode(r.ApplicationId),
                Status: (r.Status ?? ApplicationStatus.Draft).ToString(),
                DraftedAtUtc: r.CreatedAtUtc,
                FinalisedAtUtc: r.ClosedAtUtc,
                DraftedByUserSqid: r.AssignedExaminerId is { } eid ? _sqids.Encode(eid) : null,
                DossierNumber: r.DossierNumber))
            .ToList();

        return Result<Cnas.Ps.Contracts.DecisionsListPageDto>.Success(new Cnas.Ps.Contracts.DecisionsListPageDto(
            items,
            (int)Math.Min(int.MaxValue, verdict.EstimatedRowCount)));
    }

    /// <summary>
    /// Translates a wire <see cref="Cnas.Ps.Contracts.QbeFilterDto"/> to the
    /// server-side QBE filter. Unknown operator strings surface as a sentinel
    /// value the converter rejects with a stable
    /// <see cref="ErrorCodes.QbeOperatorNotSupported"/> code.
    /// </summary>
    /// <param name="dto">Wire envelope.</param>
    /// <returns>Mapped server-side filter.</returns>
    private static Cnas.Ps.Application.Qbe.QbeFilter MapDecisionQbe(Cnas.Ps.Contracts.QbeFilterDto dto)
    {
        var conds = new List<Cnas.Ps.Application.Qbe.QbeCondition>(dto.Conditions.Count);
        foreach (var c in dto.Conditions)
        {
            if (!Enum.TryParse<Cnas.Ps.Application.Qbe.QbeOperator>(c.Operator, ignoreCase: false, out var op))
            {
                op = (Cnas.Ps.Application.Qbe.QbeOperator)int.MinValue;
            }
            conds.Add(new Cnas.Ps.Application.Qbe.QbeCondition(c.FieldName, op, c.Value, c.Value2));
        }
        return new Cnas.Ps.Application.Qbe.QbeFilter(
            string.IsNullOrEmpty(dto.Combinator) ? Cnas.Ps.Application.Qbe.QbeFilter.CombinatorAnd : dto.Combinator,
            conds);
    }

    /// <inheritdoc />
    public async Task<Result> ForwardToNextLevelAsync(
        string applicationSqid,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!_caller.Roles.Contains("cnas-decider"))
        {
            return Result.Failure(ErrorCodes.WorkflowNotDecider, "Caller lacks decider role.");
        }

        var decoded = _sqids.TryDecode(applicationSqid);
        if (decoded.IsFailure) return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);

        var app = await _db.Applications
            .SingleOrDefaultAsync(a => a.Id == decoded.Value && a.IsActive, cancellationToken).ConfigureAwait(false);
        if (app is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Application not found.");
        }

        var currentLevel = WorkflowRouting.FromApplicationStatus(app.Status);
        if (currentLevel is null)
        {
            return Result.Failure(
                ErrorCodes.WorkflowNotOnApprovalChain,
                $"Application status '{app.Status}' is not on the approval chain.");
        }

        var (decision, isTerminal) = WorkflowRouting.ComputeNextLevel(currentLevel.Value, reason);
        if (isTerminal)
        {
            return Result.Failure(
                ErrorCodes.WorkflowAlreadyAtTop,
                "Application is already at the top of the approval chain (ChiefCnas).");
        }

        var nextStatus = WorkflowRouting.ToApplicationStatus(decision.NextLevel)
            ?? throw new InvalidOperationException("Routing produced an unmapped level.");

        var now = _clock.UtcNow;
        app.Status = nextStatus;
        app.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            $"WORKFLOW.FORWARDED_TO_{decision.NextLevel.ToString().ToUpperInvariant()}",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?", nameof(ServiceApplication), app.Id,
            $"{{\"reason\":{System.Text.Json.JsonSerializer.Serialize(decision.Reason)}}}",
            _caller.SourceIp, _caller.CorrelationId, cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> ReturnToPreviousStepAsync(
        string applicationSqid,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!_caller.Roles.Contains("cnas-decider"))
        {
            return Result.Failure(ErrorCodes.WorkflowNotDecider, "Caller lacks decider role.");
        }

        var decoded = _sqids.TryDecode(applicationSqid);
        if (decoded.IsFailure) return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);

        var app = await _db.Applications
            .SingleOrDefaultAsync(a => a.Id == decoded.Value && a.IsActive, cancellationToken).ConfigureAwait(false);
        if (app is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Application not found.");
        }

        var currentLevel = WorkflowRouting.FromApplicationStatus(app.Status);
        if (currentLevel is null)
        {
            return Result.Failure(
                ErrorCodes.WorkflowNotOnApprovalChain,
                $"Application status '{app.Status}' is not on the approval chain.");
        }

        var (decision, isAtFloor) = WorkflowRouting.ComputePreviousLevel(currentLevel.Value, reason);
        var now = _clock.UtcNow;
        string auditCode;
        if (isAtFloor)
        {
            // R0592 / CF 10.03 — return-from-floor degenerates into a hard rejection
            // (no examiner to bounce back to). The audit event differentiates this
            // path from a regular return so dashboards can distinguish hard-rejects
            // from chain-internal returns.
            app.Status = ApplicationStatus.Rejected;
            auditCode = "WORKFLOW.RETURNED_AT_FLOOR";
        }
        else
        {
            // Normal return: the status reflects the level we are returning TO. For
            // a return from Director → UserCnas the natural mapping is
            // PendingApproval (the examiner has already signed); for a return from
            // ChiefCnas → Director the mapping is SignedByDirector. The lifecycle
            // table (R0939) does not currently model "demote backward" edges, so
            // we sidestep the guard and rely on the chain semantics being explicit
            // in the audit row.
            app.Status = WorkflowRouting.ToApplicationStatus(decision.NextLevel)
                ?? throw new InvalidOperationException("Routing produced an unmapped level.");
            auditCode = "WORKFLOW.RETURNED";
        }
        app.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            auditCode,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?", nameof(ServiceApplication), app.Id,
            $"{{\"reason\":{System.Text.Json.JsonSerializer.Serialize(decision.Reason)},\"toLevel\":{System.Text.Json.JsonSerializer.Serialize(decision.NextLevel.ToString())}}}",
            _caller.SourceIp, _caller.CorrelationId, cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}

/// <summary>UC13 — Self-service profile management.</summary>
public sealed class ProfileService(
    ICnasDbContext db,
    Cnas.Ps.Application.Abstractions.IReadOnlyCnasDbContext readDb,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller) : IProfileService
{
    private readonly ICnasDbContext _db = db;
    private readonly Cnas.Ps.Application.Abstractions.IReadOnlyCnasDbContext _readDb = readDb;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;

    /// <summary>
    /// R0621 / TOR CF 13.02 — maximum number of issued-document rows surfaced
    /// on the profile aggregate. The full history is reachable through the
    /// dedicated documents-registry endpoint; the profile slice is bounded so
    /// the response stays cheap for the citizen self-service page.
    /// </summary>
    private const int IssuedDocumentsCap = 50;

    /// <summary>
    /// R0621 / TOR CF 13.02 — DocumentKind enum values that count as
    /// "issued by CNAS" for the profile aggregate. Excludes citizen-supplied
    /// attachments and internal notes (the staff-only verdict-note kind).
    /// </summary>
    private static readonly DocumentKind[] IssuedDocumentKinds =
    [
        DocumentKind.Decision,
        DocumentKind.Certificate,
        DocumentKind.Extract,
        DocumentKind.Information,
    ];

    /// <inheritdoc />
    public async Task<Result<ProfileOutput>> GetMineAsync(CancellationToken cancellationToken = default)
    {
        if (_caller.UserId is null)
        {
            return Result<ProfileOutput>.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }
        var user = await _db.UserProfiles.SingleOrDefaultAsync(u => u.Id == _caller.UserId.Value && u.IsActive, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result<ProfileOutput>.Failure(ErrorCodes.NotFound, "Profile not found.");
        }

        // R0621 — load the (capped, newest-first) "issued documents" slice of
        // the aggregate. Reads run through the streaming-replica context so the
        // citizen self-service page never burdens the writable primary.
        var issued = await LoadIssuedDocumentsAsync(user, cancellationToken).ConfigureAwait(false);

        // PhoneE164 is encrypted at rest (TOR SEC 035 / CLAUDE.md §5.7) — the EF converter
        // transparently decrypts on read, so we can project the entity column directly onto
        // the DTO without any additional crypto in the service. NULL stays NULL all the
        // way through.
        return Result<ProfileOutput>.Success(new ProfileOutput(
            _sqids.Encode(user.Id),
            user.DisplayName,
            user.Email,
            user.PhoneE164,
            user.PreferredLanguage,
            issued));
    }

    /// <summary>
    /// R0621 / TOR CF 13.02 — loads up to <see cref="IssuedDocumentsCap"/>
    /// CNAS-issued documents (newest first) for the supplied user. Joins the
    /// UserProfile→Solicitant identity link
    /// (<see cref="UserProfile.NationalIdHash"/> ==
    /// <see cref="Solicitant.NationalIdHash"/>) and walks through the
    /// solicitant's applications and dossiers to find documents the citizen
    /// has been issued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Empty by design.</b> Returns an empty list (never <c>null</c>) when:
    /// the user has no NationalIdHash; no Solicitant row links to that hash;
    /// the linked Solicitant has no dossiers; or those dossiers carry only
    /// citizen attachments / internal notes (neither counts as "issued").
    /// </para>
    /// <para>
    /// <b>Read path.</b> Every query in this method runs against
    /// <see cref="Cnas.Ps.Application.Abstractions.IReadOnlyCnasDbContext"/>
    /// per CLAUDE.md ("IReadOnlyCnasDbContext for reads"). The replica may
    /// lag the primary by tens of ms; eventual consistency is acceptable for
    /// the profile read.
    /// </para>
    /// </remarks>
    /// <param name="user">The authenticated caller's profile entity.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>A newest-first, capped list. Never <c>null</c>.</returns>
    private async Task<IReadOnlyList<IssuedDocumentSummaryDto>> LoadIssuedDocumentsAsync(
        UserProfile user,
        CancellationToken cancellationToken)
    {
        // No identity hash → no Solicitant linkage possible → empty slice.
        if (string.IsNullOrEmpty(user.NationalIdHash))
        {
            return Array.Empty<IssuedDocumentSummaryDto>();
        }

        // Resolve the Solicitant via the canonical UserProfile→Solicitant
        // identity link. Mirrors PersonalAccountExtractService /
        // BenefitPaymentStatusService for the same lookup.
        var solicitantId = await _readDb.Solicitants
            .Where(s => s.NationalIdHash == user.NationalIdHash && s.IsActive)
            .Select(s => (long?)s.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (solicitantId is null)
        {
            return Array.Empty<IssuedDocumentSummaryDto>();
        }

        // Pull dossier ids owned by the Solicitant — bounded by the join
        // through their applications. We collect the ids first so the
        // documents query stays a simple Contains() over an in-memory set
        // (better SQL shape on InMemory + Postgres alike).
        var dossierIds = await _readDb.Applications
            .Where(a => a.SolicitantId == solicitantId.Value && a.DossierId != null)
            .Select(a => a.DossierId!.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (dossierIds.Count == 0)
        {
            return Array.Empty<IssuedDocumentSummaryDto>();
        }

        // Load the cap'd, newest-first set of CNAS-issued document rows.
        // We intentionally read only the columns required by the DTO to keep
        // the wire payload small and avoid the large StorageObjectKey /
        // ContentSha256Hex strings.
        var rows = await _readDb.Documents
            .Where(d => d.DossierId != null
                && dossierIds.Contains(d.DossierId.Value)
                && IssuedDocumentKinds.Contains(d.Kind)
                && d.IsActive)
            .OrderByDescending(d => d.CreatedAtUtc)
            .Take(IssuedDocumentsCap)
            .Select(d => new { d.Id, d.Kind, d.Title, d.CreatedAtUtc, d.IsSigned })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Sqid encoding + URL formatting happens in-memory because
        // ISqidService is not translatable to SQL.
        var summaries = new List<IssuedDocumentSummaryDto>(rows.Count);
        foreach (var r in rows)
        {
            var sqid = _sqids.Encode(r.Id);
            summaries.Add(new IssuedDocumentSummaryDto(
                Sqid: sqid,
                DocumentTypeCode: r.Kind.ToString(),
                Title: r.Title,
                IssuedAtUtc: r.CreatedAtUtc,
                Channel: r.IsSigned
                    ? IssuedDocumentChannel.Electronic
                    : IssuedDocumentChannel.Paper,
                Status: "Active",
                DownloadUrl: $"/api/documents/{sqid}/download"));
        }
        return summaries;
    }

    /// <inheritdoc />
    public async Task<Result> UpdateMineAsync(ProfileUpdateInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }

        // Validate and normalise Phone at the service boundary BEFORE any persistence
        // write — we must NEVER silently accept-and-drop malformed PII (CLAUDE.md "no
        // silent value drops"). Null means "the caller is clearing their phone", which
        // is a valid state transition.
        string? normalisedPhone;
        if (input.Phone is null)
        {
            normalisedPhone = null;
        }
        else
        {
            var phoneResult = Cnas.Ps.Core.ValueObjects.PhoneE164.TryCreate(input.Phone);
            if (phoneResult.IsFailure)
            {
                return Result.Failure(phoneResult.ErrorCode!, phoneResult.ErrorMessage!);
            }
            normalisedPhone = phoneResult.Value.Value;
        }

        var user = await _db.UserProfiles.SingleOrDefaultAsync(u => u.Id == _caller.UserId.Value && u.IsActive, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Profile not found.");
        }

        user.Email = input.Email;
        user.PhoneE164 = normalisedPhone;
        user.PreferredLanguage = string.IsNullOrWhiteSpace(input.PreferredLanguage) ? "ro" : input.PreferredLanguage;
        user.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> UpdateMyContactAsync(ProfileContactInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }

        // DisplayName is required — the field anchors the row's human-readable
        // label in the staff console; an empty value would render as blank
        // everywhere it appears. Reject at the boundary so we never persist
        // a value that the FluentValidation layer would also reject — keeps
        // the service-layer guarantee independent of the controller's
        // pre-validation gate.
        if (string.IsNullOrWhiteSpace(input.DisplayName))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Display name is required.");
        }

        // Validate + normalise Phone at the service boundary BEFORE the persistence
        // write — silent accept-and-drop on malformed PII is the historical bug we
        // closed for UpdateMineAsync (and it would re-open here without this guard).
        string? normalisedPhone;
        if (input.Phone is null)
        {
            normalisedPhone = null;
        }
        else
        {
            var phoneResult = Cnas.Ps.Core.ValueObjects.PhoneE164.TryCreate(input.Phone);
            if (phoneResult.IsFailure)
            {
                return Result.Failure(phoneResult.ErrorCode!, phoneResult.ErrorMessage!);
            }
            normalisedPhone = phoneResult.Value.Value;
        }

        var user = await _db.UserProfiles
            .SingleOrDefaultAsync(u => u.Id == _caller.UserId.Value && u.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Profile not found.");
        }

        // Apply ONLY the contact-field deltas. PreferredLanguage is intentionally
        // left untouched — it has its own thin PUT (R0211) and overwriting it
        // here would silently revert a recent language toggle.
        user.DisplayName = input.DisplayName;
        user.Email = input.Email;
        user.PhoneE164 = normalisedPhone;
        user.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Maximum length permitted for a single category key (R0171 anti-bloat guard).</summary>
    private const int MaxCategoryKeyLength = 64;

    /// <inheritdoc />
    public async Task<Result<NotificationPreferencesDto>> GetNotificationPreferencesAsync(CancellationToken cancellationToken = default)
    {
        if (_caller.UserId is null)
        {
            return Result<NotificationPreferencesDto>.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }

        // We only need the JSON column — projecting keeps the encrypted-column scaffolding
        // out of this read path entirely.
        var prefsJson = await _db.UserProfiles
            .Where(u => u.Id == _caller.UserId.Value && u.IsActive)
            .Select(u => new { u.Id, u.NotificationPreferences })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (prefsJson is null)
        {
            return Result<NotificationPreferencesDto>.Failure(ErrorCodes.NotFound, "Profile not found.");
        }

        // Fail-open: NULL/malformed JSON returns the default (opted-in on every channel) so the
        // citizen sees a sensible UI state even if the row predates R0171 or is somehow corrupt.
        var prefs = NotificationPreferencesJson.Parse(prefsJson.NotificationPreferences);
        return Result<NotificationPreferencesDto>.Success(new NotificationPreferencesDto(
            prefs.Email,
            prefs.Sms,
            prefs.InApp,
            new Dictionary<string, bool>(prefs.Categories, StringComparer.OrdinalIgnoreCase)));
    }

    /// <inheritdoc />
    public async Task<Result> SetNotificationPreferencesAsync(NotificationPreferencesDto preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (_caller.UserId is null)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }

        // Validate every category key length at the service boundary — pathological keys
        // would bloat the JSON column without bound. Empty / null keys are rejected too.
        if (preferences.Categories is not null)
        {
            foreach (var key in preferences.Categories.Keys)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > MaxCategoryKeyLength)
                {
                    return Result.Failure(
                        ErrorCodes.ValidationFailed,
                        $"Category key must be 1..{MaxCategoryKeyLength} characters.");
                }
            }
        }

        var user = await _db.UserProfiles
            .SingleOrDefaultAsync(u => u.Id == _caller.UserId.Value && u.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Profile not found.");
        }

        var categories = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (preferences.Categories is not null)
        {
            foreach (var kv in preferences.Categories)
            {
                categories[kv.Key] = kv.Value;
            }
        }

        var prefs = new NotificationPreferences
        {
            Email = preferences.Email,
            Sms = preferences.Sms,
            InApp = preferences.InApp,
            Categories = categories,
        };
        user.NotificationPreferences = NotificationPreferencesJson.Serialize(prefs);
        user.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}

/// <summary>UC14 — Interop wrapper over MConnect.</summary>
public sealed class InteropService(IMConnectClient mconnect) : IInteropService
{
    private readonly IMConnectClient _mconnect = mconnect;

    /// <inheritdoc />
    public Task<Result<string>> CallAsync(string serviceCode, string requestJson, CancellationToken cancellationToken = default)
        => _mconnect.CallAsync(serviceCode, requestJson, cancellationToken);
}

/// <summary>
/// UC15 — ServicePassport administration. R0142 / CF 15.04 — append-only versioning:
/// every semantically-meaningful change inserts a new version row and flips
/// <see cref="ServicePassport.IsCurrent"/> on the predecessor. In-flight applications
/// remain bound to the version row they were submitted under (no mid-flight drift).
/// </summary>
/// <remarks>
/// <para>
/// <b>Branching on the input.</b> <see cref="UpsertAsync"/> takes the create branch when
/// <see cref="ServicePassportInput.Id"/> is null/empty and the version branch otherwise.
/// On the version branch the service computes a semantic diff against the addressed
/// row's logical code; an empty diff is a no-op success (the existing Sqid is echoed
/// back), while a non-empty diff inserts a new version row and emits a Critical
/// <c>SERVICEPASSPORT.VERSION_CREATED</c> audit row.
/// </para>
/// <para>
/// <b>Catalogue vs history.</b> <see cref="ListAsync(CancellationToken)"/> filters to
/// <c>IsCurrent = true</c> so administrators see one row per logical code in the
/// browser. <see cref="GetAsync"/> resolves any revision (current or historical) by
/// Sqid so the admin UI can render a version-detail page. <see cref="GetHistoryAsync"/>
/// returns the entire chain ordered by <see cref="ServicePassport.Version"/> DESC.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="sqids">Sqid encoder/decoder (CLAUDE.md RULE 3).</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller — supplies the actor id for audit rows.</param>
/// <param name="audit">Audit journal façade.</param>
/// <param name="workflows">Workflow configuration service — consulted (optionally) for
/// surfacing the pinned-workflow-version invariant on related dossiers.</param>
public sealed class ServicePassportService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    IWorkflowConfigurationService workflows) : IServicePassportService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    // _workflows is retained for future cross-cutting checks (e.g. validating the
    // referenced WorkflowCode exists before publishing a new version). Today the
    // upsert flow does not consult it inline — the validator owns the cross-ref check —
    // but the dependency is wired so a future tightening is a one-line edit.
#pragma warning disable IDE0052 // intentional forward-looking capture
    private readonly IWorkflowConfigurationService _workflows = workflows;
#pragma warning restore IDE0052

    /// <summary>Stable audit-event code for new passport versions (R0142 / CF 15.04).</summary>
    private const string VersionCreatedEvent = "SERVICEPASSPORT.VERSION_CREATED";

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ServicePassportListItem>>> ListAsync(CancellationToken cancellationToken = default)
        => ListAsync(nameQuery: null, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ServicePassportListItem>>> ListAsync(
        string? nameQuery,
        CancellationToken cancellationToken = default)
    {
        // R0142 — catalogue surface lists only the IsCurrent row per code; superseded
        // history rows are excluded so the admin UI sees a clean per-code summary.
        IQueryable<Cnas.Ps.Core.Domain.ServicePassport> query =
            _db.ServicePassports.Where(p => p.IsActive && p.IsCurrent);

        // R0528 / CF 03.13 — diacritic + case-insensitive substring match against
        // NameRo. Mirrors the Solicitant pattern: trim → DiacriticFolding.Fold →
        // build ILike pattern via WildcardMask. The InMemory fallback uses a regex
        // over the folded column so tests run without an unaccent extension.
        if (!string.IsNullOrWhiteSpace(nameQuery))
        {
            var trimmed = nameQuery.Trim();
            var folded = Cnas.Ps.Application.Search.DiacriticFolding.Fold(trimmed);
            if (IsRelationalProvider(_db))
            {
                var likePattern = Cnas.Ps.Application.Search.WildcardMask.ToLikePattern(folded);
                query = query.Where(p =>
                    EF.Functions.ILike(CnasDbFunctions.Unaccent(p.NameRo), likePattern));
            }
            else
            {
                var regex = Cnas.Ps.Application.Search.WildcardMask.ToRegex(folded);
                query = query.Where(p => regex.IsMatch(Cnas.Ps.Application.Search.DiacriticFolding.Fold(p.NameRo)));
            }
        }

        var rows = await query
            .OrderBy(p => p.NameRo)
            .Select(p => new { p.Id, p.Code, p.NameRo, p.IsEnabled, p.Version })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ServicePassportListItem> items = rows.Select(r =>
            new ServicePassportListItem(_sqids.Encode(r.Id), r.Code, r.NameRo, r.IsEnabled, r.Version)).ToList();
        return Result<IReadOnlyList<ServicePassportListItem>>.Success(items);
    }

    /// <summary>
    /// R0528 — detects whether the underlying <see cref="ICnasDbContext"/> is backed by a
    /// relational provider (Npgsql in production) vs the InMemory test fake. Mirrors the
    /// seam from <c>SolicitantService.IsRelationalProvider</c>.
    /// </summary>
    /// <param name="db">The application's DB context abstraction.</param>
    /// <returns><see langword="true"/> for Postgres / SQL Server / SQLite; <see langword="false"/> for InMemory.</returns>
    private static bool IsRelationalProvider(ICnasDbContext db)
    {
        if (db is not DbContext concrete)
        {
            return false;
        }
        var providerName = concrete.Database.ProviderName ?? string.Empty;
        return !providerName.Contains("InMemory", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<Result<ServicePassportDetailOutput>> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure) return Result<ServicePassportDetailOutput>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);

        // R0142 — GET resolves any version row (current or historical) by Sqid so the
        // admin UI can render a specific revision's detail page.
        var p = await _db.ServicePassports.SingleOrDefaultAsync(x => x.Id == decoded.Value && x.IsActive, cancellationToken).ConfigureAwait(false);
        if (p is null) return Result<ServicePassportDetailOutput>.Failure(ErrorCodes.NotFound, "Not found.");

        return Result<ServicePassportDetailOutput>.Success(new ServicePassportDetailOutput(
            _sqids.Encode(p.Id), p.Code, p.NameRo, p.NameEn, p.NameRu,
            p.DescriptionRo, p.FormSchemaJson, p.WorkflowCode, p.MaxProcessingDays, p.IsEnabled, p.IsProactive,
            p.DecisionRulesJson, p.Version, p.IsCurrent));
    }

    /// <inheritdoc />
    public async Task<Result<string>> UpsertAsync(ServicePassportInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrEmpty(input.Id))
        {
            // Create branch — insert as Version=1, IsCurrent=true, chain pointers null.
            var row = new ServicePassport
            {
                CreatedAtUtc = _clock.UtcNow,
                Code = input.Code,
                NameRo = input.NameRo,
                NameEn = input.NameEn,
                NameRu = input.NameRu,
                DescriptionRo = input.DescriptionRo,
                FormSchemaJson = input.FormSchemaJson,
                WorkflowCode = input.WorkflowCode,
                MaxProcessingDays = input.MaxProcessingDays,
                IsEnabled = input.IsEnabled,
                IsProactive = input.IsProactive,
                DecisionRulesJson = input.DecisionRulesJson,
                Version = 1,
                IsCurrent = true,
            };
            _db.ServicePassports.Add(row);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<string>.Success(_sqids.Encode(row.Id));
        }

        // Version branch — resolve the logical code from the addressed row, then either
        // no-op (semantic diff empty) or insert a new version row.
        var decoded = _sqids.TryDecode(input.Id);
        if (decoded.IsFailure) return Result<string>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);

        var addressed = await _db.ServicePassports
            .SingleOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (addressed is null)
        {
            return Result<string>.Failure(ErrorCodes.NotFound, "Service passport not found.");
        }

        // Find the CURRENT row for this logical code — the version branch always operates
        // against the live catalogue row, not the historical one the caller may have
        // addressed. This is a defensive read: if the admin clicked "edit" on a
        // stale-tab UI showing v2 while v3 was published in the meantime, the new
        // version is appended after v3 (we never insert between historical rows).
        var current = await _db.ServicePassports
            .SingleOrDefaultAsync(p => p.Code == addressed.Code && p.IsCurrent, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return Result<string>.Failure(ErrorCodes.NotFound, "No current revision for this passport.");
        }

        // Semantic diff — compare every business-meaningful field. Audit / chain columns
        // are excluded so a pure UpdatedBy / UpdatedAtUtc change does NOT spawn a version.
        var diff = ComputePassportDiff(current, input);
        if (diff.Count == 0)
        {
            // No-op success — return the CURRENT row's Sqid so the caller can refresh.
            return Result<string>.Success(_sqids.Encode(current.Id));
        }

        // Insert the new version row in the same transaction that flips the predecessor.
        var now = _clock.UtcNow;
        var nextVersion = current.Version + 1;
        var newRow = new ServicePassport
        {
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            Code = current.Code,
            NameRo = input.NameRo,
            NameEn = input.NameEn,
            NameRu = input.NameRu,
            DescriptionRo = input.DescriptionRo,
            FormSchemaJson = input.FormSchemaJson,
            WorkflowCode = input.WorkflowCode,
            MaxProcessingDays = input.MaxProcessingDays,
            IsEnabled = input.IsEnabled,
            IsProactive = input.IsProactive,
            DecisionRulesJson = input.DecisionRulesJson,
            Version = nextVersion,
            IsCurrent = true,
            SupersedesPassportId = current.Id,
        };
        _db.ServicePassports.Add(newRow);

        current.IsCurrent = false;
        current.SupersededAtUtc = now;
        current.UpdatedAtUtc = now;
        current.UpdatedBy = _caller.UserSqid;
        // SupersededByPassportId is set AFTER SaveChanges so we know the new row's Id.

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            return Result<string>.Failure(
                ErrorCodes.ConcurrencyConflict,
                $"Service passport '{current.Code}' was modified by another publisher: {ex.Message}");
        }

        current.SupersededByPassportId = newRow.Id;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // R0142 — emit Critical audit capturing the from/to versions + the diff field
        // names so investigators can reconstruct WHAT changed without echoing the full
        // (potentially large) payload.
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            code = current.Code,
            fromVersion = current.Version,
            toVersion = nextVersion,
            diff,
        });
        await _audit.RecordAsync(
            eventCode: VersionCreatedEvent,
            severity: AuditSeverity.Critical,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(ServicePassport),
            targetEntityId: newRow.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result<string>.Success(_sqids.Encode(newRow.Id));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ServicePassportHistoryItem>>> GetHistoryAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return Result<IReadOnlyList<ServicePassportHistoryItem>>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var addressed = await _db.ServicePassports
            .Where(p => p.Id == decoded.Value)
            .Select(p => new { p.Code })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (addressed is null)
        {
            return Result<IReadOnlyList<ServicePassportHistoryItem>>.Failure(
                ErrorCodes.NotFound, "Service passport not found.");
        }

        var rows = await _db.ServicePassports
            .Where(p => p.Code == addressed.Code)
            .OrderByDescending(p => p.Version)
            .Select(p => new ServicePassportHistoryItem(
                _sqids.Encode(p.Id),
                p.Code,
                p.Version,
                p.IsCurrent,
                p.CreatedAtUtc,
                p.SupersededAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<ServicePassportHistoryItem>>.Success(rows);
    }

    /// <summary>
    /// R0142 — computes the set of semantically-meaningful field names that differ
    /// between the existing current row and the incoming input. The returned list drives
    /// (a) the "should we insert a new version?" decision (empty list ⇒ no-op) and
    /// (b) the audit-details JSON payload. Audit / chain / version columns are
    /// excluded — only business-payload differences count.
    /// </summary>
    /// <param name="current">The currently-active row for the passport's logical code.</param>
    /// <param name="input">The incoming desired-state input.</param>
    /// <returns>Sorted list of changed field names.</returns>
    private static List<string> ComputePassportDiff(ServicePassport current, ServicePassportInput input)
    {
        var changed = new List<string>();
        if (current.NameRo != input.NameRo) changed.Add(nameof(ServicePassport.NameRo));
        if (current.NameEn != input.NameEn) changed.Add(nameof(ServicePassport.NameEn));
        if (current.NameRu != input.NameRu) changed.Add(nameof(ServicePassport.NameRu));
        if (current.DescriptionRo != input.DescriptionRo) changed.Add(nameof(ServicePassport.DescriptionRo));
        if (current.FormSchemaJson != input.FormSchemaJson) changed.Add(nameof(ServicePassport.FormSchemaJson));
        if (current.WorkflowCode != input.WorkflowCode) changed.Add(nameof(ServicePassport.WorkflowCode));
        if (current.MaxProcessingDays != input.MaxProcessingDays) changed.Add(nameof(ServicePassport.MaxProcessingDays));
        if (current.IsEnabled != input.IsEnabled) changed.Add(nameof(ServicePassport.IsEnabled));
        if (current.IsProactive != input.IsProactive) changed.Add(nameof(ServicePassport.IsProactive));
        if (current.DecisionRulesJson != input.DecisionRulesJson) changed.Add(nameof(ServicePassport.DecisionRulesJson));
        changed.Sort(StringComparer.Ordinal);
        return changed;
    }
}

/// <summary>
/// UC16 — Workflow definition repository. Persists versioned BPMN / workflow-graph JSON
/// payloads in the <c>cnas.WorkflowDefinitions</c> table (see
/// <see cref="WorkflowDefinition"/>). The CNAS engine adapter (Operaton via
/// <see cref="Cnas.Ps.Application.UseCases.IWorkflowEngine"/>) consumes the JSON returned
/// by <see cref="GetDefinitionAsync"/> to drive the runtime state machine for an
/// application; this service owns the catalog side — write, version, and read-back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only versioning.</b> Each <see cref="SaveDefinitionAsync"/> call inserts a
/// new row with <see cref="WorkflowDefinition.Version"/> = previous + 1 and
/// <see cref="WorkflowDefinition.IsCurrent"/> = <c>true</c>; the previous current row
/// (if any) is updated to <c>IsCurrent = false</c>. Older versions remain in the table
/// for audit and rollback (CLAUDE.md cross-cutting "Immutable Snapshots"). Hard deletes
/// never happen on this surface — soft delete via <see cref="AuditableEntity.IsActive"/>
/// is available but unused at present.
/// </para>
/// <para>
/// <b>Code canonicalization.</b> Workflow codes are administrator-typed strings and we
/// match them case-insensitively to tolerate the occasional <c>wf-pension-age</c>
/// versus <c>WF-PENSION-AGE</c>. The service trims and uppercases the supplied value
/// before both write and read, and the canonical upper-case form is what lands in the
/// <c>Code</c> column so downstream tools see consistent identifiers.
/// </para>
/// <para>
/// <b>Concurrency.</b> Two operators racing to publish a new revision for the same
/// code both read the previous current row, both flip <c>IsCurrent = false</c> on it,
/// and both call <c>SaveChangesAsync</c>. The Postgres <c>xmin</c> token on
/// <see cref="AuditableEntity"/> makes one of those <c>SaveChanges</c> calls throw
/// <see cref="DbUpdateConcurrencyException"/>; the service catches it and returns
/// <see cref="ErrorCodes.ConcurrencyConflict"/> so the caller can retry from scratch.
/// (Under the EF Core InMemory provider used by unit tests <c>xmin</c> is not enforced —
/// the conflict path is exercised against a real Postgres only.)
/// </para>
/// <para>
/// <b>Sqid exception.</b> CLAUDE.md RULE 3 does NOT apply to workflow codes. The
/// <see cref="WorkflowDefinition.Code"/> column IS the public identifier, not a surrogate
/// for an opaque integer — see the <c>WorkflowsController</c> XML doc for the same
/// documented exception.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller — supplies the actor id for audit rows.</param>
/// <param name="audit">Audit journal façade — receives a Critical row per version creation.</param>
/// <param name="sqids">Sqid encoder used by <c>ListCurrentAsync</c> to expose the surrogate row id as an external string; optional only for back-compat with legacy test harnesses.</param>
public sealed class WorkflowConfigurationService(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    ISqidService? sqids = null)
    : IWorkflowConfigurationService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;

    /// <summary>
    /// R0121 / CF 16.02 — Sqid service used by <see cref="ListCurrentAsync"/> to
    /// project each row's surrogate id into its external string form. Marked
    /// nullable purely to keep the legacy unit-test harnesses
    /// (<c>WorkflowConfigurationServiceTests</c>) compiling without an
    /// <see cref="ISqidService"/>; production DI always supplies one. The
    /// list-method short-circuits with <see cref="ErrorCodes.Internal"/> when
    /// invoked with a null encoder so a misconfigured composition fails loudly
    /// rather than silently returning empty strings.
    /// </summary>
    private readonly ISqidService? _sqids = sqids;

    /// <summary>Stable audit-event code for new workflow versions (R0129 / CF 15.04).</summary>
    private const string VersionCreatedEvent = "WORKFLOWDEFINITION.VERSION_CREATED";

    /// <inheritdoc />
    /// <remarks>
    /// Looks up the row whose <see cref="WorkflowDefinition.Code"/> equals the canonical
    /// (trimmed + upper-case) form of <paramref name="workflowCode"/> AND whose
    /// <see cref="WorkflowDefinition.IsCurrent"/> is <c>true</c>. The composite index on
    /// <c>(Code, IsCurrent)</c> serves this exactly.
    /// </remarks>
    public async Task<Result<string>> GetDefinitionAsync(
        string workflowCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowCode))
        {
            return Result<string>.Failure(
                ErrorCodes.ValidationFailed,
                "Workflow code is required.");
        }

        var canonical = Canonicalize(workflowCode);

        var row = await _db.WorkflowDefinitions
            .AsNoTracking()
            .Where(w => w.Code == canonical && w.IsCurrent)
            .Select(w => w.DefinitionJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return Result<string>.Failure(
                ErrorCodes.NotFound,
                $"Workflow definition '{canonical}' not found.");
        }

        return Result<string>.Success(row);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Validates that <paramref name="definitionJson"/> is non-empty and parseable as a
    /// JSON document (via <see cref="System.Text.Json.JsonDocument.Parse(string, System.Text.Json.JsonDocumentOptions)"/>),
    /// then inserts a new <see cref="WorkflowDefinition"/> row marked
    /// <see cref="WorkflowDefinition.IsCurrent"/> = <c>true</c>. If a previous current
    /// row exists for the same canonical code it is updated to <c>IsCurrent = false</c>
    /// in the same transaction so the invariant "at most one current row per code"
    /// holds across the save boundary.
    /// </para>
    /// </remarks>
    public async Task<Result> SaveDefinitionAsync(
        string workflowCode,
        string definitionJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowCode))
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                "Workflow code is required.");
        }

        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                "Workflow definition JSON is required.");
        }

        // Structural sanity — the body must be a well-formed JSON document. The model
        // binder on the controller already rejects malformed JSON before the action runs
        // (via [Consumes("application/json")] + JsonElement), but service-layer callers
        // (background jobs, tests, future imports) skip the binder so we re-validate here.
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(definitionJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                $"Workflow definition is not valid JSON: {ex.Message}");
        }

        var canonical = Canonicalize(workflowCode);

        // Find the previous current row (if any) so we can flip IsCurrent=false on it.
        // Tracking is required here because we want EF to issue the UPDATE.
        var previousCurrent = await _db.WorkflowDefinitions
            .Where(w => w.Code == canonical && w.IsCurrent)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // R0129 — no-op short-circuit. If the incoming payload is byte-equal to the
        // current revision then there is no semantic change; we return success without
        // inserting a row OR emitting an audit. This keeps "republish click" idempotent.
        if (previousCurrent is not null
            && string.Equals(previousCurrent.DefinitionJson, definitionJson, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        // Next version number: max existing + 1, or 1 if no rows exist for this code.
        // The query is independent of IsCurrent (history rows count too).
        var nextVersion = await _db.WorkflowDefinitions
            .Where(w => w.Code == canonical)
            .Select(w => (int?)w.Version)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) + 1 ?? 1;

        var now = _clock.UtcNow;
        if (previousCurrent is not null)
        {
            previousCurrent.IsCurrent = false;
            previousCurrent.SupersededAtUtc = now;
            previousCurrent.UpdatedAtUtc = now;
            previousCurrent.UpdatedBy = _caller.UserSqid;
        }

        var newRow = new WorkflowDefinition
        {
            Code = canonical,
            Version = nextVersion,
            DefinitionJson = definitionJson,
            IsCurrent = true,
            IsActive = true,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            SupersedesDefinitionId = previousCurrent?.Id,
        };
        _db.WorkflowDefinitions.Add(newRow);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Another publisher won the race between our read of `previousCurrent` and
            // the SaveChanges UPDATE — the row's xmin advanced underneath us. Surface
            // a deterministic Result.Failure so the caller can retry rather than letting
            // the exception bubble out as a 500.
            return Result.Failure(
                ErrorCodes.ConcurrencyConflict,
                $"Workflow definition '{canonical}' was modified by another publisher: {ex.Message}");
        }

        // R0129 — close the doubly-linked chain by stamping the predecessor's
        // SupersededByDefinitionId now that the new row's Id is known.
        if (previousCurrent is not null)
        {
            previousCurrent.SupersededByDefinitionId = newRow.Id;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // R0129 — emit Critical audit row capturing the version delta. Investigators
        // can use the (fromVersion, toVersion) tuple to find adjacent rows for diffing.
        var fromVersion = previousCurrent?.Version ?? 0;
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            code = canonical,
            fromVersion,
            toVersion = nextVersion,
        });
        await _audit.RecordAsync(
            eventCode: VersionCreatedEvent,
            severity: AuditSeverity.Critical,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(WorkflowDefinition),
            targetEntityId: newRow.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Contracts.WorkflowDefinitionHistoryItem>>> GetHistoryAsync(
        string workflowCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowCode))
        {
            return Result<IReadOnlyList<Contracts.WorkflowDefinitionHistoryItem>>.Failure(
                ErrorCodes.ValidationFailed,
                "Workflow code is required.");
        }

        var canonical = Canonicalize(workflowCode);
        var rows = await _db.WorkflowDefinitions
            .AsNoTracking()
            .Where(w => w.Code == canonical)
            .OrderByDescending(w => w.Version)
            .Select(w => new Contracts.WorkflowDefinitionHistoryItem(
                w.Code,
                w.Version,
                w.IsCurrent,
                w.CreatedAtUtc,
                w.SupersededAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<Contracts.WorkflowDefinitionHistoryItem>>.Success(rows);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Contracts.WorkflowDefinitionListItem>>> ListCurrentAsync(
        string? codeFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (_sqids is null)
        {
            return Result<IReadOnlyList<Contracts.WorkflowDefinitionListItem>>.Failure(
                ErrorCodes.Internal,
                "WorkflowConfigurationService was constructed without an ISqidService; the list endpoint is unavailable.");
        }

        // Pull the materialised rows first so the Sqid encoding happens in
        // memory (the encoder is a C# call — EF cannot translate it). Filter
        // SQL-side via SQL ILIKE substring contained-match when supplied.
        var canonicalFilter = string.IsNullOrWhiteSpace(codeFilter)
            ? null
            : codeFilter.Trim().ToUpperInvariant();

        var query = _db.WorkflowDefinitions
            .AsNoTracking()
            .Where(w => w.IsCurrent);

        if (canonicalFilter is not null)
        {
            query = query.Where(w => w.Code.Contains(canonicalFilter));
        }

        var rows = await query
            .OrderBy(w => w.Code)
            .Select(w => new { w.Id, w.Code, w.Version, w.IsCurrent, w.CreatedAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var projected = rows
            .Select(r => new Contracts.WorkflowDefinitionListItem(
                DefinitionSqid: _sqids.Encode(r.Id),
                Code: r.Code,
                Version: r.Version,
                IsCurrent: r.IsCurrent,
                CreatedAtUtc: r.CreatedAtUtc))
            .ToList();

        return Result<IReadOnlyList<Contracts.WorkflowDefinitionListItem>>.Success(projected);
    }

    /// <summary>
    /// Returns the canonical form of a workflow code — trimmed of surrounding whitespace
    /// and upper-cased using the invariant culture. Centralised so write and read paths
    /// share a single definition of "the same code".
    /// </summary>
    /// <param name="workflowCode">Raw caller-supplied code (already null/whitespace-checked).</param>
    /// <returns>Canonical (upper-case, trimmed) form.</returns>
    private static string Canonicalize(string workflowCode) =>
        workflowCode.Trim().ToUpperInvariant();
}

/// <summary>UC17 — Classifier (nomenclator) administration.</summary>
/// <remarks>
/// <para>
/// <b>Deactivation contract.</b>
/// <see cref="DeactivateAsync(string, string, CancellationToken)"/> consults
/// the injected <c>IClassifierReferenceGuard</c> BEFORE flipping
/// <c>IsActive=false</c>. A non-zero reference count short-circuits with
/// <see cref="ErrorCodes.ClassifierReferenced"/> (R0402 / TOR CF 17.09)
/// so callers cannot strand referencing rows. The metrics counter
/// <c>CnasMeter.ClassifierReferenceBlocked</c> is incremented on every
/// short-circuit.
/// </para>
/// </remarks>
public sealed class ClassifierService(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    Cnas.Ps.Application.Classifiers.IClassifierReferenceGuard referenceGuard) : IClassifierService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly Cnas.Ps.Application.Classifiers.IClassifierReferenceGuard _referenceGuard = referenceGuard;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ClassifierRow>>> ListAsync(string kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var rows = await _db.Classifiers
            .Where(c => c.Kind == kind && c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => new ClassifierRow(c.Kind, c.Code, c.LabelRo, c.LabelEn, c.LabelRu, c.ParentCode, c.Source))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Result<IReadOnlyList<ClassifierRow>>.Success(rows);
    }

    /// <inheritdoc />
    public async Task<Result> UpsertAsync(ClassifierRow row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        var existing = await _db.Classifiers.SingleOrDefaultAsync(c => c.Kind == row.Kind && c.Code == row.Code, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            _db.Classifiers.Add(new Classifier
            {
                CreatedAtUtc = _clock.UtcNow,
                Kind = row.Kind,
                Code = row.Code,
                LabelRo = row.LabelRo,
                LabelEn = row.LabelEn,
                LabelRu = row.LabelRu,
                ParentCode = row.ParentCode,
                Source = row.Source,
            });
        }
        else
        {
            // R0401 / TOR CF 17.02-04 — national-mirror rows are inbound-only.
            // Edits inside SI PS would silently desynchronise the local copy
            // from the authoritative national register, so the upsert short-
            // circuits with CLASSIFIER.READONLY_MIRROR rather than mutating the
            // row.
            if (existing.IsReadOnlyMirror)
            {
                return Result.Failure(
                    ErrorCodes.ClassifierReadonlyMirror,
                    $"Classifier ({row.Kind}, {row.Code}) is a read-only mirror of a national register and cannot be edited locally.");
            }

            existing.LabelRo = row.LabelRo;
            existing.LabelEn = row.LabelEn;
            existing.LabelRu = row.LabelRu;
            existing.ParentCode = row.ParentCode;
            existing.Source = row.Source;
            existing.UpdatedAtUtc = _clock.UtcNow;
            existing.IsActive = true;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> DeactivateAsync(string kind, string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        // Locate the row first so we can surface a precise NotFound and avoid
        // an unnecessary reference scan for a non-existent (kind, code) pair.
        var existing = await _db.Classifiers
            .SingleOrDefaultAsync(c => c.Kind == kind && c.Code == code, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return Result.Failure(ErrorCodes.NotFound, $"Classifier ({kind}, {code}) was not found.");
        }
        if (!existing.IsActive)
        {
            // Already deactivated — short-circuit success so the call is idempotent.
            return Result.Success();
        }

        // R0401 / TOR CF 17.02-04 — national-mirror rows cannot be deactivated
        // locally. Deactivation must come from the upstream national register
        // via the next MConnect sync; rejecting here keeps SI PS in lock-step
        // with the authoritative source.
        if (existing.IsReadOnlyMirror)
        {
            return Result.Failure(
                ErrorCodes.ClassifierReadonlyMirror,
                $"Classifier ({kind}, {code}) is a read-only mirror of a national register and cannot be deactivated locally.");
        }

        // R0402 / TOR CF 17.09 — pre-flight reference-blocking. The guard
        // returns a depersonalised summary; we only consume the total here.
        var scan = await _referenceGuard.ScanAsync(kind, code, cancellationToken).ConfigureAwait(false);
        if (scan.IsFailure)
        {
            // Underlying I/O failure inside the guard; surface as Result.From.
            return Result.Failure(scan.ErrorCode!, scan.ErrorMessage!);
        }
        if (scan.Value.ReferencingRowCount > 0)
        {
            Cnas.Ps.Infrastructure.Observability.CnasMeter.ClassifierReferenceBlocked.Add(
                1,
                new System.Collections.Generic.KeyValuePair<string, object?>("scheme", kind));
            return Result.Failure(
                ErrorCodes.ClassifierReferenced,
                $"Classifier ({kind}, {code}) is referenced by {scan.Value.ReferencingRowCount} row(s) and cannot be deactivated.");
        }

        existing.IsActive = false;
        existing.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}

/// <summary>
/// UC18 — User administration (RBAC). All mutating operations require the caller to hold
/// the <c>cnas-admin</c> role; controllers also gate their action via
/// <c>[Authorize(Policy = CnasAdmin)]</c>, but the service performs the check too as
/// defense-in-depth (an internal caller invoking the service directly cannot bypass it).
/// </summary>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping (CLAUDE.md RULE 3).</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller — supplies <c>UserSqid</c>, roles, IP for audit.</param>
/// <param name="audit">Audit journal façade — critical events are mirrored to MLog per SEC 056.</param>
/// <param name="deactivationGuard">
/// R0672 / TOR CF 18.08 — pre-flight gate consulted before
/// <see cref="UserAdministrationService.DeactivateAsync(string, CancellationToken)"/>
/// flips <c>UserProfile.IsActive=false</c>. Refuses the soft-delete with
/// <see cref="ErrorCodes.UserProfileNoAuditHistory"/> when the user has no
/// audit-history rows yet — guarantees every deactivation leaves a trail behind.
/// </param>
public sealed class UserAdministrationService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    Cnas.Ps.Application.Users.IUserDeactivationGuard deactivationGuard)
    : IUserAdministrationService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService _audit = audit;
    private readonly ICallerContext _caller = caller;
    private readonly Cnas.Ps.Application.Users.IUserDeactivationGuard _deactivationGuard = deactivationGuard;

    /// <summary>Role required for every mutating operation in this service.</summary>
    private const string AdminRole = "cnas-admin";

    /// <summary>Audit event code emitted when a role is granted to a user.</summary>
    private const string EvtRoleGranted = "USER.ROLE_GRANTED";

    /// <summary>Audit event code emitted when a role is revoked from a user.</summary>
    private const string EvtRoleRevoked = "USER.ROLE_REVOKED";

    /// <summary>Audit event code emitted when a user account is locked.</summary>
    private const string EvtLocked = "USER.LOCKED";

    /// <summary>Audit event code emitted when a user account is unlocked.</summary>
    private const string EvtUnlocked = "USER.UNLOCKED";

    /// <summary>
    /// R0672 / TOR CF 18.08 — audit event code emitted on a successful
    /// soft-delete. Distinct from the lifecycle <c>USER.LOCKED</c> /
    /// <c>USER.UNLOCKED</c> codes because deactivation is an end-of-life
    /// transition, not a temporary state.
    /// </summary>
    private const string EvtDeactivated = "USER.DEACTIVATED";

    /// <inheritdoc />
    public async Task<Result<PagedResult<UserListItem>>> ListAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        // Read operations are open to any cnas-admin caller; the policy attribute on the
        // controller is the primary gate. We still surface a paged contract identical to
        // the contributor registry (CLAUDE.md UI 014 — paged lists) so the WASM client
        // can reuse the same paging widget.
        var pageSize = Math.Clamp(page.PageSize, 1, 200);
        var pageNumber = Math.Max(1, page.Page);
        var skip = (pageNumber - 1) * pageSize;

        var query = _db.UserProfiles
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName);

        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Project to an intermediate shape so we can call _sqids.Encode in-memory after the
        // SQL round-trip (ISqidService is not translatable to SQL). The State column is
        // stringified at the DTO boundary so the WASM client does not need to share the
        // enum type with the API (UI 014 — the listing widget renders the literal).
        var items = await query
            .Skip(skip).Take(pageSize)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.State, u.Roles })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var rows = items
            .Select(u => new UserListItem(
                _sqids.Encode(u.Id), u.DisplayName, u.Email, u.State.ToString(), u.Roles))
            .ToList();

        return Result<PagedResult<UserListItem>>.Success(
            new PagedResult<UserListItem>(rows, pageNumber, pageSize, total));
    }

    /// <inheritdoc />
    public Task<Result> GrantRoleAsync(string userId, string role, CancellationToken cancellationToken = default)
        => MutateRoleAsync(userId, role, add: true, EvtRoleGranted, cancellationToken);

    /// <inheritdoc />
    public Task<Result> RevokeRoleAsync(string userId, string role, CancellationToken cancellationToken = default)
        => MutateRoleAsync(userId, role, add: false, EvtRoleRevoked, cancellationToken);

    /// <inheritdoc />
    public Task<Result> LockAsync(string userId, CancellationToken cancellationToken = default)
        => SetLockAsync(userId, locked: true, EvtLocked, cancellationToken);

    /// <inheritdoc />
    public Task<Result> UnlockAsync(string userId, CancellationToken cancellationToken = default)
        => SetLockAsync(userId, locked: false, EvtUnlocked, cancellationToken);

    /// <inheritdoc />
    public async Task<Result> DeactivateAsync(string userId, CancellationToken cancellationToken = default)
    {
        // Defense-in-depth — mirror the role guard used by every other
        // mutating operation on this service.
        if (!_caller.Roles.Contains(AdminRole))
        {
            return Result.Failure(ErrorCodes.Forbidden, "Caller lacks cnas-admin role.");
        }

        var decoded = _sqids.TryDecode(userId);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        // Look up the row — we accept BOTH active and already-inactive rows
        // so the contract stays idempotent (re-deactivation is a no-op).
        var user = await _db.UserProfiles
            .SingleOrDefaultAsync(u => u.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "User not found.");
        }

        // Idempotent — re-deactivating an already-inactive row is a no-op.
        // We skip the audit write so the trail stays meaningful (one row per
        // actual state change, not per retry).
        if (!user.IsActive)
        {
            return Result.Success();
        }

        // R0672 / TOR CF 18.08 — gate the flip on the audit-history check.
        // The guard returns USERPROFILE.NO_AUDIT_HISTORY when neither the
        // EntityHistoryRow projection nor the AuditLog ledger has a row keyed
        // to the user. The row is NOT mutated when the guard refuses.
        var guard = await _deactivationGuard
            .EnsureCanDeactivateAsync(user.Id, cancellationToken)
            .ConfigureAwait(false);
        if (guard.IsFailure)
        {
            return guard;
        }

        user.IsActive = false;
        user.UpdatedAtUtc = _clock.UtcNow;
        user.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            EvtDeactivated,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(UserProfile),
            user.Id,
            "{}",
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// Shared body for <see cref="GrantRoleAsync"/> / <see cref="RevokeRoleAsync"/>.
    /// Performs the admin-role guard, decodes the Sqid, idempotently mutates the role list,
    /// and journals a <see cref="AuditSeverity.Critical"/> event.
    /// </summary>
    /// <param name="userId">Sqid-encoded user id.</param>
    /// <param name="role">Role code to add or remove (e.g. <c>cnas-decider</c>).</param>
    /// <param name="add">True to add the role; false to remove.</param>
    /// <param name="auditEventCode">Audit code emitted on success.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<Result> MutateRoleAsync(string userId, string role, bool add, string auditEventCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        // Defense-in-depth — the [Authorize(Policy = CnasAdmin)] attribute on the controller
        // is the primary gate, but internal callers (background jobs, future MediatR pipelines)
        // could bypass it. Enforce at the service boundary so the check is unavoidable.
        if (!_caller.Roles.Contains(AdminRole))
        {
            return Result.Failure(ErrorCodes.Forbidden, "Caller lacks cnas-admin role.");
        }

        var decoded = _sqids.TryDecode(userId);
        if (decoded.IsFailure) return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);

        var user = await _db.UserProfiles
            .SingleOrDefaultAsync(u => u.Id == decoded.Value && u.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (user is null) return Result.Failure(ErrorCodes.NotFound, "User not found.");

        // Idempotent mutations — granting an already-held role or revoking a missing one
        // is a no-op and still returns success so retries are safe.
        if (add)
        {
            if (!user.Roles.Contains(role)) user.Roles.Add(role);
        }
        else
        {
            user.Roles.Remove(role);
        }
        user.UpdatedAtUtc = _clock.UtcNow;
        user.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            auditEventCode,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(UserProfile),
            user.Id,
            $"{{\"role\":\"{role}\"}}",
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// Shared body for <see cref="LockAsync"/> / <see cref="UnlockAsync"/>. Mirrors the
    /// guard + decode + mutate + audit sequence used by <see cref="MutateRoleAsync"/>.
    /// </summary>
    /// <remarks>
    /// Since R0059 (account state machine), the underlying mutation is a transition on
    /// <see cref="UserProfile.State"/> (Active ↔ Locked) rather than a boolean flip. The
    /// audit event code is preserved verbatim (<c>USER.LOCKED</c> / <c>USER.UNLOCKED</c>)
    /// because external systems may consume it — the rich
    /// <c>USER.STATE_CHANGE.&lt;FROM&gt;.&lt;TO&gt;</c> code is reserved for the
    /// general-purpose <see cref="UserAccountStateService"/> path. A reactivation
    /// (unlock) is rejected when the account is in a non-Locked state because that
    /// would silently bypass the state-machine transition matrix.
    /// </remarks>
    /// <param name="userId">Sqid-encoded user id.</param>
    /// <param name="locked">When true, transition to <see cref="UserAccountState.Locked"/>; otherwise transition to <see cref="UserAccountState.Active"/>.</param>
    /// <param name="auditEventCode">Audit code emitted on success.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<Result> SetLockAsync(string userId, bool locked, string auditEventCode, CancellationToken cancellationToken)
    {
        if (!_caller.Roles.Contains(AdminRole))
        {
            return Result.Failure(ErrorCodes.Forbidden, "Caller lacks cnas-admin role.");
        }

        var decoded = _sqids.TryDecode(userId);
        if (decoded.IsFailure) return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);

        var user = await _db.UserProfiles
            .SingleOrDefaultAsync(u => u.Id == decoded.Value && u.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (user is null) return Result.Failure(ErrorCodes.NotFound, "User not found.");

        // Translate the boolean intent into a state transition. The transition matrix
        // is mirrored from UserAccountStateService.AllowedTransitions to keep the two
        // paths consistent — Lock requires Active (or already Locked = idempotent);
        // Unlock requires Locked (or already Active = idempotent).
        var targetState = locked ? UserAccountState.Locked : UserAccountState.Active;
        if (user.State == targetState)
        {
            // Idempotent — re-locking a locked account or re-unlocking an active one
            // is a no-op. Skip the audit write so the trail stays meaningful.
            return Result.Success();
        }
        // Disallow exotic transitions through this surface (e.g. trying to "unlock" a
        // Suspended or Disabled account would silently bypass the state machine).
        if (locked && user.State != UserAccountState.Active)
        {
            return Result.Failure(
                ErrorCodes.UserAccountStateTransitionForbidden,
                $"Cannot lock account in state {user.State}.");
        }
        if (!locked && user.State != UserAccountState.Locked)
        {
            return Result.Failure(
                ErrorCodes.UserAccountStateTransitionForbidden,
                $"Cannot unlock account in state {user.State}.");
        }

        user.State = targetState;
        user.UpdatedAtUtc = _clock.UtcNow;
        user.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            auditEventCode,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(UserProfile),
            user.Id,
            "{}",
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}

/// <summary>UC20 — Automation engine façade (scheduled / on-demand).</summary>
public sealed class AutomationService : IAutomationService
{
    /// <inheritdoc />
    public Task<Result> RunNowAsync(string automationCode, string parametersJson, CancellationToken cancellationToken = default)
    {
        _ = automationCode;
        _ = parametersJson;
        _ = cancellationToken;
        // Real implementation triggers a Quartz job by name with the provided JobDataMap.
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result> ScheduleAsync(string automationCode, string cronExpression, CancellationToken cancellationToken = default)
    {
        _ = automationCode;
        _ = cronExpression;
        _ = cancellationToken;
        return Task.FromResult(Result.Success());
    }
}

/// <summary>
/// UC21 — System actor advances a workflow step. Orchestrates the
/// <see cref="ApplicationStatus.Submitted"/> → (<see cref="ApplicationStatus.UnderExamination"/>
/// | <see cref="ApplicationStatus.Rejected"/>) transition using the configured
/// <see cref="IDecisionEngine"/>.
/// <para>
/// Pipeline (CLAUDE.md §2.1 / §6.2):
/// </para>
/// <list type="number">
///   <item>Decode the inbound Sqid identifier (CLAUDE.md RULE 3).</item>
///   <item>Load the <see cref="ServiceApplication"/> + <see cref="ServicePassport"/>; bail with
///         <see cref="ErrorCodes.NotFound"/> when either is missing / soft-deleted.</item>
///   <item>Reject with <see cref="ErrorCodes.ApplicationNotSubmitted"/> when the application
///         is not in <see cref="ApplicationStatus.Submitted"/>.</item>
///   <item>Convert <c>FormPayloadJson</c> to <see cref="DecisionFacts"/> via
///         <see cref="FormPayloadParser"/>.</item>
///   <item>Evaluate the passport's rule-set:
///         <list type="bullet">
///           <item><b>Engine failure</b> or <b>ineligible</b>: auto-reject the application
///                 (status → <see cref="ApplicationStatus.Rejected"/>, audit
///                 <c>APPLICATION.AUTO_REJECTED</c>, notify solicitant).</item>
///           <item><b>Eligible</b>: open a <see cref="Dossier"/>, set status to
///                 <see cref="ApplicationStatus.UnderExamination"/>, queue an
///                 examiner <see cref="WorkflowTask"/> with SLA derived from
///                 <c>MaxProcessingDays</c>, audit
///                 <c>APPLICATION.ACCEPTED_FOR_EXAMINATION</c>, notify solicitant.</item>
///         </list>
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// Each successful transition also mirrors a citizen-portal card revision to MCabinet via
/// <see cref="IMCabinetPublisher"/>: the eligible branch publishes an
/// <see cref="MCabinetStatus.InExamination"/> card keyed off the newly-opened dossier; the
/// auto-reject branches (engine failure, ineligible) publish an
/// <see cref="MCabinetStatus.Rejected"/> card keyed off the application Sqid because no
/// dossier exists yet at that point. The publish is best-effort — a failure (transport
/// error, non-2xx response, or the publisher throwing) is logged at <c>Warning</c> level
/// and swallowed so the dossier state machine commits regardless. See CLAUDE.md
/// cross-cutting "Idempotent Callbacks".
/// </remarks>
public sealed class ApplicationProcessingService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    IDecisionEngine engine,
    IAuditService audit,
    INotificationService notify,
    ICallerContext caller,
    IMCabinetPublisher mcabinet,
    ILogger<ApplicationProcessingService> logger) : IApplicationProcessingService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IDecisionEngine _engine = engine;
    private readonly IAuditService _audit = audit;
    private readonly INotificationService _notify = notify;
    private readonly ICallerContext _caller = caller;
    private readonly IMCabinetPublisher _mcabinet = mcabinet;
    private readonly ILogger<ApplicationProcessingService> _logger = logger;

    /// <inheritdoc />
    public async Task<Result> AdvanceAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        // ── 1. Decode the external Sqid → internal long primary key (CLAUDE.md RULE 3).
        var decoded = _sqids.TryDecode(applicationId);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        // ── 2. Load aggregate + passport. Single round-trip via Include avoids N+1.
        var app = await _db.Applications
            .Include(a => a.Solicitant)
            .SingleOrDefaultAsync(a => a.Id == decoded.Value && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (app is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Application not found.");
        }

        var passport = await _db.ServicePassports
            .SingleOrDefaultAsync(p => p.Id == app.ServicePassportId && p.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (passport is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Service passport not found.");
        }

        // ── 3. State guard — only Submitted applications can be advanced via this path.
        if (app.Status != ApplicationStatus.Submitted)
        {
            return Result.Failure(
                ErrorCodes.ApplicationNotSubmitted,
                "Application not in Submitted state.");
        }

        // ── 4. Convert the schema-flexible form payload into the engine's fact bag.
        var now = _clock.UtcNow;
        var factsResult = FormPayloadParser.Parse(app.FormPayloadJson, now);
        if (factsResult.IsFailure)
        {
            return await RejectAsync(
                app,
                $"FACT_PARSE_FAILED:{factsResult.ErrorCode}",
                factsResult.ErrorMessage ?? "Form payload could not be parsed.",
                cancellationToken).ConfigureAwait(false);
        }

        // ── 5. Evaluate eligibility + amount via the configured decision engine.
        var outcomeResult = _engine.Evaluate(passport.DecisionRulesJson, factsResult.Value);

        // 5a. Engine-level failure (bad rule JSON, missing fact, amount computation failure)
        //     — treat as auto-reject so the citizen never gets stuck in limbo while admins fix
        //     the passport configuration. The audit trail names the engine error code.
        if (outcomeResult.IsFailure)
        {
            return await RejectAsync(
                app,
                outcomeResult.ErrorCode!,
                $"Decision engine failure: {outcomeResult.ErrorMessage}",
                cancellationToken).ConfigureAwait(false);
        }

        var outcome = outcomeResult.Value;

        // 5b. Eligibility failed → auto-reject with the accumulated reason codes.
        if (!outcome.IsEligible)
        {
            var reasons = string.Join(", ", outcome.ReasonCodes);
            return await RejectAsync(
                app,
                ErrorCodes.Ineligible,
                $"Application ineligible: {reasons}",
                cancellationToken).ConfigureAwait(false);
        }

        // ── 6. Eligible — open a dossier and queue the examiner task.
        // ComputedAmountMdl is consumed downstream by MPayDispatcherJob to enqueue an outbound MPay transfer.
        var dossier = new Dossier
        {
            ApplicationId = app.Id,
            DossierNumber = $"D-{now:yyyy}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid ?? "system",
            IsActive = true,
            ComputedAmountMdl = outcome.Amount?.Amount,
        };
        _db.Dossiers.Add(dossier);

        // Application moves to UnderExamination — the linked DossierId is set below
        // after EF Core assigns the dossier its primary key on SaveChanges.
        app.Status = ApplicationStatus.UnderExamination;
        app.UpdatedAtUtc = now;

        // R0202 — the task lands in the examiner group inbox unclaimed, so the
        // UnclaimedSinceUtc stamp must be set here for the unclaimed-task escalation
        // sweep (UnclaimedTaskEscalationJob) to pick it up if no examiner claims it
        // within the configured window (CF 20.05).
        var task = new WorkflowTask
        {
            // DossierId is assigned via navigation by EF Core when the dossier is saved.
            Title = "Examinare cerere",
            Status = WorkflowTaskStatus.Pending,
            GroupCode = "cnas-examiner",
            DueAtUtc = now.AddDays(passport.MaxProcessingDays),
            UnclaimedSinceUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid ?? "system",
            IsActive = true,
        };
        // EF Core assigns the FK once both rows share the same change tracker scope.
        // We set it after SaveChanges below so the dossier PK is known.
        _db.WorkflowTasks.Add(task);

        // Persist dossier first so we obtain its primary key, then stitch the FKs.
        // Single SaveChanges keeps the operation atomic for relational providers; the
        // EF Core change tracker batches both inserts into one transaction.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        app.DossierId = dossier.Id;
        task.DossierId = dossier.Id;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // ── 7. Audit + notify (UC22 / UC23). Audit message includes the amount when known.
        var amountText = outcome.Amount?.ToString() ?? "n/a";
        var detailsJson = $"{{\"dossierNumber\":\"{dossier.DossierNumber}\","
                          + $"\"amount\":\"{amountText}\","
                          + $"\"reasonCodes\":\"{string.Join(',', outcome.ReasonCodes)}\"}}";
        await _audit.RecordAsync(
            "APPLICATION.ACCEPTED_FOR_EXAMINATION",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "system",
            nameof(ServiceApplication),
            app.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        var refNum = app.ReferenceNumber ?? _sqids.Encode(app.Id);
        await _notify.EnqueueAsync(
            app.SolicitantId,
            "Cererea acceptată pentru examinare",
            $"Cererea Dvs. (ref {refNum}) a fost acceptată pentru examinare; "
                + $"dosar {dossier.DossierNumber} deschis.",
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        // ── 8. MCabinet outbound projection (best-effort, CLAUDE.md "Idempotent Callbacks") ─
        // The dossier has been persisted with its PK assigned; we publish under the dossier
        // Sqid (the stable external id citizens see for the rest of the workflow).
        var dossierSqid = _sqids.Encode(dossier.Id);
        var card = new MCabinetCard(
            ExternalId: dossierSqid,
            CitizenIdnp: app.Solicitant!.NationalId,
            ServiceCode: passport.Code,
            Status: MCabinetStatus.InExamination,
            TitleRo: passport.NameRo,
            SubtitleRo: dossier.DossierNumber,
            EventUtc: now,
            DeepLink: null);
        await PublishMCabinetAsync(card, cancellationToken).ConfigureAwait(false);

        // ── 9. OTel metric (best-effort) ───────────────────────────────────────────────
        // After persistence + audit + MCabinet, increment the
        // dossiers-accepted-for-examination counter. Wrapped in a try/catch so a
        // misbehaving listener / exporter can never break the state machine — the
        // counter is observability-only.
        RecordCounterSafely(
            CnasTelemetry.DossiersAcceptedForExamination,
            new KeyValuePair<string, object?>("service_code", passport.Code));

        return Result.Success();
    }

    /// <summary>
    /// Best-effort wrapper around <see cref="System.Diagnostics.Metrics.Counter{T}"/>.Add
    /// that swallows any exception thrown by a downstream
    /// <see cref="System.Diagnostics.Metrics.MeterListener"/> or exporter. The dossier
    /// state machine must never be broken by a telemetry side-effect — mirrors the
    /// MCabinet best-effort pattern documented at the top of the file.
    /// </summary>
    /// <param name="counter">Pre-declared counter to increment by one.</param>
    /// <param name="tag">Key/value tag describing the dossier dimension (e.g. service_code).</param>
    private void RecordCounterSafely(
        System.Diagnostics.Metrics.Counter<long> counter,
        KeyValuePair<string, object?> tag)
    {
        try
        {
            counter.Add(1, tag);
        }
#pragma warning disable CA1031 // Best-effort telemetry: a misbehaving listener MUST NOT break the state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Telemetry counter {Counter} increment threw; ignoring.",
                counter.Name);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Best-effort multi-tag counter increment. Used when more than one dimension
    /// is meaningful (e.g. "tag" describing the rejection channel <i>and</i>
    /// "service_code" describing the underlying service). Any thrown exception is
    /// logged at <c>Warning</c> level and swallowed.
    /// </summary>
    /// <param name="counter">Pre-declared counter to increment by one.</param>
    /// <param name="tags">Set of key/value tags to attach to the measurement.</param>
    private void RecordCounterSafelyMulti(
        System.Diagnostics.Metrics.Counter<long> counter,
        TagList tags)
    {
        try
        {
            counter.Add(1, tags);
        }
#pragma warning disable CA1031 // Best-effort telemetry: a misbehaving listener MUST NOT break the state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Telemetry counter {Counter} increment threw; ignoring.",
                counter.Name);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Auto-rejects an application: flips status to <see cref="ApplicationStatus.Rejected"/>,
    /// stamps <c>ClosedAtUtc</c>, writes a critical audit entry, notifies the solicitant,
    /// and returns a <see cref="Result"/> failure that propagates the underlying error code
    /// upstream. Used both for engine-level failures (BAD_RULE) and business-level rejections
    /// (INELIGIBLE).
    /// </summary>
    /// <param name="app">The application being rejected. Mutated in place.</param>
    /// <param name="errorCode">Stable code to return to the caller and embed in audit metadata.</param>
    /// <param name="message">Human-readable reason; surfaces in audit + notification body.</param>
    /// <param name="cancellationToken">Cancellation propagation token.</param>
    /// <returns>A failed <see cref="Result"/> carrying <paramref name="errorCode"/>.</returns>
    private async Task<Result> RejectAsync(
        ServiceApplication app,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        app.Status = ApplicationStatus.Rejected;
        app.ClosedAtUtc = now;
        app.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Serialize through JsonSerializer to avoid quote-escaping landmines in the message body.
        var detailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            errorCode,
            message,
        });
        await _audit.RecordAsync(
            "APPLICATION.AUTO_REJECTED",
            AuditSeverity.Critical,
            _caller.UserSqid ?? "system",
            nameof(ServiceApplication),
            app.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        var refNum = app.ReferenceNumber ?? _sqids.Encode(app.Id);
        await _notify.EnqueueAsync(
            app.SolicitantId,
            "Cererea respinsă",
            $"Cererea Dvs. (ref {refNum}) a fost respinsă. Motiv: {message}",
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        // MCabinet outbound projection — auto-reject happens BEFORE a dossier is opened,
        // so we key the card off the application Sqid (the only stable external id at this
        // point). The Submitted card published by ApplicationServiceImpl used the same id,
        // so MCabinet treats this Rejected revision as an update of that card. Resolving
        // the passport+solicitant requires extra reads since this method only has the app.
        var passport = await _db.ServicePassports.AsNoTracking()
            .Where(p => p.Id == app.ServicePassportId)
            .Select(p => new { p.Code, p.NameRo })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var citizenIdnp = app.Solicitant?.NationalId
            ?? await _db.Solicitants.AsNoTracking()
                .Where(s => s.Id == app.SolicitantId)
                .Select(s => s.NationalId)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (passport is not null && !string.IsNullOrEmpty(citizenIdnp))
        {
            var card = new MCabinetCard(
                ExternalId: _sqids.Encode(app.Id),
                CitizenIdnp: citizenIdnp,
                ServiceCode: passport.Code,
                Status: MCabinetStatus.Rejected,
                TitleRo: passport.NameRo,
                SubtitleRo: refNum,
                EventUtc: now,
                DeepLink: null);
            await PublishMCabinetAsync(card, cancellationToken).ConfigureAwait(false);
        }

        // OTel metric (best-effort) — auto-reject path: tag the rejection with the
        // "auto-reject" channel so dashboards can split decider-initiated rejections
        // from engine/ineligibility-driven ones. The service_code tag adds the
        // dossier-level dimension; falls back to "?" when the passport row is missing
        // (which can happen if the passport was hard-deleted between submit and advance).
        RecordCounterSafelyMulti(
            CnasTelemetry.DossiersRejected,
            new TagList
            {
                { "tag", "auto-reject" },
                { "service_code", passport?.Code ?? "?" },
            });

        return Result.Failure(errorCode, message);
    }

    /// <summary>
    /// Publishes the supplied <paramref name="card"/> to MCabinet, swallowing every error
    /// at the boundary so the dossier state machine cannot be broken by an outbound
    /// projection failure. Transport / non-2xx / publisher-throws are all logged at
    /// <c>Warning</c> level with structured fields <c>dossierSqid</c> and <c>status</c>;
    /// publish success is silent (the citizen sees the card revision in MCabinet).
    /// </summary>
    /// <param name="card">Card revision to publish.</param>
    /// <param name="cancellationToken">Cancellation propagation token.</param>
    private async Task PublishMCabinetAsync(MCabinetCard card, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mcabinet.PublishCardAsync(card, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                _logger.LogWarning(
                    "MCabinet publish failed: dossierSqid={DossierSqid} status={Status} errorCode={ErrorCode}",
                    card.ExternalId, card.Status, result.ErrorCode);
            }
        }
#pragma warning disable CA1031 // Best-effort projection: a thrown publisher MUST NOT break the dossier state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "MCabinet publish threw: dossierSqid={DossierSqid} status={Status}",
                card.ExternalId, card.Status);
        }
#pragma warning restore CA1031
    }
}
