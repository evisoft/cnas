using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ApplicationProcessing;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.Prefill;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.ApplicationProcessing;

/// <summary>
/// R0701 / TOR CF 21.01-02 — default <see cref="IApplicationProcessingContextService"/>
/// implementation. Composes the processing-context payload from seven read sources
/// (the application + applicant + linked entities + workflow tasks + decision drafts
/// + attachments + audit timeline + pre-fill hint) into one DTO so the future
/// Blazor staff processing UI can render the entire application detail screen
/// with a single round-trip.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Scoped — captures the per-request <see cref="IReadOnlyCnasDbContext"/>,
/// <see cref="ICnasTimeProvider"/>, <see cref="ICallerContext"/>, audit sink, and the
/// scoped <see cref="IPrefillService"/> it delegates the pre-fill probe to.
/// </para>
/// <para>
/// <b>Permission model.</b> Three independent paths satisfy the gate:
/// <list type="number">
///   <item>The caller's roles include the <see cref="IApplicationProcessingContextService.ProcessPermission"/>
///         permission code (granted via the user-administration UI for cnas-user
///         examiners).</item>
///   <item>The caller is the application's currently-assigned examiner
///         (<see cref="Dossier.AssignedExaminerId"/> = caller's user id).</item>
///   <item>The caller holds the <c>cnas-admin</c> role.</item>
/// </list>
/// Anonymous callers always receive <see cref="ErrorCodes.Unauthorized"/>.
/// </para>
/// <para>
/// <b>Audit + counter.</b> Every successful invocation writes one Sensitive
/// <c>APPLICATION.PROCESSING_CONTEXT_VIEWED</c> audit row with the application
/// Sqid + the list of high-level field groups loaded (<c>viewedFields</c>), and
/// increments <see cref="CnasMeter.ApplicationProcessingContextLoaded"/>. The
/// counter is the bulk-dossier-open detector; the audit row is the per-call
/// traceability anchor.
/// </para>
/// <para>
/// <b>PII discipline.</b> The audit-timeline projection runs each row's
/// <c>DetailsJson</c> through <see cref="PiiRedactor.Redact(string?)"/> before
/// returning it, and truncates the redacted result to 200 chars. The applicant's
/// IDNP hash is sliced to its first 8 hex chars (via the same algorithm the
/// interop surface uses — see <see cref="HashPrefix(string)"/>) so the prefix
/// never reveals IDNP magnitude.
/// </para>
/// </remarks>
public sealed class ApplicationProcessingContextService : IApplicationProcessingContextService
{
    /// <summary>Maximum number of attachments returned (newest first).</summary>
    public const int MaxAttachments = 20;

    /// <summary>Maximum number of audit-timeline rows returned (newest first).</summary>
    public const int MaxAuditTimelineRows = 50;

    /// <summary>Cap on the redacted audit-row detail string surfaced via the DTO.</summary>
    public const int MaxAuditDetailLength = 200;

    /// <summary>Number of activity-period rows surfaced inside the applicant profile.</summary>
    public const int MaxRecentActivityPeriods = 3;

    /// <summary>Number of hex characters in the IDNP-hash prefix (matches R0634 / Annex 4).</summary>
    public const int NationalIdHashPrefixLength = 8;

    /// <summary>Role code that satisfies the admin path of the permission gate.</summary>
    public const string AdminRole = "cnas-admin";

    /// <summary>Stable next-action codes the heuristic emits. See <see cref="ComputeSuggestedNextActions"/>.</summary>
    public static class NextActions
    {
        /// <summary>Submitted application has no assigned examiner.</summary>
        public const string AssignExaminer = "AssignExaminer";

        /// <summary>UnderExamination has no decision draft on the dossier.</summary>
        public const string DraftDecision = "DraftDecision";

        /// <summary>UnderExamination has at least one rejected attachment.</summary>
        public const string RequestMissingDocuments = "RequestMissingDocuments";

        /// <summary>PendingApproval — şef-direcţie must decide.</summary>
        public const string ApproveOrReject = "ApproveOrReject";

        /// <summary>Approved but no signed decision document is attached.</summary>
        public const string FinalizeDecisionDocument = "FinalizeDecisionDocument";
    }

