using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Application-layer abstraction over the EF Core <c>DbContext</c>. Service code depends on
/// this interface; the concrete <c>CnasDbContext</c> lives in Infrastructure.
/// </summary>
public interface ICnasDbContext
{
    /// <summary>Solicitants — applicants for CNAS services.</summary>
    DbSet<Solicitant> Solicitants { get; }

    /// <summary>Contributors (Plătitori) registered with CNAS.</summary>
    DbSet<Contributor> Contributors { get; }

    /// <summary>Insured persons (Persoane asigurate).</summary>
    DbSet<InsuredPerson> InsuredPersons { get; }

    /// <summary>Applications (Cereri) submitted by Solicitants.</summary>
    DbSet<ServiceApplication> Applications { get; }

    /// <summary>
    /// R0321 / R0224 / UI 008 — immutable form-payload snapshots captured for every
    /// save (autosave tick OR manual save) of a <see cref="ServiceApplication"/>. The
    /// citizen can revert to any prior version while the application is still editable.
    /// See <see cref="ApplicationVersion"/> for the natural-key, pruning, and dedup
    /// contracts.
    /// </summary>
    DbSet<ApplicationVersion> ApplicationVersions { get; }

    /// <summary>Dossiers (Dosare) opened in response to Applications.</summary>
    DbSet<Dossier> Dossiers { get; }

    /// <summary>Documents stored inside a Dossier.</summary>
    DbSet<Document> Documents { get; }

    /// <summary>Report definitions.</summary>
    DbSet<Report> Reports { get; }

    /// <summary>User profiles with role + group assignments.</summary>
    DbSet<UserProfile> UserProfiles { get; }

    /// <summary>Notifications dispatched to users.</summary>
    DbSet<Notification> Notifications { get; }

    /// <summary>Workflow tasks (Sarcini).</summary>
    DbSet<WorkflowTask> WorkflowTasks { get; }

    /// <summary>Service passport definitions (UC15).</summary>
    DbSet<ServicePassport> ServicePassports { get; }

    /// <summary>Reference data (clasificatoare, nomenclatoare).</summary>
    DbSet<Classifier> Classifiers { get; }

    /// <summary>Audit log records (UC23).</summary>
    DbSet<AuditLog> AuditLogs { get; }

    /// <summary>
    /// Dead-letter-queue entries — one row per Quartz job execution that failed
    /// definitively after retries (CLAUDE.md §6.2). Read by ops dashboards and the
    /// admin replay endpoint; written only by <c>FailedJobListener</c>.
    /// </summary>
    DbSet<FailedJob> FailedJobs { get; }

    /// <summary>
    /// R0137 — application-level immutability ledger over object-storage keys. Written
    /// by <c>IFileImmutabilityMarker</c> and consulted by <c>IFileImmutabilityGuard</c>
    /// before <see cref="IFileStorage.DeleteAsync"/> attempts a delete.
    /// </summary>
    DbSet<FileImmutabilityRecord> FileImmutabilityRecords { get; }

    /// <summary>
    /// Versioned BPMN / workflow-graph JSON revisions (UC16). Append-only: each
    /// <c>SaveDefinitionAsync</c> inserts a new row and flips <c>IsCurrent</c> on the
    /// previous current row to <c>false</c>. Historical versions remain queryable for
    /// audit and rollback. See <see cref="WorkflowDefinition"/> for the snapshot
    /// semantics and concurrency contract.
    /// </summary>
    DbSet<WorkflowDefinition> WorkflowDefinitions { get; }

    /// <summary>
    /// Versioned operator-uploaded DOCX templates (UC17 phase 2A). Append-only: each
    /// upload inserts a new row and flips <c>IsCurrent</c> on the previous current row
    /// for the same code to <c>false</c>. Distinct from the DI-baked
    /// <c>IDocxTemplate</c> singletons; the admin catalog endpoint unions both sources
    /// behind a single contract. See <see cref="DocumentTemplate"/> for the snapshot
    /// semantics and the persistent-wins collision rule.
    /// </summary>
    DbSet<DocumentTemplate> DocumentTemplates { get; }

    /// <summary>
    /// R0133 / CF 17.16 — per-language variants of a <see cref="DocumentTemplate"/>. At
    /// most one row per (template, language) pair; translated variants start with
    /// <c>IsApproved=false</c> until an admin signs off. The renderer falls back to the
    /// template's <see cref="DocumentTemplate.DefaultLanguage"/> when the requested
    /// locale's variant is missing or unapproved. See <see cref="TemplateVariant"/> for
    /// the approval and fallback contract.
    /// </summary>
    DbSet<TemplateVariant> TemplateVariants { get; }

    /// <summary>
    /// R2003 / R0133 — persisted "this template is missing an approved variant
    /// for language X" findings produced by the nightly coverage scan job. The
    /// open-finding registry is deduped on
    /// <c>(TemplateId, MissingLanguage, Acknowledged=false)</c> so repeated
    /// scans of the same gap don't pile up duplicates. See
    /// <see cref="TemplateLanguageCoverageFinding"/> for the lifecycle.
    /// </summary>
    DbSet<TemplateLanguageCoverageFinding> TemplateLanguageCoverageFindings { get; }

    /// <summary>
    /// MPay payment orders originated by CNAS. One row per citizen payment ceremony —
    /// the service layer mints the natural-key <see cref="MPayOrder.OrderId"/>, inserts
    /// the row immediately before posting to MPay, and the inbound callback controller
    /// updates the row when MPay confirms settlement. The store enforces
    /// "Idempotent Callbacks" (CLAUDE.md cross-cutting) — a retried confirm with the
    /// same payment reference is a no-op success.
    /// </summary>
    DbSet<MPayOrder> MPayOrders { get; }

    /// <summary>
    /// Pending sensitive admin actions awaiting a second-administrator approval
    /// (R0058 / SEC 027). The maker-checker service persists one row per
    /// <c>SubmitAsync</c>, the approve/reject endpoints update the row's status, and
    /// a background sweeper flips stale rows to <c>Expired</c>. See
    /// <see cref="PendingAdminAction"/> for the lifecycle contract.
    /// </summary>
    DbSet<PendingAdminAction> PendingAdminActions { get; }

    /// <summary>
    /// Opaque refresh tokens issued by the R0053 token pipeline (CLAUDE.md §5.3 /
    /// SEC 018). One row per refresh token, hashed at rest; a logout or reuse-detected
    /// event revokes every row sharing the same <see cref="RefreshToken.FamilyId"/>.
    /// See <see cref="RefreshToken"/> for the rotation + reuse-detection contract.
    /// </summary>
    DbSet<RefreshToken> RefreshTokens { get; }

    /// <summary>
    /// R2264 / SEC 017 + R2267 / SEC 020 — one row per authenticated user session,
    /// driving the concurrent-session-limit enforcer and the manual / auto session-
    /// lock primitives. Inserted by the auth pipeline at sign-in, mutated by
    /// <c>ISessionLimitEnforcer</c> / <c>ISessionLockService</c>, swept by the
    /// <c>SessionAutoLockJob</c>. See <see cref="UserSession"/> for the lifecycle.
    /// </summary>
    DbSet<UserSession> UserSessions { get; }

