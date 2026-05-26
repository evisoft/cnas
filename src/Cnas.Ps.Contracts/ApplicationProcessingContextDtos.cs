using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0701 / TOR CF 21.01-02 — single-payload "open this application dossier"
/// aggregator. Returned by <c>GET /api/applications/{sqid}/processing-context</c>
/// so the future CNAS staff processing UI can populate the entire application
/// detail screen with one round-trip instead of firing N parallel REST calls
/// (applicant profile + tasks + decisions + attachments + audit timeline + …).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only orchestrator.</b> The DTO never mutates state — it is the
/// server-side primitive the future UI will call before letting the operator
/// drive any specific action (assign, draft, approve, …). Every individual
/// mutation continues to flow through its own existing service / endpoint;
/// this surface only AGGREGATES the current state.
/// </para>
/// <para>
/// <b>Sensitivity floor.</b> The payload concatenates citizen PII
/// (applicant name, contacts, addresses), audit-timeline rows, and
/// decision-draft handles — every individual sub-DTO already carries the
/// appropriate label. The aggregate is floored at
/// <see cref="SensitivityLabel.Confidential"/> so the sensitivity middleware
/// stamps the response correctly even before walking the nested types.
/// </para>
/// <para>
/// <b>UTC discipline.</b> Every timestamp is UTC per CLAUDE.md cross-cutting;
/// the snapshot itself stamps <see cref="GeneratedAtUtc"/> so the UI can render
/// "Last loaded N seconds ago" without re-issuing the call.
/// </para>
/// </remarks>
/// <param name="ApplicationSqid">
/// Sqid-encoded id of the <c>ServiceApplication</c> the payload describes.
/// </param>
/// <param name="Status">
/// Stable string name of the current <c>ApplicationStatus</c> (e.g.
/// <c>"Submitted"</c>, <c>"UnderExamination"</c>). The UI renders this verbatim;
/// translation is the consumer's responsibility.
/// </param>
/// <param name="Applicant">
/// Aggregated applicant profile (citizen + linked entities) as of the snapshot
/// instant. See <see cref="ApplicantProfileDto"/>.
/// </param>
/// <param name="OpenTasks">
/// Workflow tasks for this application whose status is one of
/// <c>{Pending, InProgress, Overdue}</c>. Completed / Cancelled tasks are
/// filtered out — the UI surfaces them through a separate "task history" call.
/// </param>
/// <param name="DecisionDrafts">
/// Decision documents (i.e. <c>Document</c> rows with <c>Kind=Decision</c>)
/// attached to the dossier and not yet signed. A finalised (signed) decision
/// no longer surfaces here; the UI renders it under <see cref="Attachments"/>
/// via the standard final-document surface.
/// </param>
/// <param name="Attachments">
/// Top 20 attachments (ordered by <see cref="AttachmentBriefDto.UploadedAtUtc"/>
/// DESC) owned by the application. Always capped at 20; the full ledger is
/// retrievable through the existing attachment list endpoint when needed.
/// </param>
/// <param name="AuditTimeline">
/// Last 50 audit-log rows whose <c>TargetEntity=ServiceApplication</c> and
/// <c>TargetEntityId=applicationId</c>. Detail strings are PII-redacted via
/// R0185 / R0182 and capped at 200 characters.
/// </param>
/// <param name="SuggestedNextActions">
/// Heuristic-derived stable action codes (e.g. <c>"AssignExaminer"</c>,
/// <c>"RequestMissingDocuments"</c>, <c>"DraftDecision"</c>) reflecting what
/// the staff user is most likely to need next given the application's
/// current state. The UI translates the codes; this surface stays stable.
/// </param>
/// <param name="HasUnappliedPrefill">
/// <c>true</c> when the R0552 pre-fill service reports at least one candidate
/// field that has not been merged into the application's payload yet — the UI
/// surfaces a banner suggesting the operator re-run pre-fill. <c>false</c> when
/// pre-fill reports nothing or when the underlying gateways fail soft.
/// </param>
/// <param name="GeneratedAtUtc">
/// UTC instant the service composed this snapshot.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record ApplicationProcessingContextDto(
    string ApplicationSqid,
    string Status,
    ApplicantProfileDto Applicant,
    IReadOnlyList<WorkflowTaskBriefDto> OpenTasks,
    IReadOnlyList<DecisionBriefDto> DecisionDrafts,
    IReadOnlyList<AttachmentBriefDto> Attachments,
    IReadOnlyList<AuditTimelineEntryDto> AuditTimeline,
    IReadOnlyList<string> SuggestedNextActions,
    bool HasUnappliedPrefill,
    DateTime GeneratedAtUtc);