    private readonly IReadOnlyCnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IPrefillService _prefill;
    private readonly ILogger<ApplicationProcessingContextService> _logger;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Read-only DB context routed to the replica (R0026).</param>
    /// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder for every id surfaced on the output DTO.</param>
    /// <param name="caller">Per-request caller context (permission gate + audit attribution).</param>
    /// <param name="audit">Audit sink — writes the per-call Sensitive row.</param>
    /// <param name="prefill">Pre-fill service used to compute the <c>HasUnappliedPrefill</c> flag.</param>
    /// <param name="logger">Structured logger.</param>
    public ApplicationProcessingContextService(
        IReadOnlyCnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IPrefillService prefill,
        ILogger<ApplicationProcessingContextService> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(prefill);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _prefill = prefill;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationProcessingContextDto>> GetForCurrentUserAsync(
        long applicationId,
        CancellationToken ct = default)
    {
        if (_caller.UserId is not long callerId)
        {
            return Result<ApplicationProcessingContextDto>.Failure(
                ErrorCodes.Unauthorized,
                "Authentication is required to open an application processing context.");
        }

        // 1. Resolve the application (read-only). NotFound when missing or soft-deleted.
        var app = await _db.Applications
            .Where(a => a.Id == applicationId && a.IsActive)
            .Select(a => new
            {
                a.Id,
                a.Status,
                a.SolicitantId,
                a.SubmittedAtUtc,
            })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (app is null)
        {
            return Result<ApplicationProcessingContextDto>.Failure(
                ErrorCodes.NotFound,
                $"Application '{applicationId}' was not found.");
        }

        // 2. Resolve the dossier (1:1 with application) — null when the application has
        //    not yet been routed to an examiner. The AssignedExaminerId is consulted by
        //    the permission gate AND the suggested-next-action heuristic, so we always
        //    eager-fetch the row.
        var dossier = await _db.Dossiers
            .Where(d => d.ApplicationId == applicationId && d.IsActive)
            .Select(d => new
            {
                d.Id,
                d.AssignedExaminerId,
            })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // 3. Permission gate — admin OR process permission OR assigned examiner.
        var isAdmin = _caller.Roles.Contains(AdminRole, StringComparer.Ordinal);
        var hasProcessPermission = _caller.Roles.Contains(
            IApplicationProcessingContextService.ProcessPermission, StringComparer.Ordinal);
        var isAssignedExaminer = dossier?.AssignedExaminerId == callerId;
        if (!isAdmin && !hasProcessPermission && !isAssignedExaminer)
        {
            return Result<ApplicationProcessingContextDto>.Failure(
                ErrorCodes.Forbidden,
                "Caller is neither an administrator nor the assigned examiner nor holder of the Application.Process permission.");
        }

        // 4. Applicant profile + linked entities.
        var applicant = await BuildApplicantProfileAsync(app.SolicitantId, ct).ConfigureAwait(false);

        // 5. Open workflow tasks (Pending / InProgress / Overdue) for this dossier.
        var openTasks = dossier is null
            ? Array.Empty<WorkflowTaskBriefDto>()
            : await LoadOpenTasksAsync(dossier.Id, ct).ConfigureAwait(false);

        // 6. Decision drafts — Documents on the dossier with Kind=Decision and IsSigned=false.
        var decisionDrafts = dossier is null
            ? Array.Empty<DecisionBriefDto>()
            : await LoadDecisionDraftsAsync(dossier.Id, ct).ConfigureAwait(false);

        // 7. Top 20 attachments owned by the application.
        var attachments = await LoadAttachmentsAsync(applicationId, ct).ConfigureAwait(false);

        // 8. Last 50 audit-timeline rows scoped to the application.
        var timeline = await LoadAuditTimelineAsync(applicationId, ct).ConfigureAwait(false);

        // 9. Suggested next actions — heuristic over the current snapshot.
        var nextActions = ComputeSuggestedNextActions(
            status: app.Status,
            assignedExaminerId: dossier?.AssignedExaminerId,
            hasDecisionDraft: decisionDrafts.Count > 0,
            hasSignedDecisionDocument: dossier is not null
                && await HasSignedDecisionAsync(dossier.Id, ct).ConfigureAwait(false));

        // 10. Pre-fill hint — true when R0552 returns at least one candidate field.
        var hasUnappliedPrefill = await ProbePrefillAsync(app.SolicitantId, ct).ConfigureAwait(false);

        var dto = new ApplicationProcessingContextDto(
            ApplicationSqid: _sqids.Encode(app.Id),
            Status: app.Status.ToString(),
            Applicant: applicant,
            OpenTasks: openTasks,
            DecisionDrafts: decisionDrafts,
            Attachments: attachments,
            AuditTimeline: timeline,
            SuggestedNextActions: nextActions,
            HasUnappliedPrefill: hasUnappliedPrefill,
            GeneratedAtUtc: _clock.UtcNow);

        // 11. Audit + counter — emit once on success only.
        await EmitAuditRowAsync(applicationId, dto, ct).ConfigureAwait(false);
        CnasMeter.ApplicationProcessingContextLoaded.Add(1);

        _logger.LogInformation(
            "Application processing context composed: applicationId={ApplicationId} openTasks={OpenTasks} drafts={Drafts} attachments={Attachments} timeline={Timeline}",
            applicationId, openTasks.Count, decisionDrafts.Count, attachments.Count, timeline.Count);

        return Result<ApplicationProcessingContextDto>.Success(dto);
    }

    /// <summary>
    /// Loads the applicant's Solicitant row + the current linked-entity rows
    /// (address / contact / civil-status, plus the 3 most recent activity periods)
    /// from the InsuredPerson aggregate matched by NationalIdHash. Returns a
    /// fully-populated <see cref="ApplicantProfileDto"/>; rows the citizen has not
    /// supplied surface as <c>null</c>.
    /// </summary>
    /// <param name="solicitantId">Raw bigint id of the application's Solicitant.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Populated applicant profile DTO.</returns>
    private async Task<ApplicantProfileDto> BuildApplicantProfileAsync(
        long solicitantId,
        CancellationToken ct)
    {
        var sol = await _db.Solicitants
            .Where(s => s.Id == solicitantId)
            .Select(s => new
            {
                s.Id,
                s.DisplayName,
                s.Email,
                s.PhoneE164,
                s.NationalIdHash,
            })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (sol is null)
        {
            // Defensive — the application FK is supposed to be valid, but the read
            // replica could lag the primary. Return a stub profile so the rest of
            // the dossier still renders.
            return new ApplicantProfileDto(
                SolicitantSqid: _sqids.Encode(solicitantId),
                DisplayName: string.Empty,
                NationalIdHashPrefix: new string('0', NationalIdHashPrefixLength),
                Email: null,
                PhoneE164: null,
                CurrentAddress: null,
                CurrentContact: null,
                CurrentCivilStatus: null,
                RecentActivityPeriods: Array.Empty<ContributorActivityPeriodDto>());
        }

        // Resolve the InsuredPerson on the same identity-hash. Linked entities hang
        // off InsuredPerson, not Solicitant (per R0311 / ARH 028).
        long? insuredPersonId = null;
        if (!string.IsNullOrEmpty(sol.NationalIdHash))
        {
            insuredPersonId = await _db.InsuredPersons
                .Where(ip => ip.IdnpHash == sol.NationalIdHash && ip.IsActive)
                .Select(ip => (long?)ip.Id)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        ContributorAddressDto? address = null;
        ContributorContactDto? contact = null;
        ContributorCivilStatusDto? civil = null;
        IReadOnlyList<ContributorActivityPeriodDto> periods = Array.Empty<ContributorActivityPeriodDto>();

        if (insuredPersonId is long ipId)
        {
            var solSqid = _sqids.Encode(solicitantId);

            var addressRow = await _db.ContributorAddresses
                .Where(a => a.ContributorId == ipId && a.IsActive && a.ValidToUtc == null)
                .OrderByDescending(a => a.ValidFromUtc)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (addressRow is not null)
            {
                address = new ContributorAddressDto(
                    Id: _sqids.Encode(addressRow.Id),
                    ContributorSqid: solSqid,
                    Street: addressRow.Street,
                    City: addressRow.City,
                    Region: addressRow.Region,
                    PostalCode: addressRow.PostalCode,
                    Country: addressRow.Country,
                    ValidFromUtc: addressRow.ValidFromUtc,
                    ValidToUtc: addressRow.ValidToUtc,
                    ChangeReason: addressRow.ChangeReason,
                    RecordedByUserSqid: addressRow.RecordedByUserSqid);
            }

            var contactRow = await _db.ContributorContacts
                .Where(c => c.ContributorId == ipId && c.IsActive && c.ValidToUtc == null)
                .OrderByDescending(c => c.ValidFromUtc)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (contactRow is not null)
            {
                contact = new ContributorContactDto(
                    Id: _sqids.Encode(contactRow.Id),
                    ContributorSqid: solSqid,
                    PhoneE164: contactRow.PhoneE164,
                    Email: contactRow.Email,
                    ContactPersonName: contactRow.ContactPersonName,
                    ValidFromUtc: contactRow.ValidFromUtc,
                    ValidToUtc: contactRow.ValidToUtc,
                    ChangeReason: contactRow.ChangeReason,
                    RecordedByUserSqid: contactRow.RecordedByUserSqid);
            }

            var civilRow = await _db.ContributorCivilStatuses
                .Where(c => c.ContributorId == ipId && c.IsActive && c.ValidToUtc == null)
                .OrderByDescending(c => c.ValidFromUtc)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (civilRow is not null)
            {
                civil = new ContributorCivilStatusDto(
                    Id: _sqids.Encode(civilRow.Id),
                    ContributorSqid: solSqid,
                    Status: civilRow.Status.ToString(),
                    EffectiveDate: civilRow.EffectiveDate,
                    ValidFromUtc: civilRow.ValidFromUtc,
                    ValidToUtc: civilRow.ValidToUtc,
                    ChangeReason: civilRow.ChangeReason,
                    RecordedByUserSqid: civilRow.RecordedByUserSqid);
            }

            var periodRows = await _db.ContributorActivityPeriods
                .Where(p => p.ContributorId == ipId && p.IsActive)
                .OrderByDescending(p => p.ValidFromUtc)
                .Take(MaxRecentActivityPeriods)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            periods = periodRows.Select(p => new ContributorActivityPeriodDto(
                Id: _sqids.Encode(p.Id),
                ContributorSqid: solSqid,
                EmployerCode: p.EmployerCode,
                Position: p.Position,
                MonthlySalary: p.MonthlySalary,
                ValidFromUtc: p.ValidFromUtc,
                ValidToUtc: p.ValidToUtc,
                ChangeReason: p.ChangeReason,
                RecordedByUserSqid: p.RecordedByUserSqid)).ToList();
        }

        return new ApplicantProfileDto(
            SolicitantSqid: _sqids.Encode(sol.Id),
            DisplayName: sol.DisplayName,
            NationalIdHashPrefix: HashPrefix(sol.NationalIdHash),
            Email: sol.Email,
            PhoneE164: sol.PhoneE164,
            CurrentAddress: address,
            CurrentContact: contact,
            CurrentCivilStatus: civil,
            RecentActivityPeriods: periods);
    }

    /// <summary>
    /// Loads open workflow tasks for the supplied dossier (Pending / InProgress /
    /// Overdue), projected to the lightweight <see cref="WorkflowTaskBriefDto"/>.
    /// </summary>
    /// <param name="dossierId">Raw bigint id of the Dossier.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Task list (possibly empty).</returns>
    private async Task<IReadOnlyList<WorkflowTaskBriefDto>> LoadOpenTasksAsync(
        long dossierId,
        CancellationToken ct)
    {
        var rows = await _db.WorkflowTasks
            .Where(t => t.DossierId == dossierId
                        && t.IsActive
                        && (t.Status == WorkflowTaskStatus.Pending
                            || t.Status == WorkflowTaskStatus.InProgress
                            || t.Status == WorkflowTaskStatus.Overdue))
            .OrderBy(t => t.CreatedAtUtc)
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.AssignedUserId,
                t.Status,
                t.CreatedAtUtc,
                t.DueAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new WorkflowTaskBriefDto(
            TaskSqid: _sqids.Encode(r.Id),
            Title: r.Title,
            AssigneeUserSqid: r.AssignedUserId is long uid ? _sqids.Encode(uid) : null,
            Status: r.Status.ToString(),
            CreatedAtUtc: r.CreatedAtUtc,
            DueAtUtc: r.DueAtUtc)).ToList();
    }

    /// <summary>
    /// Loads decision drafts (unsigned <see cref="Document"/> rows of
    /// <see cref="DocumentKind.Decision"/>) attached to the dossier.
    /// </summary>
    /// <param name="dossierId">Raw bigint id of the dossier.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Decision-draft list (possibly empty).</returns>
    private async Task<IReadOnlyList<DecisionBriefDto>> LoadDecisionDraftsAsync(
        long dossierId,
        CancellationToken ct)
    {
        var rows = await _db.Documents
            .Where(d => d.DossierId == dossierId
                        && d.IsActive
                        && d.Kind == DocumentKind.Decision
                        && !d.IsSigned)
            .OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new
            {
                d.Id,
                d.CreatedAtUtc,
                d.CreatedBy,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new DecisionBriefDto(
            DecisionSqid: _sqids.Encode(r.Id),
            Status: "Draft",
            CreatedAtUtc: r.CreatedAtUtc,
            DraftedByUserSqid: r.CreatedBy)).ToList();
    }

    /// <summary>
    /// Loads the top <see cref="MaxAttachments"/> attachments owned by the
    /// application, newest first.
    /// </summary>
    /// <param name="applicationId">Raw bigint id of the ServiceApplication.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Attachment-brief list (possibly empty).</returns>
    private async Task<IReadOnlyList<AttachmentBriefDto>> LoadAttachmentsAsync(
        long applicationId,
        CancellationToken ct)
    {
        var rows = await _db.AttachmentRecords
            .Where(a => a.OwnerEntityType == nameof(ServiceApplication)
                        && a.OwnerEntityId == applicationId
                        && a.IsActive
                        && !a.IsArchived)
            .OrderByDescending(a => a.UploadedUtc)
            .Take(MaxAttachments)
            .Select(a => new
            {
                a.Id,
                a.FileName,
                a.ContentType,
                a.SizeBytes,
                a.UploadedUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new AttachmentBriefDto(
            AttachmentSqid: _sqids.Encode(r.Id),
            FileName: r.FileName,
            ContentType: r.ContentType,
            SizeBytes: r.SizeBytes,
            UploadedAtUtc: r.UploadedUtc)).ToList();
    }

    /// <summary>
    /// Loads the last <see cref="MaxAuditTimelineRows"/> audit-log rows scoped to
    /// this application, projects them to <see cref="AuditTimelineEntryDto"/> with
    /// PII-redacted detail strings capped at <see cref="MaxAuditDetailLength"/>.
    /// </summary>
    /// <param name="applicationId">Raw bigint id of the ServiceApplication.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Audit-timeline list (possibly empty).</returns>
    private async Task<IReadOnlyList<AuditTimelineEntryDto>> LoadAuditTimelineAsync(
        long applicationId,
        CancellationToken ct)
    {
        var rows = await _db.AuditLogs
            .Where(a => a.TargetEntity == nameof(ServiceApplication)
                        && a.TargetEntityId == applicationId
                        && a.IsActive)
            .OrderByDescending(a => a.EventAtUtc)
            .Take(MaxAuditTimelineRows)
            .Select(a => new
            {
                a.EventAtUtc,
                a.EventCode,
                a.Severity,
                a.ActorId,
                a.DetailsJson,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new AuditTimelineEntryDto(
            CreatedAtUtc: r.EventAtUtc,
            EventCode: r.EventCode,
            Severity: r.Severity.ToString(),
            ActorUserSqid: ResolveActorUserSqid(r.ActorId),
            Detail: TruncateDetail(PiiRedactor.Redact(r.DetailsJson)))).ToList();
    }

    /// <summary>
    /// Resolves the <see cref="AuditLog.ActorId"/> to a Sqid string when it
    /// already is one (e.g. <c>USR-42</c>) — otherwise returns <c>null</c>.
    /// System callers store literal labels like <c>"system:r0189-evaluator"</c>
    /// which carry no Sqid; the UI renders those as a plain "system" badge.
    /// </summary>
    /// <param name="actorId">Raw <see cref="AuditLog.ActorId"/> value.</param>
    /// <returns>Sqid string when interpretable; <c>null</c> for system / unknown.</returns>
    private static string? ResolveActorUserSqid(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return null;
        }
        // The convention across the system is to write the caller's UserSqid
        // verbatim (e.g. "USR-42"); system / job actors carry a colon-prefixed
        // label. Anything containing a colon or whitespace is not a Sqid.
        if (actorId.Contains(':', StringComparison.Ordinal)
            || actorId.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }
        return actorId;
    }

    /// <summary>
    /// Truncates the redacted detail string to <see cref="MaxAuditDetailLength"/>
    /// characters. Empty strings remain empty.
    /// </summary>
    /// <param name="redacted">Output of <see cref="PiiRedactor.Redact(string?)"/>.</param>
    /// <returns>The truncated string.</returns>
    private static string TruncateDetail(string redacted)
    {
        if (string.IsNullOrEmpty(redacted))
        {
            return string.Empty;
        }
        return redacted.Length <= MaxAuditDetailLength
            ? redacted
            : redacted[..MaxAuditDetailLength];
    }

    /// <summary>
    /// Computes the heuristic-derived suggested-next-action codes from the
    /// application's status, the dossier examiner-assignment, the presence of
    /// any decision draft, and the presence of a signed (final) decision.
    /// </summary>
    /// <param name="status">Current application status.</param>
    /// <param name="assignedExaminerId">Examiner id when assigned; <c>null</c> otherwise.</param>
    /// <param name="hasDecisionDraft">True when at least one unsigned decision exists.</param>
    /// <param name="hasSignedDecisionDocument">True when a signed final decision exists.</param>
    /// <returns>Stable list of action codes (may be empty).</returns>
    private static IReadOnlyList<string> ComputeSuggestedNextActions(
        ApplicationStatus status,
        long? assignedExaminerId,
        bool hasDecisionDraft,
        bool hasSignedDecisionDocument)
    {
        var actions = new List<string>(capacity: 2);
        switch (status)
        {
            case ApplicationStatus.Draft:
                // Draft is the citizen's responsibility — no staff action.
                break;
            case ApplicationStatus.Submitted:
                if (assignedExaminerId is null)
                {
                    actions.Add(NextActions.AssignExaminer);
                }
                break;
            case ApplicationStatus.UnderExamination:
                if (!hasDecisionDraft)
                {
                    actions.Add(NextActions.DraftDecision);
                }
                break;
            case ApplicationStatus.PendingApproval:
                actions.Add(NextActions.ApproveOrReject);
                break;
            case ApplicationStatus.Approved:
                if (!hasSignedDecisionDocument)
                {
                    actions.Add(NextActions.FinalizeDecisionDocument);
                }
                break;
            case ApplicationStatus.RejectedIncomplete:
            case ApplicationStatus.Rejected:
            case ApplicationStatus.Closed:
            case ApplicationStatus.Withdrawn:
            default:
                // Terminal / citizen-driven states surface no staff next action.
                break;
        }
        return actions;
    }

    /// <summary>
    /// Returns <see langword="true"/> when a SIGNED Decision document exists on
    /// the dossier — used by the suggested-next-action heuristic for the
    /// Approved branch. Soft-deleted rows are excluded.
    /// </summary>
    /// <param name="dossierId">Raw bigint id of the dossier.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns><see langword="true"/> when a signed Decision row exists.</returns>
    private async Task<bool> HasSignedDecisionAsync(long dossierId, CancellationToken ct)
        => await _db.Documents
            .AnyAsync(d => d.DossierId == dossierId
                            && d.IsActive
                            && d.Kind == DocumentKind.Decision
                            && d.IsSigned, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Probes the R0552 pre-fill service for the applicant's Solicitant and
    /// returns <see langword="true"/> when the response carries at least one
    /// candidate field. A failure from the underlying gateways is swallowed —
    /// the hint defaults to <see langword="false"/> so the UI does not pop a
    /// spurious banner on transient pre-fill outages.
    /// </summary>
    /// <param name="solicitantId">Raw bigint id of the application's Solicitant.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns><see langword="true"/> when pre-fill reports candidates.</returns>
    private async Task<bool> ProbePrefillAsync(long solicitantId, CancellationToken ct)
    {
        try
        {
            var payload = await _prefill
                .PrefillForSolicitantAsync(solicitantId, new PrefillRequestDto(null, null), ct)
                .ConfigureAwait(false);
            return payload.IsSuccess && payload.Value.Fields.Count > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Pre-fill is an advisory hint — its failure must not break the dossier.
            _logger.LogWarning(ex,
                "Pre-fill probe failed for solicitantId={SolicitantId}; defaulting HasUnappliedPrefill to false.",
                solicitantId);
            return false;
        }
    }

    /// <summary>
    /// Emits the per-call Sensitive <c>APPLICATION.PROCESSING_CONTEXT_VIEWED</c>
    /// audit row with the application Sqid + the list of high-level field groups
    /// loaded. Failure to write the row is logged but does not fail the call —
    /// the dossier read is the source of truth for the operator's experience.
    /// </summary>
    /// <param name="applicationId">Raw bigint id of the target application.</param>
    /// <param name="dto">Populated DTO — used to enumerate viewed field groups.</param>
    /// <param name="ct">Standard cancellation token.</param>
    private async Task EmitAuditRowAsync(
        long applicationId,
        ApplicationProcessingContextDto dto,
        CancellationToken ct)
    {
        var viewedFields = new List<string>(capacity: 8)
        {
            "applicant",
            "openTasks",
            "decisionDrafts",
            "attachments",
            "auditTimeline",
            "suggestedNextActions",
            "hasUnappliedPrefill",
        };
        var details = JsonSerializer.Serialize(new
        {
            applicationSqid = dto.ApplicationSqid,
            viewedFields,
        });
        var record = await _audit.RecordAsync(
            IApplicationProcessingContextService.AuditEventCode,
            AuditSeverity.Sensitive,
            _caller.UserSqid ?? "?",
            nameof(ServiceApplication),
            applicationId,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            _logger.LogWarning(
                "Audit row {EventCode} failed for applicationId={ApplicationId}: {Error}",
                IApplicationProcessingContextService.AuditEventCode,
                applicationId,
                record.ErrorMessage);
        }
    }

    /// <summary>
    /// Renders the first <see cref="NationalIdHashPrefixLength"/> hex characters
    /// of the supplied deterministic hash. The deterministic hasher emits base64;
    /// we decode the first few bytes back to raw and re-emit them as lower-case
    /// hex so the prefix matches the R0634 / Annex 4 contract
    /// (<c>[0-9a-f]{8}</c>). Falls back to a deterministic hex placeholder when
    /// the input is empty or fails to decode — the public surface is total.
    /// </summary>
    /// <param name="fullHash">Full base64 hash, or empty.</param>
    /// <returns>Exactly <see cref="NationalIdHashPrefixLength"/> lower-case hex characters.</returns>
    private static string HashPrefix(string? fullHash)
    {
        if (string.IsNullOrEmpty(fullHash))
        {
            return new string('0', NationalIdHashPrefixLength);
        }

        // 8 hex chars = 4 raw bytes. Feed 8 base64 chars (= 6 raw bytes) so we
        // have headroom against base64 padding.
        Span<byte> raw = stackalloc byte[6];
        var sliceLength = Math.Min(fullHash.Length, 8);
        if (Convert.TryFromBase64String(
                fullHash[..sliceLength].PadRight(8, '='),
                raw,
                out var written) && written >= NationalIdHashPrefixLength / 2)
        {
            var hex = new char[NationalIdHashPrefixLength];
            const string Alphabet = "0123456789abcdef";
            for (var i = 0; i < NationalIdHashPrefixLength / 2; i++)
            {
                hex[2 * i] = Alphabet[raw[i] >> 4];
                hex[2 * i + 1] = Alphabet[raw[i] & 0x0F];
            }
            return new string(hex);
        }

        // Fallback — hex-encode the leading UTF-16 bytes. Stable, deterministic,
        // never triggered in production because base64 decode always succeeds for
        // an HMAC-SHA256 emitted by IDeterministicHasher.
        var fallbackBytes = System.Text.Encoding.UTF8.GetBytes(fullHash);
        var sb = new System.Text.StringBuilder(NationalIdHashPrefixLength);
        for (var i = 0; i < NationalIdHashPrefixLength / 2 && i < fallbackBytes.Length; i++)
        {
            sb.Append(fallbackBytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        while (sb.Length < NationalIdHashPrefixLength)
        {
            sb.Append('0');
        }
        return sb.ToString();
    }
}