    /// <summary>
    /// User-saved registry searches (R0165 / CF 03.06). Each row is owned by the
    /// creating user (<see cref="SavedSearch.OwnerUserId"/>) and may be unilaterally
    /// published to colleagues via <see cref="SavedSearch.IsShared"/>; non-owners
    /// receive READ access exclusively to published rows. See <see cref="SavedSearch"/>
    /// for the access rules, the natural-key uniqueness contract, and the opaque
    /// <c>FilterJson</c> payload semantics.
    /// </summary>
    DbSet<SavedSearch> SavedSearches { get; }

    /// <summary>
    /// Singleton-row checkpoint table backing the SIEM CEF / syslog forwarder
    /// (R0190 / SEC 049). One row per environment, keyed by the literal
    /// <c>"default"</c>; <see cref="SiemForwarderState.LastForwardedAuditId"/> is the
    /// resume anchor used by the polling job to avoid re-emitting already-forwarded
    /// audit rows. See <see cref="SiemForwarderState"/> for the singleton pattern and
    /// the failure-handling contract.
    /// </summary>
    DbSet<SiemForwarderState> SiemForwarderStates { get; }

    /// <summary>
    /// Persisted security-alert notification rules evaluated by the
    /// <c>SecurityAlertEvaluatorJob</c> background job (R0189 / SEC 048). Each row
    /// describes an event-code pattern, a rolling-window threshold, and a recipient
    /// group; when the threshold is met inside the window the evaluator queues an
    /// in-app notification per recipient, writes a <c>SECURITY_ALERT.FIRED</c> audit
    /// row, and stamps the rule's cooldown. See <see cref="SecurityAlertRule"/> for
    /// the full contract.
    /// </summary>
    DbSet<SecurityAlertRule> SecurityAlertRules { get; }

    /// <summary>
    /// Singleton-row checkpoint table backing the security-alert evaluator background
    /// job (R0189 / SEC 048). One row per environment, keyed by the literal
    /// <c>"default"</c>; <see cref="SecurityAlertEvaluatorState.LastEvaluatedAuditId"/>
    /// is the resume anchor used by the evaluator to avoid re-scoring already-scanned
    /// audit rows. See <see cref="SecurityAlertEvaluatorState"/> for the singleton
    /// pattern and the failure-handling contract.
    /// </summary>
    DbSet<SecurityAlertEvaluatorState> SecurityAlertEvaluatorStates { get; }

    /// <summary>
    /// Admin-configurable audit-policy registry (R0182 / SEC 042). Each row defines a
    /// module/screen/data-category filter, an event-code regex, and the resulting
    /// override / suppression / extra-redact behaviour applied by the drainer at flush
    /// time. See <see cref="AuditPolicy"/> for the resolution algorithm and the
    /// suppression safeguard.
    /// </summary>
    DbSet<AuditPolicy> AuditPolicies { get; }

    /// <summary>
    /// Cross-page bulk-selection handles (R0166 / TOR CF 03.11 / UI 015). Each row
    /// captures a registry + opaque filter envelope + hand-curated include/exclude id
    /// list; the bulk-operation runner re-resolves the filter against the live DB at
    /// run time. See <see cref="BulkSelection"/> for the ownership / single-use /
    /// expiry contract.
    /// </summary>
    DbSet<BulkSelection> BulkSelections { get; }

    /// <summary>
    /// Records of bulk-operation executions (R0166 / TOR CF 03.11 / UI 015). One row
    /// per <c>POST /api/bulk-actions/runs</c>; carries the operation code, status,
    /// counters, per-row failure summary, and optional idempotency key. See
    /// <see cref="BulkOperationRun"/> for the lifecycle / idempotency contract.
    /// </summary>
    DbSet<BulkOperationRun> BulkOperationRuns { get; }

    /// <summary>
    /// Admin-configurable per-entity field-change policy registry (R0183 / SEC 043).
    /// Each row binds a CLR entity type to a tracked-field / suppressed-field list +
    /// emission policy consulted by <c>IAuditDiffWriter</c> before writing an audit
    /// row. See <see cref="AuditFieldPolicy"/> for the tracking / suppression
    /// semantics.
    /// </summary>
    DbSet<AuditFieldPolicy> AuditFieldPolicies { get; }

    /// <summary>
    /// Per-workflow notification-strategy registry (R0128 / R0173 / CF 16.14 / CF
    /// 22.04). Each row binds a <see cref="WorkflowDefinition"/> + event code to a
    /// channel list, recipient-role list, template override, and quiet-hours window
    /// consulted by the workflow notification orchestrator at dispatch time. See
    /// <see cref="WorkflowNotificationStrategy"/> for the natural-key uniqueness rule
    /// and the IsEnabled-vs-IsActive semantics.
    /// </summary>
    DbSet<WorkflowNotificationStrategy> WorkflowNotificationStrategies { get; }

    /// <summary>
    /// R0127 / CF 16.11 — operator-declared absence windows for CNAS staff. Each row
    /// nominates a delegate who receives the absent user's open workflow tasks for the
    /// duration; the lifecycle job activates and completes rows on schedule. See
    /// <see cref="UserAbsence"/> for the lifecycle contract.
    /// </summary>
    DbSet<UserAbsence> UserAbsences { get; }

    /// <summary>
    /// R0126 / CF 16.10 — per-workflow per-step ACL refinement table. Each row
    /// narrows access for one step of one workflow ON TOP of the workflow-level
    /// <see cref="WorkflowDefinition.AllowedRoles"/> /
    /// <see cref="WorkflowDefinition.AllowedGroups"/> gate. The ACL resolver
    /// (<c>IWorkflowAclService</c>) intersects the caller's role/group set with both
    /// the workflow-level and step-level requirements at every mutation entry point.
    /// See <see cref="WorkflowStepAcl"/> for the natural-key + conjunctive-composition
    /// contract.
    /// </summary>
    DbSet<WorkflowStepAcl> WorkflowStepAcls { get; }

    /// <summary>
    /// R0125 / CF 16.09 — append-only history projection of every state-transition,
    /// reassignment, SLA breach, completion and cancellation event in a workflow
    /// task's lifecycle. Populated by <c>IWorkflowTaskHistoryService.RecordEventAsync</c>
    /// from every write-site that mutates a <see cref="WorkflowTask"/>; queried by the
    /// admin REST surface to render the per-task timeline.
    /// </summary>
    DbSet<WorkflowTaskStepHistory> WorkflowTaskStepHistories { get; }

    /// <summary>
    /// R0512 / TOR CF 02.01 — CNAS regional branches surfaced by the anonymous
    /// online-appointment booking directory. Each row is a physical CNAS
    /// office that citizens can deep-link to from the public surface. Seeded
    /// by the <c>AddCnasBranchesAndPublicLookups</c> migration via idempotent
    /// <c>ON CONFLICT (Code) DO NOTHING</c>; operators may add, deactivate,
    /// or edit rows through future admin tooling.
    /// </summary>
    DbSet<CnasBranch> CnasBranches { get; }

    /// <summary>
    /// R0516 / TOR CF 02.04 — citizen-facing personal-account aggregates
    /// ("Cont personal"). Each row is owned by one <see cref="Solicitant"/>
    /// (1:1, enforced via the unique index on
    /// <see cref="Cnas.Ps.Core.Domain.PersonalAccount.OwnerSolicitantId"/>) and
    /// carries the stable external
    /// <see cref="Cnas.Ps.Core.Domain.PersonalAccount.AccountCode"/> plus
    /// cached lifetime counters. See the entity remarks for the
    /// projection-cache semantics.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PersonalAccount> PersonalAccounts { get; }