/// <summary>
/// R0701 — Applicant + linked-entities snapshot returned inside the
/// <see cref="ApplicationProcessingContextDto"/>. Carries citizen-display data
/// (name, contact, addresses, civil status, recent activity periods) and the
/// public-correlation hash prefix the future UI uses to confirm "yes, the
/// dossier you are about to act on is the citizen you expect" without
/// re-exposing the IDNP.
/// </summary>
/// <param name="SolicitantSqid">Sqid of the underlying <c>Solicitant</c> row.</param>
/// <param name="DisplayName">
/// Full display name as captured on the <c>Solicitant</c> at submission time —
/// PII (<see cref="SensitivityLabel.Confidential"/>).
/// </param>
/// <param name="NationalIdHashPrefix">
/// First 8 hex characters of the deterministic IDNP HMAC-SHA256 hash. Always
/// exactly 8 hex chars (<c>0-9a-f</c>); never reveals the IDNP itself.
/// Internal classification because the prefix is correlatable but not PII.
/// </param>
/// <param name="Email">Primary email if any (Confidential).</param>
/// <param name="PhoneE164">Primary phone in E.164 if any (Confidential).</param>
/// <param name="CurrentAddress">
/// The most recently superseded-into address row (the row whose <c>ValidToUtc</c>
/// is null), or <c>null</c> when the citizen has no address on file.
/// </param>
/// <param name="CurrentContact">
/// The most recently superseded-into contact row, or <c>null</c> when nothing
/// is on file.
/// </param>
/// <param name="CurrentCivilStatus">
/// The most recently superseded-into civil-status row, or <c>null</c> when
/// nothing is on file.
/// </param>
/// <param name="RecentActivityPeriods">
/// Up to 3 most recent activity-period rows (newest first). Returned even when
/// some are closed — the UI renders an end-date pill when <c>ValidToUtc</c> is
/// non-null.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record ApplicantProfileDto(
    string SolicitantSqid,
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string NationalIdHashPrefix,
    string? Email,
    string? PhoneE164,
    ContributorAddressDto? CurrentAddress,
    ContributorContactDto? CurrentContact,
    ContributorCivilStatusDto? CurrentCivilStatus,
    IReadOnlyList<ContributorActivityPeriodDto> RecentActivityPeriods);

/// <summary>
/// R0701 — brief projection of one <c>WorkflowTask</c> row inside the
/// processing-context payload. Only the fields the future UI's task-strip
/// renders are surfaced — the full task ledger lives behind
/// <c>ITaskInboxService</c>.
/// </summary>
/// <param name="TaskSqid">Sqid of the <c>WorkflowTask</c> row.</param>
/// <param name="Title">Display title of the task.</param>
/// <param name="AssigneeUserSqid">
/// Sqid of the currently-assigned user, or <c>null</c> when the task lives in
/// a group inbox.
/// </param>
/// <param name="Status">
/// Stable string name of the current <c>WorkflowTaskStatus</c>
/// (Pending / InProgress / Overdue inside this projection).
/// </param>
/// <param name="CreatedAtUtc">UTC instant the task was created.</param>
/// <param name="DueAtUtc">
/// Optional SLA deadline. The UI renders an "Overdue" badge when
/// <see cref="Status"/> is <c>Overdue</c> regardless of this value.
/// </param>
public sealed record WorkflowTaskBriefDto(
    string TaskSqid,
    string Title,
    string? AssigneeUserSqid,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? DueAtUtc);

/// <summary>
/// R0701 — brief projection of one decision-draft <c>Document</c> (Kind=Decision,
/// IsSigned=false). The full document download / preview path goes through the
/// existing document endpoints; this projection only carries the handle the UI
/// needs to render the draft strip.
/// </summary>
/// <param name="DecisionSqid">Sqid of the <c>Document</c> row backing the draft.</param>
/// <param name="Status">
/// Stable status label — currently always <c>"Draft"</c>; reserved for future
/// states (e.g. "PendingApproval") once the decision-document state machine
/// grows.
/// </param>
/// <param name="CreatedAtUtc">UTC instant the draft was created.</param>
/// <param name="DraftedByUserSqid">
/// Sqid of the operator who created the draft. Sourced from the row's
/// <c>CreatedByUserId</c>; <c>null</c> when the draft was generated by a
/// system job.
/// </param>
public sealed record DecisionBriefDto(
    string DecisionSqid,
    string Status,
    DateTime CreatedAtUtc,
    string? DraftedByUserSqid);

/// <summary>
/// R0701 — brief projection of one <c>AttachmentRecord</c> owned by the
/// application. The processing-context endpoint returns up to 20 of these
/// ordered newest-first; the full ledger is reachable via
/// <c>IAttachmentService</c>.
/// </summary>
/// <param name="AttachmentSqid">Sqid of the <c>AttachmentRecord</c> row.</param>
/// <param name="FileName">Sanitised filename.</param>
/// <param name="ContentType">Detected MIME type (from magic-byte sniff).</param>
/// <param name="SizeBytes">Size of the stored blob in bytes.</param>
/// <param name="UploadedAtUtc">UTC instant of the upload.</param>
public sealed record AttachmentBriefDto(
    string AttachmentSqid,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTime UploadedAtUtc);

/// <summary>
/// R0701 — single row in the application's audit timeline. Each row is one
/// <c>AuditLog</c> entry whose target was this application. The detail string
/// is PII-redacted via the standard R0185 / R0182 pipeline and truncated to at
/// most 200 characters so the timeline strip stays cheap to render.
/// </summary>
/// <param name="CreatedAtUtc">UTC instant of the event (audit row's EventAtUtc).</param>
/// <param name="EventCode">Stable event code (e.g. <c>APPLICATION.SUBMITTED</c>).</param>
/// <param name="Severity">
/// Stable string name of the <c>AuditSeverity</c> (Information / Notice /
/// Sensitive / Critical) — projected as the enum's name so the wire shape
/// stays stable across protobuf-like consumers.
/// </param>
/// <param name="ActorUserSqid">
/// Sqid of the user who triggered the event. <c>null</c> when the actor is a
/// background job / system caller.
/// </param>
/// <param name="Detail">
/// Short human-readable detail of the event, derived from the audit row's
/// <c>DetailsJson</c> after PII redaction. Capped at 200 characters.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record AuditTimelineEntryDto(
    DateTime CreatedAtUtc,
    string EventCode,
    string Severity,
    string? ActorUserSqid,
    string Detail);