    /// <summary>
    /// R0516 / TOR CF 02.04 — contribution entries attributed to a
    /// <see cref="Cnas.Ps.Core.Domain.PersonalAccount"/>. The natural key
    /// <c>(PersonalAccountId, Year, Month, SourceCode)</c> is enforced via a
    /// composite unique index in
    /// <c>Cnas.Ps.Infrastructure.Persistence.Configurations.PersonalAccountEntryConfiguration</c>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PersonalAccountEntry> PersonalAccountEntries { get; }

    /// <summary>
    /// R0517 / TOR CF 02.05 — citizen benefit-payment ledger rows (pensions +
    /// allowances). Each row is the disbursement record (paid / scheduled /
    /// returned / cancelled) for one (Beneficiary × BenefitType × PaymentMonth)
    /// tuple. The composite unique index on
    /// <c>(BeneficiarySolicitantId, BenefitType, PaymentMonth)</c> enforces the
    /// natural-key rule documented on the entity; the secondary index on
    /// <c>(BeneficiarySolicitantId, PaymentMonth DESC)</c> supports the
    /// authenticated status-lookup query path. See
    /// <see cref="Cnas.Ps.Core.Domain.BenefitPayment"/> for the lifecycle and
    /// sensitivity rules.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.BenefitPayment> BenefitPayments { get; }

    /// <summary>
    /// R0301 / ARH 028 / TOR Annex 1 — change-traceable address child rows for
    /// <see cref="Cnas.Ps.Core.Domain.Contributor"/> (Plătitor). Supersession-only
    /// updates enforce "exactly one current row per Payer" via the filtered unique
    /// index. See <see cref="Cnas.Ps.Core.Domain.PayerAddress"/> for the lifecycle.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PayerAddress> PayerAddresses { get; }

    /// <summary>
    /// R0301 / ARH 028 — change-traceable contact rows for a Payer. See
    /// <see cref="Cnas.Ps.Core.Domain.PayerContact"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PayerContact> PayerContacts { get; }

    /// <summary>
    /// R0301 / ARH 028 — change-traceable CAEM activity rows for a Payer. Multiple
    /// concurrent activity rows are permitted (primary + secondaries). See
    /// <see cref="Cnas.Ps.Core.Domain.PayerActivityCAEM"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PayerActivityCAEM> PayerActivities { get; }

    /// <summary>
    /// R0301 / ARH 028 — append-only audit-style log for parent-level field changes
    /// on a Payer that aren't captured by the dedicated child tables. See
    /// <see cref="Cnas.Ps.Core.Domain.PayerHistory"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PayerHistory> PayerHistory { get; }

    /// <summary>
    /// R0803 / ARH 028 / TOR BP 1.1-D — change-traceable bank-account child rows for
    /// a Payer. Multiple non-primary accounts may coexist; at most one current
    /// primary row is allowed per Payer (filtered unique index). See
    /// <see cref="Cnas.Ps.Core.Domain.PayerBankAccount"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PayerBankAccount> PayerBankAccounts { get; }

    /// <summary>
    /// R0803 / ARH 028 / TOR BP 1.1-D — change-traceable secondary-contact rows for
    /// a Payer (Accountant, Legal, Authorised Representative). Multiple concurrent
    /// rows are permitted. See <see cref="Cnas.Ps.Core.Domain.PayerSecondaryContact"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PayerSecondaryContact> PayerSecondaryContacts { get; }

    /// <summary>
    /// R0311 / ARH 028 / TOR Annex 2.3 — change-traceable address rows for an
    /// <see cref="Cnas.Ps.Core.Domain.InsuredPerson"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ContributorAddress> ContributorAddresses { get; }

    /// <summary>R0311 — change-traceable contact rows for an InsuredPerson.</summary>
    DbSet<Cnas.Ps.Core.Domain.ContributorContact> ContributorContacts { get; }

    /// <summary>R0311 — employment / activity-period rows for an InsuredPerson.</summary>
    DbSet<Cnas.Ps.Core.Domain.ContributorActivityPeriod> ContributorActivityPeriods { get; }

    /// <summary>R0311 — civil-status rows for an InsuredPerson.</summary>
    DbSet<Cnas.Ps.Core.Domain.ContributorCivilStatus> ContributorCivilStatuses { get; }

    /// <summary>R0311 — voluntary social-insurance contracts on file for an InsuredPerson.</summary>
    DbSet<Cnas.Ps.Core.Domain.ContributorSocialInsuranceContract> ContributorSocialInsuranceContracts { get; }

    /// <summary>R0311 — pre-1999 Carnet de muncă historical periods for an InsuredPerson.</summary>
    DbSet<Cnas.Ps.Core.Domain.ContributorPre1999PeriodCarnetMunca> ContributorPre1999PeriodsCarnetMunca { get; }

    /// <summary>
    /// R0920 / TOR BP 2.3-A — labor-booklet master records (Carnete de muncă)
    /// registered against natural-person Solicitants. Each row carries the OCR
    /// metadata + lifecycle status; the scanned binary lives on an attached
    /// <see cref="Cnas.Ps.Core.Domain.AttachmentRecord"/> with
    /// <c>OwnerEntityType="LaborBooklet"</c>. See
    /// <see cref="Cnas.Ps.Core.Domain.LaborBooklet"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.LaborBooklet> LaborBooklets { get; }

    /// <summary>
    /// R0921 / TOR BP 2.3-B — pre-01.01.1999 activity periods for an insured
    /// person (modeled as a natural-person Solicitant). Each row digitises one
    /// employment period from the citizen's paper Carnet de muncă. See
    /// <see cref="Cnas.Ps.Core.Domain.InsuredPersonPre1999Period"/> for the
    /// supersession contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.InsuredPersonPre1999Period> InsuredPersonPre1999Periods { get; }

    /// <summary>
    /// R0922 / TOR Annex 2 §8.2.4 — pre-1999 stagiu (insurance-period) roll-up
    /// attached directly to an <see cref="Cnas.Ps.Core.Domain.InsuredPerson"/>.
    /// Each row carries the post-validation Years/Months/Days tally that feeds
    /// the pension-calculation pipeline. Distinct from the employment-period
    /// timeline stored in <c>InsuredPersonPre1999Periods</c>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.Pre1999StagiuRecord> Pre1999StagiuRecords { get; }

    /// <summary>
    /// R0362 / TOR UC13 — workflow-driven profile-update sub-requests. One row per
    /// <see cref="Cnas.Ps.Core.Domain.ServiceApplication"/> of profile-update kind;
    /// approval applies the requested change to the matching child table. See
    /// <see cref="Cnas.Ps.Core.Domain.ProfileUpdateRequest"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ProfileUpdateRequest> ProfileUpdateRequests { get; }

    /// <summary>
    /// R0363 / TOR UC13 — records of external-data refresh runs (RSP / RSUD / SI SFS).
    /// One row per <c>IProfileRefreshService.RefreshFromSourceAsync</c> call. See
    /// <see cref="Cnas.Ps.Core.Domain.ProfileRefreshRun"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ProfileRefreshRun> ProfileRefreshRuns { get; }

    /// <summary>
    /// R0201 / TOR CF 20.02 — daily pre-aggregated KPI snapshot rows.
    /// One row per (<see cref="Cnas.Ps.Core.Domain.KpiSnapshot.SnapshotDate"/>,
    /// <see cref="Cnas.Ps.Core.Domain.KpiSnapshot.KpiCode"/>, <c>Dimension1</c>,
    /// <c>Dimension2</c>) tuple, upserted by the <c>KpiSnapshotJob</c> and
    /// read by the operator-dashboard endpoint. See
    /// <see cref="Cnas.Ps.Core.Domain.KpiSnapshot"/> for the entity contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.KpiSnapshot> KpiSnapshots { get; }

    /// <summary>
    /// R0153 / TOR CF 19.05 — period-aware projection rows for an
    /// <see cref="Cnas.Ps.Core.Domain.InsuredPerson"/> ("Persoană asigurată" /
    /// Contributor). One row per slice <c>[PeriodStartUtc, PeriodEndUtc)</c>
    /// across which every projected field held a consistent value; rebuilt
    /// daily by <c>ContributorPeriodProjectionJob</c> and queried by
    /// period-aware reports. See
    /// <see cref="Cnas.Ps.Core.Domain.ContributorPeriodProjection"/> for the
    /// entity contract and the unique-index semantics.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ContributorPeriodProjection> ContributorPeriodProjections { get; }

    /// <summary>
    /// R0123 / TOR CF 16.05 — nodes of the persisted execution graph for a
    /// <see cref="WorkflowDefinition"/> version. See
    /// <see cref="Cnas.Ps.Core.Domain.WorkflowGraphNode"/> for the entity contract and
    /// the per-version pinning rule.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.WorkflowGraphNode> WorkflowGraphNodes { get; }

    /// <summary>
    /// R0123 / TOR CF 16.05 — directed edges of the persisted execution graph. See
    /// <see cref="Cnas.Ps.Core.Domain.WorkflowGraphEdge"/> for the entity contract and
    /// the source/target lookup index shape.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.WorkflowGraphEdge> WorkflowGraphEdges { get; }

    /// <summary>
    /// R0210 / TOR UI 007 / CF 17.16 — translation-key registry rows. One row per
    /// stable kebab-case key (e.g. <c>pages.applications.list.title</c>); per-language
    /// text lives in <see cref="TranslationValues"/>. See
    /// <see cref="Cnas.Ps.Core.Domain.TranslationKey"/> for the entity contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.TranslationKey> TranslationKeys { get; }

    /// <summary>
    /// R0210 / TOR UI 007 / CF 17.16 — per-language localised text rows linked to
    /// <see cref="TranslationKeys"/> via
    /// <see cref="Cnas.Ps.Core.Domain.TranslationValue.TranslationKeyId"/>. One row
    /// per (key, language) pair. See
    /// <see cref="Cnas.Ps.Core.Domain.TranslationValue"/> for the entity contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.TranslationValue> TranslationValues { get; }

    /// <summary>
    /// R0225 / TOR UI 015 — contextual-help topic registry rows. One row per stable
    /// kebab-case code; per-language title + body live in <see cref="HelpTopicTranslations"/>.
    /// See <see cref="Cnas.Ps.Core.Domain.HelpTopic"/> for the entity contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.HelpTopic> HelpTopics { get; }

    /// <summary>
    /// R0225 / TOR UI 015 — per-language help-topic translation rows linked to
    /// <see cref="HelpTopics"/> via
    /// <see cref="Cnas.Ps.Core.Domain.HelpTopicTranslation.HelpTopicId"/>. One row per
    /// (topic, language) pair. See
    /// <see cref="Cnas.Ps.Core.Domain.HelpTopicTranslation"/> for the entity contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.HelpTopicTranslation> HelpTopicTranslations { get; }

    /// <summary>
    /// R0156 / TOR CF 09.02 / FLEX 003 — ad-hoc report templates authored by power
    /// users. Each row captures the registry, the JSON-encoded selected-fields list,
    /// the JSON-encoded QBE filter (R0163), the multi-column ordering, an optional
    /// group-by field, the owning user, and a sharing flag. Executed by
    /// <c>IReportEngine.RunAsync</c> / <c>ExportAsync</c>; see
    /// <see cref="Cnas.Ps.Core.Domain.ReportTemplate"/> for the entity contract and
    /// the sharing rules.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ReportTemplate> ReportTemplates { get; }

    /// <summary>
    /// R0156 — append-only execution history for
    /// <see cref="Cnas.Ps.Core.Domain.ReportTemplate"/>. One row per engine
    /// invocation, capturing who ran the template, when, how many rows it produced,
    /// the stable outcome string, the duration, and (on failure) the human-readable
    /// reason. See <see cref="Cnas.Ps.Core.Domain.ReportRun"/> for the entity
    /// contract and the outcome vocabulary.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ReportRun> ReportRuns { get; }

    /// <summary>
    /// R0227 / TOR UI 014 — file-attachment ledger rows. Each row pins one binary
    /// attachment (stored in the configured blob backend, opaque
    /// <see cref="Cnas.Ps.Core.Domain.AttachmentRecord.StorageKey"/>) to a polymorphic
    /// owner identified by
    /// (<see cref="Cnas.Ps.Core.Domain.AttachmentRecord.OwnerEntityType"/>,
    /// <see cref="Cnas.Ps.Core.Domain.AttachmentRecord.OwnerEntityId"/>). The filtered
    /// unique index on (<c>OwnerEntityType</c>, <c>OwnerEntityId</c>, <c>Sha256Hex</c>)
    /// WHERE <c>IsActive=true</c> enforces the per-owner dedup contract. See
    /// <see cref="Cnas.Ps.Core.Domain.AttachmentRecord"/> for the full entity contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.AttachmentRecord> AttachmentRecords { get; }

    /// <summary>
    /// R0583 / TOR CF 09.06 / CF 09.09 — durable background work items describing
    /// one queued/running/completed execution of a
    /// <see cref="Cnas.Ps.Core.Domain.ReportTemplate"/>. The runner picks the oldest
    /// <c>Queued</c> row, flips it to <c>Running</c>, runs the engine, persists the
    /// output via the R0227 attachment subsystem, and notifies the user via the
    /// R0171 / R0128 orchestrator. See <see cref="Cnas.Ps.Core.Domain.ReportJob"/>
    /// for the entity contract and lifecycle transitions.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ReportJob> ReportJobs { get; }

    /// <summary>
    /// R0810 / R0811 / R0812 / TOR BP 1.2 (Annex 8 — Declarații) — declaration
    /// rows registered in the contributions registry. Three registration paths
    /// (SFS feed, CNAS desk, other documents) land here as separate
    /// <see cref="Cnas.Ps.Core.Domain.DeclarationKind"/> values; the monthly
    /// aggregator (R0813) rolls every non-cancelled row up via
    /// <see cref="MonthlyContributionCalculations"/>. See
    /// <see cref="Cnas.Ps.Core.Domain.Declaration"/> for the natural-key and
    /// adjustment-workflow contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.Declaration> Declarations { get; }

    /// <summary>
    /// R0813 / TOR BP 1.2-D — per-payer per-month roll-ups of
    /// <see cref="Declarations"/>. Idempotent on the
    /// <c>(ContributorId, Month)</c> natural key: re-running the calculator
    /// upserts in place. See
    /// <see cref="Cnas.Ps.Core.Domain.MonthlyContributionCalculation"/> for the
    /// aggregation contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.MonthlyContributionCalculation> MonthlyContributionCalculations { get; }

    /// <summary>
    /// R0819 / TOR BP 1.2-J — late-payment-penalty rows produced when a payer's
    /// <see cref="MonthlyContributionCalculations"/> roll-up remains unpaid past
    /// the statutory due date. Natural key is
    /// <c>(ContributorId, Month, UpToDate)</c>; re-running the calculator for
    /// the same triple upserts in place. See
    /// <see cref="Cnas.Ps.Core.Domain.LatePaymentPenalty"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.LatePaymentPenalty> LatePaymentPenalties { get; }

    /// <summary>
    /// R0820 / TOR BP 1.2-K — management-period closure rows. One row per closed
    /// calendar month captures the generalising-report aggregates and acts as
    /// the gate that refuses new declarations for the month unless an admin
    /// re-opens it. See <see cref="Cnas.Ps.Core.Domain.ManagementPeriodClose"/>
    /// for the lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ManagementPeriodClose> ManagementPeriodCloses { get; }

    /// <summary>
    /// R0910 / TOR BP 2.2-A — REV-5 declaration headers (employer-filed per-
    /// employee contribution breakdown). One row per (employer × month ×
    /// reference). Children live in <see cref="Rev5DeclarationRows"/>. See
    /// <see cref="Cnas.Ps.Core.Domain.Rev5Declaration"/> for the natural-key
    /// and lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.Rev5Declaration> Rev5Declarations { get; }

    /// <summary>
    /// R0910 / TOR BP 2.2-A — per-employee child rows attached to a
    /// <see cref="Rev5Declarations"/> header. Natural key is
    /// <c>(Rev5DeclarationId, InsuredPersonNationalIdHash)</c>. See
    /// <see cref="Cnas.Ps.Core.Domain.Rev5DeclarationRow"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.Rev5DeclarationRow> Rev5DeclarationRows { get; }

    /// <summary>
    /// R0913 / TOR BP 2.2-D — per-insured-person contribution adjustments
    /// sourced from non-REV-5 supporting documents (court decisions, audit
    /// reports, individual contracts, "other"). Each row projects a
    /// corresponding <see cref="PersonalAccountEntry"/> with
    /// <c>SourceCode = SourceDocumentCode</c>. See
    /// <see cref="Cnas.Ps.Core.Domain.InsuredPersonContributionAdjustment"/>
    /// for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.InsuredPersonContributionAdjustment> InsuredPersonContributionAdjustments { get; }

    /// <summary>
    /// R0911 / TOR BP 2.2-B — Treasury payment receipts imported from the
    /// daily Treasury feed. The
    /// <c>TreasuryDistributionJob</c> background job reconciles each row
    /// against the matching <see cref="Rev5DeclarationRows"/> and credits the
    /// per-citizen <see cref="PersonalAccountEntries"/>. See
    /// <see cref="Cnas.Ps.Core.Domain.TreasuryPaymentReceipt"/> for the
    /// natural-key uniqueness rule and the lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.TreasuryPaymentReceipt> TreasuryPaymentReceipts { get; }

    /// <summary>
    /// R0831 / TOR BP 1.3-B — claims (creanțe) registry. One row per
    /// outstanding obligation owed by a payer; partial payments accumulate
    /// via <see cref="ClaimPayments"/> children. See
    /// <see cref="Cnas.Ps.Core.Domain.Claim"/> for the lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.Claim> Claims { get; }

    /// <summary>
    /// R0832 / TOR BP 1.3-C — claim payments. One row per payment received
    /// against a parent <see cref="Claims"/>; the service updates the parent
    /// totals atomically on each insert. See
    /// <see cref="Cnas.Ps.Core.Domain.ClaimPayment"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ClaimPayment> ClaimPayments { get; }

    /// <summary>
    /// R0814 / TOR BP 1.2-E — BASS-to-payer refund instructions. One row per
    /// (payer × month) overpayment that crosses the refund-request boundary;
    /// the service drives the row through the
    /// <c>Requested → Approved → IssuedToTreasury → Confirmed</c> chain. See
    /// <see cref="Cnas.Ps.Core.Domain.BassRefund"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.BassRefund> BassRefunds { get; }

    /// <summary>
    /// R0815 / TOR BP 1.2-F — corrections applied to underlying
    /// <see cref="TreasuryPaymentReceipts"/>. One row per corrective action
    /// (reverse, redirect-to-payer, redirect-to-month, adjust-amount);
    /// applied mutation lands on the receipt only when the row transitions
    /// to <c>Applied</c>. See
    /// <see cref="Cnas.Ps.Core.Domain.PaymentCorrection"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PaymentCorrection> PaymentCorrections { get; }

    /// <summary>
    /// R0817 / TOR BP 1.2-H — staggered-repayment plans that split a
    /// <see cref="LatePaymentPenalties"/> row into N installments. Active
    /// uniqueness is enforced per parent penalty via a filtered unique
    /// index. See <see cref="Cnas.Ps.Core.Domain.PenaltyRepaymentPlan"/>
    /// for the lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PenaltyRepaymentPlan> PenaltyRepaymentPlans { get; }

    /// <summary>
    /// R0817 / TOR BP 1.2-H — per-installment child rows attached to a
    /// <see cref="PenaltyRepaymentPlans"/> parent. Natural key is
    /// (<c>PenaltyRepaymentPlanId</c>, <c>InstallmentNumber</c>). See
    /// <see cref="Cnas.Ps.Core.Domain.PenaltyRepaymentInstallment"/> for
    /// the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PenaltyRepaymentInstallment> PenaltyRepaymentInstallments { get; }

    /// <summary>
    /// R1600 / R1406 / TOR Annex 3.8 — registry of executory documents
    /// (documente executorii) that compel withholding from benefit payments
    /// payable to the debtor. See
    /// <see cref="Cnas.Ps.Core.Domain.ExecutoryDocument"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ExecutoryDocument> ExecutoryDocuments { get; }

    /// <summary>
    /// R2270 / TOR SEC 023-024 — first-class user-group aggregates with stable
    /// codes, nested membership, and role-grant aggregation. See
    /// <see cref="Cnas.Ps.Core.Domain.UserGroup"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.UserGroup> UserGroups { get; }

    /// <summary>
    /// R2270 / TOR SEC 023-024 — join rows recording group nesting (DAG).
    /// Each row asserts that <c>ChildGroup</c> is a member of <c>ParentGroup</c>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.UserGroupParent> UserGroupParents { get; }

    /// <summary>
    /// R2270 / TOR SEC 023-024 — join rows recording direct user memberships
    /// in a group. Transitive role resolution walks upward from these rows.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.UserGroupMembership> UserGroupMemberships { get; }

    /// <summary>
    /// R2282 / TOR SEC 036 — one row per execution of the data-integrity
    /// sweep. See <see cref="Cnas.Ps.Core.Domain.IntegrityCheckRun"/> for the
    /// lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.IntegrityCheckRun> IntegrityCheckRuns { get; }

    /// <summary>
    /// R2282 / TOR SEC 036 — one row per detected invariant violation. See
    /// <see cref="Cnas.Ps.Core.Domain.IntegrityCheckFinding"/> for the
    /// acknowledgement contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.IntegrityCheckFinding> IntegrityCheckFindings { get; }

    /// <summary>
    /// R1906 / TOR Annex 6 — per-report distribution rules. Each row binds a
    /// stable report code to a (channel, recipient-kind, recipient-code)
    /// tuple controlling where finalised runs of that report fan out. See
    /// <see cref="Cnas.Ps.Core.Domain.ReportDistributionRule"/> for the
    /// lifecycle and encryption contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ReportDistributionRule> ReportDistributionRules { get; }

    /// <summary>
    /// R1906 / TOR Annex 6 — per-attempt distribution dispatch ledger. One
    /// row per consulted rule per fan-out call. See
    /// <see cref="Cnas.Ps.Core.Domain.ReportDistributionDispatch"/> for the
    /// snapshot-at-dispatch contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ReportDistributionDispatch> ReportDistributionDispatches { get; }

    /// <summary>
    /// R1503 / TOR §3.7-D — legal-change events registered by operators that
    /// trigger mass-recalculation sweeps. See
    /// <see cref="Cnas.Ps.Core.Domain.LegalChangeEvent"/> for the lifecycle
    /// contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.LegalChangeEvent> LegalChangeEvents { get; }

    /// <summary>
    /// R1503 / TOR §3.7-D — one row per execution of the mass-recalculation
    /// engine. See <see cref="Cnas.Ps.Core.Domain.RecalculationRun"/> for the
    /// lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.RecalculationRun> RecalculationRuns { get; }

    /// <summary>
    /// R1503 / TOR §3.7-D — per-decision outcome rows inside a
    /// <see cref="RecalculationRuns"/>. See
    /// <see cref="Cnas.Ps.Core.Domain.RecalculationDecisionResult"/> for the
    /// status contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.RecalculationDecisionResult> RecalculationDecisionResults { get; }

    /// <summary>
    /// R1710 / TOR INT 002 — offline-batch submissions (one row per uploaded
    /// CSV). See <see cref="Cnas.Ps.Core.Domain.OfflineBatchSubmission"/> for
    /// the lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.OfflineBatchSubmission> OfflineBatchSubmissions { get; }

    /// <summary>
    /// R1710 / TOR INT 002 — per-row payloads inside each offline-batch
    /// submission. See <see cref="Cnas.Ps.Core.Domain.OfflineBatchRow"/> for
    /// the per-row lifecycle.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.OfflineBatchRow> OfflineBatchRows { get; }

    /// <summary>
    /// R1810 / TOR BP 1.2-I — daily Treasury feed import registry. One row
    /// per ingestion attempt. See
    /// <see cref="Cnas.Ps.Core.Domain.TreasuryFeedImport"/> for the lifecycle
    /// contract and the filtered unique-index semantics.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.TreasuryFeedImport> TreasuryFeedImports { get; }

    /// <summary>
    /// R1810 / TOR BP 1.2-I — per-row payloads inside each Treasury feed
    /// import. See <see cref="Cnas.Ps.Core.Domain.TreasuryFeedImportRow"/>
    /// for the per-row lifecycle and the documented internal-ops exception
    /// to CLAUDE.md RULE 3 on <c>MappedReceiptId</c>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.TreasuryFeedImportRow> TreasuryFeedImportRows { get; }

    /// <summary>
    /// R0203 / TOR CF 20.06 — per-source external-system ingestion-run registry.
    /// One row per scheduled or manual ingestion attempt. See
    /// <see cref="Cnas.Ps.Core.Domain.ExternalSourceIngestionRun"/> for the
    /// lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ExternalSourceIngestionRun> ExternalSourceIngestionRuns { get; }

    /// <summary>
    /// R2273 / TOR SEC 027 — generic 4-eyes admin requests awaiting a second-operator
    /// approval. The substrate carries the workflow once; per-action behaviour is
    /// plugged in via <c>ISensitiveActionPolicy</c> + <c>ISensitiveActionHandler</c>.
    /// See <see cref="SensitiveAdminAction"/> for the lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.SensitiveAdminAction> SensitiveAdminActions { get; }

    /// <summary>
    /// R1202 / TOR §3.4-C — capitalised-payment requests opened when a Moldovan
    /// company is being liquidated and owes an ongoing periodic indemnization
    /// obligation. See <see cref="Cnas.Ps.Core.Domain.CapitalisedPaymentRequest"/>
    /// for the lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.CapitalisedPaymentRequest> CapitalisedPaymentRequests { get; }

    /// <summary>
    /// R1202 / TOR §3.4-C — finalised computation outcomes attached to a
    /// <see cref="CapitalisedPaymentRequests"/>. See
    /// <see cref="Cnas.Ps.Core.Domain.CapitalisedPaymentDecision"/> for the
    /// contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.CapitalisedPaymentDecision> CapitalisedPaymentDecisions { get; }

    /// <summary>
    /// R2279 / TOR SEC 033 — data-classification catalog snapshot rows. One row
    /// per scanner run (manual or weekly scheduled). See
    /// <see cref="Cnas.Ps.Core.Domain.ClassificationCatalogSnapshot"/> for the
    /// lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ClassificationCatalogSnapshot> ClassificationCatalogSnapshots { get; }

    /// <summary>
    /// R2279 / TOR SEC 033 — one row per (snapshot × classified property)
    /// discovered by the scanner. Natural key
    /// <c>(SnapshotId, TypeFullName, PropertyName)</c>. See
    /// <see cref="Cnas.Ps.Core.Domain.ClassificationCatalogEntry"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ClassificationCatalogEntry> ClassificationCatalogEntries { get; }

    /// <summary>
    /// R2279 / TOR SEC 033 — one row per drift detection between a baseline
    /// and a current snapshot. See
    /// <see cref="Cnas.Ps.Core.Domain.ClassificationDriftFinding"/> for the
    /// acknowledgement contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ClassificationDriftFinding> ClassificationDriftFindings { get; }

    /// <summary>
    /// R1403 / TOR §3.6-D — lifetime athlete-pension awards (indemnizație
    /// viageră sportivi performanță + antrenori). See
    /// <see cref="Cnas.Ps.Core.Domain.AthletePensionAward"/> for the
    /// lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.AthletePensionAward> AthletePensionAwards { get; }

    /// <summary>
    /// R1403 / TOR §3.6-D — career-record child rows attached to an
    /// <see cref="AthletePensionAwards"/>. See
    /// <see cref="Cnas.Ps.Core.Domain.AthleteCareerRecord"/> for the
    /// verification contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.AthleteCareerRecord> AthleteCareerRecords { get; }

    /// <summary>
    /// R1201 / R1402 / TOR §3.4-B / §3.6-C — international-agreements
    /// review cases driven through the reusable 3-level routing chain. See
    /// <see cref="Cnas.Ps.Core.Domain.IntlAgreementReviewCase"/> for the
    /// lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.IntlAgreementReviewCase> IntlAgreementReviewCases { get; }

    /// <summary>
    /// R1201 / R1402 / TOR §3.4-B / §3.6-C — append-only review-level
    /// decisions attached to an <see cref="IntlAgreementReviewCases"/>.
    /// See <see cref="Cnas.Ps.Core.Domain.IntlAgreementReviewStep"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.IntlAgreementReviewStep> IntlAgreementReviewSteps { get; }

    /// <summary>
    /// R2271 / TOR SEC 025 — ABAC rule sets keyed by stable policy name. See
    /// <see cref="Cnas.Ps.Core.Domain.AbacRuleSet"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.AbacRuleSet> AbacRuleSets { get; }

    /// <summary>
    /// R2271 / TOR SEC 025 — ordered ABAC rules belonging to a rule set. See
    /// <see cref="Cnas.Ps.Core.Domain.AbacRule"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.AbacRule> AbacRules { get; }

    /// <summary>
    /// R2430 / TOR M4 — declarative migration-plan registry. See
    /// <see cref="Cnas.Ps.Core.Domain.MigrationPlan"/> for the lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.MigrationPlan> MigrationPlans { get; }

    /// <summary>
    /// R2430 / R2431 / TOR M4 — per-execution migration run records. See
    /// <see cref="Cnas.Ps.Core.Domain.MigrationRun"/> for the lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.MigrationRun> MigrationRuns { get; }

    /// <summary>
    /// R2430 / R2431 / TOR M4 — per-batch counters inside a migration run. See
    /// <see cref="Cnas.Ps.Core.Domain.MigrationBatch"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.MigrationBatch> MigrationBatches { get; }

    /// <summary>
    /// R2430 / R2433 / TOR M4 — per-issue findings raised during a migration. See
    /// <see cref="Cnas.Ps.Core.Domain.MigrationFinding"/> for the acknowledgement contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.MigrationFinding> MigrationFindings { get; }

    /// <summary>
    /// R2433 / TOR M4 — per-run reconciliation reports. See
    /// <see cref="Cnas.Ps.Core.Domain.ReconciliationReport"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ReconciliationReport> ReconciliationReports { get; }

    /// <summary>
    /// R2431 / TOR M4 — generic staging rows produced by the migration importer.
    /// See <see cref="Cnas.Ps.Core.Domain.MigrationStagingRow"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.MigrationStagingRow> MigrationStagingRows { get; }

    /// <summary>
    /// R2307 / TOR SEC 060 — operator-configurable backup-policy registry. Each
    /// row binds a stable PolicyCode to a (scope, strategy, cron, retention,
    /// target) tuple driving the orchestrator. See
    /// <see cref="Cnas.Ps.Core.Domain.BackupPolicy"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.BackupPolicy> BackupPolicies { get; }

    /// <summary>
    /// R2307 / TOR SEC 060 — per-execution backup-run ledger; one row per
    /// scheduled or manual fire of a <see cref="Cnas.Ps.Core.Domain.BackupPolicy"/>.
    /// See <see cref="Cnas.Ps.Core.Domain.BackupRun"/> for the lifecycle contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.BackupRun> BackupRuns { get; }

    /// <summary>
    /// R2307 / TOR SEC 060 — single integrity-verification record attached to a
    /// <see cref="Cnas.Ps.Core.Domain.BackupRun"/>. See
    /// <see cref="Cnas.Ps.Core.Domain.BackupIntegrityCheck"/> for the contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.BackupIntegrityCheck> BackupIntegrityChecks { get; }

    /// <summary>
    /// R2500 / TOR PIR 020-023 — operator-configurable helpdesk category
    /// registry. Each row carries per-category SLA targets + the default
    /// severity for newly-opened tickets. See
    /// <see cref="Cnas.Ps.Core.Domain.SupportTicketCategory"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.SupportTicketCategory> SupportTicketCategories { get; }

    /// <summary>
    /// R2500 / TOR PIR 020-023 — user-submitted helpdesk ticket aggregate. See
    /// <see cref="Cnas.Ps.Core.Domain.SupportTicket"/> for the lifecycle
    /// contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.SupportTicket> SupportTickets { get; }

    /// <summary>
    /// R2500 / TOR PIR 020-023 — append-only comment timeline attached to a
    /// helpdesk ticket. See <see cref="Cnas.Ps.Core.Domain.SupportTicketComment"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.SupportTicketComment> SupportTicketComments { get; }

    /// <summary>
    /// R2500 / TOR PIR 020-023 — SLA event ledger attached to a helpdesk
    /// ticket. See <see cref="Cnas.Ps.Core.Domain.SupportTicketSlaEvent"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.SupportTicketSlaEvent> SupportTicketSlaEvents { get; }

    /// <summary>
    /// R2501 / TOR PIR 024 — operator-configurable business-hours policy
    /// registry consumed by service-management aggregates that need to
    /// compute "business days" notice. See
    /// <see cref="Cnas.Ps.Core.Domain.BusinessHoursPolicy"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.BusinessHoursPolicy> BusinessHoursPolicies { get; }

    /// <summary>
    /// R2502 / TOR PIR 025 — maintenance-window registry with per-kind
    /// duration ceilings and notice-lead-time enforcement. See
    /// <see cref="Cnas.Ps.Core.Domain.MaintenanceWindow"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.MaintenanceWindow> MaintenanceWindows { get; }

    /// <summary>
    /// R2503 / TOR PIR 022-023 — operator-configurable system-update
    /// schedule registry. See
    /// <see cref="Cnas.Ps.Core.Domain.SystemUpdateSchedule"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.SystemUpdateSchedule> SystemUpdateSchedules { get; }

    /// <summary>
    /// R2504 / TOR PIR 024 — concrete system-update event ledger. Each row
    /// references a parent <see cref="Cnas.Ps.Core.Domain.SystemUpdateSchedule"/>
    /// and (optionally) an associated <see cref="Cnas.Ps.Core.Domain.MaintenanceWindow"/>.
    /// See <see cref="Cnas.Ps.Core.Domain.SystemUpdateEvent"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.SystemUpdateEvent> SystemUpdateEvents { get; }

    /// <summary>
    /// R2505 / TOR PIR 030-033 — change-management aggregate. See
    /// <see cref="Cnas.Ps.Core.Domain.ChangeRequest"/> for the lifecycle
    /// contract and four-eyes++ separation rules.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ChangeRequest> ChangeRequests { get; }

    /// <summary>
    /// R2506 / TOR PIR 037-040 — quality-assurance risk registry. See
    /// <see cref="Cnas.Ps.Core.Domain.QualityRisk"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.QualityRisk> QualityRisks { get; }

    /// <summary>
    /// R2506 / TOR PIR 037-040 — preventive actions linked to a quality risk.
    /// See <see cref="Cnas.Ps.Core.Domain.QualityRiskPreventiveAction"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.QualityRiskPreventiveAction> QualityRiskPreventiveActions { get; }

    /// <summary>
    /// R0103 / TOR CF 14.02 — inbound integration-event dedup ledger. One row per
    /// successfully claimed CloudEvents <c>MessageId</c>; the UNIQUE constraint on
    /// <see cref="Cnas.Ps.Core.Domain.ProcessedIntegrationEvent.MessageId"/> enforces
    /// the "exactly-once at the boundary" guarantee. Written by
    /// <c>IIntegrationEventDeduper</c>; read by ops dashboards and integration tests.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ProcessedIntegrationEvent> ProcessedIntegrationEvents { get; }

    /// <summary>
    /// R0196 / TOR CF 23.02 — operator-configurable audit-category registry.
    /// Each row binds a stable SCREAMING_SNAKE_CASE code to a display name and
    /// default severity. See <see cref="Cnas.Ps.Core.Domain.AuditCategory"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.AuditCategory> AuditCategories { get; }

    /// <summary>
    /// R0302 / TOR §2.1 — append-only history rows capturing every mutation of a
    /// contributor's <c>SourceSystem</c> attribution. Writers (manual update,
    /// MConnect RSUD sync) push one row per change; readers render the
    /// per-contributor timeline ordered by <c>ChangedAtUtc DESC</c>. See
    /// <see cref="Cnas.Ps.Core.Domain.ContributorSourceChangeHistory"/> for the
    /// append-only contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ContributorSourceChangeHistory> ContributorSourceChangeHistory { get; }

    /// <summary>
    /// R0322 / TOR UI 014 — first-class application-attachment metadata rows.
    /// One row per active (<c>ApplicationId</c>, <c>DocumentId</c>) link with
    /// rich metadata (category, mandatory snapshot, virus-scan lifecycle,
    /// optional removal record). Source of truth going forward; the legacy
    /// <c>ServiceApplication.AttachmentDocumentIds</c> list remains as a
    /// denormalised cache. See <see cref="Cnas.Ps.Core.Domain.ApplicationAttachment"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ApplicationAttachment> ApplicationAttachments { get; }

    /// <summary>
    /// R0191 / TOR SEC 050 / TOR ARH 028 — application-level history snapshot
    /// rows. One row per mutation of any entity that implements
    /// <c>Cnas.Ps.Core.Audit.IHistoryTracked</c>; written by the universal
    /// <c>HistoryTrackingInterceptor</c>. See
    /// <see cref="Cnas.Ps.Core.Domain.EntityHistoryRow"/> for the natural-key
    /// + payload contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.EntityHistoryRow> EntityHistoryRows { get; }

    /// <summary>
    /// R0570 / TOR CF 08.02 — singleton-row checkpoint for the round-robin
    /// examiner assignment service. One row per environment, keyed by the
    /// literal <c>"default"</c>; the assignment service increments
    /// <see cref="Cnas.Ps.Core.Domain.ExaminerAssignmentCursor.NextIndex"/>
    /// after every successful pick so consecutive submissions fan out
    /// uniformly across the eligible examiner pool. See
    /// <see cref="Cnas.Ps.Core.Domain.ExaminerAssignmentCursor"/> for the
    /// singleton-via-known-key pattern and the failure-handling contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.ExaminerAssignmentCursor> ExaminerAssignmentCursors { get; }

    /// <summary>
    /// R0200 / TOR CF 20.01-03, MR 012 — operator-configurable cron overrides for the
    /// embedded Quartz job set. One row per job code; absence means "use the baked-in
    /// default cron". See <see cref="Cnas.Ps.Core.Domain.JobScheduleOverride"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.JobScheduleOverride> JobScheduleOverrides { get; }

    /// <summary>
    /// R0540 / TOR CF 05.01 (iter 134) — admin-configurable rules that drive
    /// rule-based auto-creation of <see cref="WorkflowTask"/> rows on
    /// application status transitions. Consulted by
    /// <c>RuleDrivenWorkflowTaskAutoCreator</c> on every state-machine transition
    /// site. The table is intentionally small and decoupled from any external BPM
    /// engine; once R0120 (Operaton) lands the rules can be soft-disabled.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.WorkflowAutoCreationRule> WorkflowAutoCreationRules { get; }

    /// <summary>
    /// R0673 / TOR CF 18.12 — granular permission matrix rows. Each row maps a
    /// (RoleCode, ResourceType, PermissionVerb) triple to a grant. Absence of a
    /// row means "denied". See <see cref="Cnas.Ps.Core.Domain.GranularPermissionAssignment"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.GranularPermissionAssignment> GranularPermissionAssignments { get; }

    /// <summary>
    /// R0602 / TOR CF 11.03 — territorial paper-channel fulfilment workflow
    /// rows. One row per Document whose channel is <c>Paper</c>; tracks the
    /// state machine Pending → Printed → Dispatched → Delivered. See
    /// <see cref="Cnas.Ps.Core.Domain.PaperFulfilmentRecord"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PaperFulfilmentRecord> PaperFulfilmentRecords { get; }

    /// <summary>
    /// R0933 / TOR §10.1 — append-only audit rows linking a newly accepted
    /// decision to the prior active decision it superseded for the same
    /// (Solicitant, ServiceCode) pair. See
    /// <see cref="Cnas.Ps.Core.Domain.DecisionSupersession"/> for the
    /// natural-key + idempotency contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.DecisionSupersession> DecisionSupersessions { get; }

    /// <summary>
    /// R1504 / TOR §3.7-E — CNAS-initiated payment-suspension lifecycle records.
    /// See <see cref="Cnas.Ps.Core.Domain.PaymentSuspensionRecord"/> for the
    /// double-suspend / double-resume invariants.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.PaymentSuspensionRecord> PaymentSuspensionRecords { get; }

    /// <summary>
    /// R0115 / TOR CF 14.07 — admin-managed MNotify template registry.
    /// See <see cref="Cnas.Ps.Core.Domain.MNotifyTemplate"/> for the natural-key
    /// (<c>Code</c>) and channel-kind invariants.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.MNotifyTemplate> MNotifyTemplates { get; }

    /// <summary>
    /// R0116 + R0195 / TOR SEC 054-055 — admin-configurable MLog dual-write
    /// category filter. See <see cref="Cnas.Ps.Core.Domain.MLogCategoryConfig"/>
    /// for the dual-write decision rule.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.MLogCategoryConfig> MLogCategoryConfigs { get; }

    /// <summary>
    /// R0057 / TOR SEC 026 + CF 16.11 — time-bounded permission grants under which
    /// one user authorises another to act on their behalf for the window. See
    /// <see cref="Cnas.Ps.Core.Domain.DelegationGrant"/> for the lifecycle contract
    /// (grant / revoke / list-active) and the active-grant predicate.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.DelegationGrant> DelegationGrants { get; }

    /// <summary>
    /// R2161 / TOR INT 002 — generic CnasUser-facing offline-batch job registry
    /// (ingest + export). See <see cref="Cnas.Ps.Core.Domain.OfflineBatchJob"/>
    /// for the lifecycle contract. Separate from <see cref="OfflineBatchSubmissions"/>
    /// which is the Annex-4 / B2B file-based surface.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.OfflineBatchJob> OfflineBatchJobs { get; }

    /// <summary>
    /// R2190-R2200 / TOR §15.6 FLEX 006 — dynamic-entity-attribute (EAV)
    /// sidecar. One row per (entityType, entityId, attributeCode) tuple.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.EntityAttributeValue> EntityAttributeValues { get; }

    /// <summary>
    /// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — insolvency lifecycle registry.
    /// One row per insolvency event against a <see cref="Contributors"/>;
    /// children live in <see cref="InsolvencyClaims"/> + <see cref="InsolvencyPayments"/>.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.InsolvencyCase> InsolvencyCases { get; }

    /// <summary>
    /// R0834 / TOR Annex 1 §8.1.4.5 — claims lodged against a parent
    /// <see cref="InsolvencyCases"/> row by third-party creditors.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.InsolvencyClaim> InsolvencyClaims { get; }

    /// <summary>
    /// R0834 / TOR Annex 1 §8.1.4.5 — payments received against a parent
    /// <see cref="InsolvencyCases"/> row from the insolvent estate.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.InsolvencyPayment> InsolvencyPayments { get; }

    /// <summary>
    /// R1000..R1034 / TOR §3.2-AB..AD — operator-configured voucher quotas
    /// backing the spa / rehabilitation / sanatorium passports. One row per
    /// (PassportCode, Year). See <see cref="Cnas.Ps.Core.Domain.VoucherQuota"/>
    /// for the atomicity + uniqueness contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.VoucherQuota> VoucherQuotas { get; }

    /// <summary>
    /// R1000..R1034 / TOR §3.2-Z — operator-registered recurrent-payment
    /// schedules driving the monthly state-support and similar allowances.
    /// See <see cref="Cnas.Ps.Core.Domain.RecurrentPaymentSchedule"/> for the
    /// lifecycle + dispatch contract.
    /// </summary>
    DbSet<Cnas.Ps.Core.Domain.RecurrentPaymentSchedule> RecurrentPaymentSchedules { get; }

    /// <summary>Persists pending changes to the database.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
