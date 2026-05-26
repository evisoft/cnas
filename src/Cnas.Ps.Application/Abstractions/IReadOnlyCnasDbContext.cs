using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Read-only projection of <see cref="ICnasDbContext"/> routed to the Postgres
/// streaming-replication replica per TOR PSR 006 / ARH 025. Consumers are pure
/// readers (reporting aggregations, registry listings, audit queries) and have
/// no write capability — there is no <c>SaveChangesAsync</c>, <c>Add</c>,
/// <c>Update</c>, or <c>Remove</c> exposed here. Every <see cref="DbSet{T}"/> on
/// <see cref="ICnasDbContext"/> appears below as an <see cref="IQueryable{T}"/>
/// so the read-only surface cannot be accidentally widened into a write path at
/// the call site.
/// </summary>
/// <remarks>
/// <para>
/// Failover semantics: when <c>ConnectionStrings:PostgresReadReplica</c> is
/// unset (dev, single-Postgres staging) the implementation transparently routes
/// to the primary. A WARN-level log line on application startup announces the
/// fallback so operators don't silently lose replica isolation. See
/// <c>docs/operations.md</c> §"Read-replica routing (R0026)" for the topology.
/// </para>
/// <para>
/// Replica lag is BEST-EFFORT eventual consistency — a row just inserted via
/// <see cref="ICnasDbContext"/> may not yet be visible here. Callers that need
/// read-your-own-writes guarantees MUST stay on <see cref="ICnasDbContext"/>.
/// In integration tests both contexts share the same EF Core InMemory store so
/// the round-trip is deterministic; in production the streaming replica may lag
/// the primary by tens to hundreds of milliseconds.
/// </para>
/// <para>
/// Drift protection: when a new entity is added to <see cref="ICnasDbContext"/>
/// it MUST also be added to this interface in the same commit. The reflection-
/// based test <c>CnasReadOnlyDbContextTests.EveryDbSet_IsMirroredAsIQueryable</c>
/// will fail loudly when one interface grows without the other.
/// </para>
/// </remarks>
public interface IReadOnlyCnasDbContext
{
    /// <summary>Solicitants — applicants for CNAS services (read-only).</summary>
    IQueryable<Solicitant> Solicitants { get; }

    /// <summary>Contributors (Plătitori) registered with CNAS (read-only).</summary>
    IQueryable<Contributor> Contributors { get; }

    /// <summary>Insured persons (Persoane asigurate) (read-only).</summary>
    IQueryable<InsuredPerson> InsuredPersons { get; }

    /// <summary>Applications (Cereri) submitted by Solicitants (read-only).</summary>
    IQueryable<ServiceApplication> Applications { get; }

    /// <summary>
    /// R0321 / R0224 / UI 008 — autosave snapshots of in-flight applications (read-only).
    /// See <see cref="ICnasDbContext.ApplicationVersions"/> for the write-side contract.
    /// </summary>
    IQueryable<ApplicationVersion> ApplicationVersions { get; }

    /// <summary>Dossiers (Dosare) opened in response to Applications (read-only).</summary>
    IQueryable<Dossier> Dossiers { get; }

    /// <summary>Documents stored inside a Dossier (read-only).</summary>
    IQueryable<Document> Documents { get; }

    /// <summary>Report definitions (read-only).</summary>
    IQueryable<Report> Reports { get; }

    /// <summary>User profiles with role + group assignments (read-only).</summary>
    IQueryable<UserProfile> UserProfiles { get; }

    /// <summary>Notifications dispatched to users (read-only).</summary>
    IQueryable<Notification> Notifications { get; }

    /// <summary>Workflow tasks (Sarcini) (read-only).</summary>
    IQueryable<WorkflowTask> WorkflowTasks { get; }

    /// <summary>Service passport definitions (UC15) (read-only).</summary>
    IQueryable<ServicePassport> ServicePassports { get; }

    /// <summary>Reference data (clasificatoare, nomenclatoare) (read-only).</summary>
    IQueryable<Classifier> Classifiers { get; }

    /// <summary>Audit log records (UC23) (read-only).</summary>
    IQueryable<AuditLog> AuditLogs { get; }

    /// <summary>
    /// Dead-letter-queue entries (CLAUDE.md §6.2) (read-only). See
    /// <see cref="ICnasDbContext.FailedJobs"/> for the write-side contract.
    /// </summary>
    IQueryable<FailedJob> FailedJobs { get; }

    /// <summary>
    /// R0137 — application-level immutability ledger (read-only). See
    /// <see cref="ICnasDbContext.FileImmutabilityRecords"/> for the write-side contract.
    /// </summary>
    IQueryable<FileImmutabilityRecord> FileImmutabilityRecords { get; }

    /// <summary>
    /// Versioned BPMN / workflow-graph JSON revisions (UC16) (read-only). See
    /// <see cref="ICnasDbContext.WorkflowDefinitions"/> for the write-side contract.
    /// </summary>
    IQueryable<WorkflowDefinition> WorkflowDefinitions { get; }

    /// <summary>
    /// Versioned operator-uploaded DOCX templates (UC17 phase 2A) (read-only).
    /// See <see cref="ICnasDbContext.DocumentTemplates"/> for the write-side contract.
    /// </summary>
    IQueryable<DocumentTemplate> DocumentTemplates { get; }

    /// <summary>
    /// R0133 / CF 17.16 — per-language variants of a <see cref="DocumentTemplate"/>
    /// (read-only). See <see cref="ICnasDbContext.TemplateVariants"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<TemplateVariant> TemplateVariants { get; }

    /// <summary>
    /// R2003 / R0133 — persisted template-language coverage findings
    /// (read-only). See <see cref="ICnasDbContext.TemplateLanguageCoverageFindings"/>
    /// for the write-side contract and the open-finding dedup rule.
    /// </summary>
    IQueryable<TemplateLanguageCoverageFinding> TemplateLanguageCoverageFindings { get; }

    /// <summary>
    /// MPay payment orders originated by CNAS (read-only). See
    /// <see cref="ICnasDbContext.MPayOrders"/> for the write-side contract.
    /// </summary>
    IQueryable<MPayOrder> MPayOrders { get; }

    /// <summary>
    /// Pending sensitive admin actions awaiting a second-administrator approval
    /// (R0058 / SEC 027) (read-only). See <see cref="ICnasDbContext.PendingAdminActions"/>
    /// for the write-side contract.
    /// </summary>
    IQueryable<PendingAdminAction> PendingAdminActions { get; }

    /// <summary>
    /// Opaque refresh tokens issued by the R0053 token pipeline (read-only). See
    /// <see cref="ICnasDbContext.RefreshTokens"/> for the write-side contract.
    /// </summary>
    IQueryable<RefreshToken> RefreshTokens { get; }

    /// <summary>
    /// R2264 / R2267 — per-session rows backing concurrent-session limit + manual /
    /// auto session lock (read-only). See <see cref="ICnasDbContext.UserSessions"/>
    /// for the write-side contract and lifecycle.
    /// </summary>
    IQueryable<UserSession> UserSessions { get; }

    /// <summary>
    /// User-saved registry searches (R0165 / CF 03.06) (read-only). See
    /// <see cref="ICnasDbContext.SavedSearches"/> for the write-side contract and the
    /// owner/share access rules.
    /// </summary>
    IQueryable<SavedSearch> SavedSearches { get; }

    /// <summary>
    /// SIEM CEF / syslog forwarder checkpoint state (R0190 / SEC 049) (read-only). See
    /// <see cref="ICnasDbContext.SiemForwarderStates"/> for the write-side contract and
    /// the singleton-row pattern.
    /// </summary>
    IQueryable<SiemForwarderState> SiemForwarderStates { get; }

    /// <summary>
    /// Security-alert notification rules (R0189 / SEC 048) (read-only). See
    /// <see cref="ICnasDbContext.SecurityAlertRules"/> for the write-side contract.
    /// </summary>
    IQueryable<SecurityAlertRule> SecurityAlertRules { get; }

    /// <summary>
    /// Security-alert evaluator checkpoint state (R0189 / SEC 048) (read-only). See
    /// <see cref="ICnasDbContext.SecurityAlertEvaluatorStates"/> for the write-side
    /// contract and the singleton-row pattern.
    /// </summary>
    IQueryable<SecurityAlertEvaluatorState> SecurityAlertEvaluatorStates { get; }

    /// <summary>
    /// Admin-configurable audit policies (R0182 / SEC 042) (read-only). See
    /// <see cref="ICnasDbContext.AuditPolicies"/> for the write-side contract and the
    /// resolution algorithm.
    /// </summary>
    IQueryable<AuditPolicy> AuditPolicies { get; }

    /// <summary>
    /// Admin-configurable per-entity field-change policies (R0183 / SEC 043)
    /// (read-only). See <see cref="ICnasDbContext.AuditFieldPolicies"/> for the
    /// write-side contract and the tracked/suppressed-field semantics.
    /// </summary>
    IQueryable<AuditFieldPolicy> AuditFieldPolicies { get; }

    /// <summary>
    /// Per-workflow notification strategies (R0128 / R0173 / CF 16.14 / CF 22.04)
    /// (read-only). See <see cref="ICnasDbContext.WorkflowNotificationStrategies"/>
    /// for the write-side contract and the natural-key uniqueness rule.
    /// </summary>
    IQueryable<WorkflowNotificationStrategy> WorkflowNotificationStrategies { get; }

    /// <summary>
    /// Cross-page bulk-selection handles (R0166 / TOR CF 03.11 / UI 015) (read-only).
    /// See <see cref="ICnasDbContext.BulkSelections"/> for the write-side contract
    /// and the single-use / expiry semantics.
    /// </summary>
    IQueryable<BulkSelection> BulkSelections { get; }

    /// <summary>
    /// Records of bulk-operation executions (R0166 / TOR CF 03.11 / UI 015)
    /// (read-only). See <see cref="ICnasDbContext.BulkOperationRuns"/> for the
    /// write-side contract and the idempotency rules.
    /// </summary>
    IQueryable<BulkOperationRun> BulkOperationRuns { get; }

    /// <summary>
    /// Operator-declared absence windows (R0127 / CF 16.11) (read-only). See
    /// <see cref="ICnasDbContext.UserAbsences"/> for the write-side contract and the
    /// lifecycle transitions.
    /// </summary>
    IQueryable<UserAbsence> UserAbsences { get; }

    /// <summary>
    /// Per-workflow per-step ACL refinement rows (R0126 / CF 16.10) (read-only). See
    /// <see cref="ICnasDbContext.WorkflowStepAcls"/> for the write-side contract and
    /// the conjunctive composition with the workflow-level ACL.
    /// </summary>
    IQueryable<WorkflowStepAcl> WorkflowStepAcls { get; }

    /// <summary>
    /// R0125 / CF 16.09 — workflow-task step-history projection (read-only). See
    /// <see cref="ICnasDbContext.WorkflowTaskStepHistories"/> for the write-side contract
    /// and the population invariants.
    /// </summary>
    IQueryable<WorkflowTaskStepHistory> WorkflowTaskStepHistories { get; }

    /// <summary>
    /// R0512 / TOR CF 02.01 — CNAS regional branches (read-only). See
    /// <see cref="ICnasDbContext.CnasBranches"/> for the write-side contract and the
    /// public deep-link-template contract.
    /// </summary>
    IQueryable<CnasBranch> CnasBranches { get; }

    /// <summary>
    /// R0516 / TOR CF 02.04 — citizen personal-account aggregates (read-only).
    /// See <see cref="ICnasDbContext.PersonalAccounts"/> for the write-side
    /// contract and the per-Solicitant uniqueness rule.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.PersonalAccount> PersonalAccounts { get; }

    /// <summary>
    /// R0516 / TOR CF 02.04 — contribution entries attributed to a personal
    /// account (read-only). See <see cref="ICnasDbContext.PersonalAccountEntries"/>
    /// for the write-side contract and the natural-key composite unique index.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.PersonalAccountEntry> PersonalAccountEntries { get; }

    /// <summary>
    /// R0517 / TOR CF 02.05 — citizen benefit-payment ledger rows (read-only).
    /// See <see cref="ICnasDbContext.BenefitPayments"/> for the write-side
    /// contract and the natural-key composite unique index.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.BenefitPayment> BenefitPayments { get; }

    /// <summary>R0301 / ARH 028 — Payer address child rows (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.PayerAddress> PayerAddresses { get; }

    /// <summary>R0301 / ARH 028 — Payer contact child rows (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.PayerContact> PayerContacts { get; }

    /// <summary>R0301 / ARH 028 — Payer CAEM activity child rows (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.PayerActivityCAEM> PayerActivities { get; }

    /// <summary>R0301 / ARH 028 — Payer parent-level audit-style history log (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.PayerHistory> PayerHistory { get; }

    /// <summary>R0803 / ARH 028 — Payer bank-account child rows (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.PayerBankAccount> PayerBankAccounts { get; }

    /// <summary>R0803 / ARH 028 — Payer secondary-contact child rows (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.PayerSecondaryContact> PayerSecondaryContacts { get; }

    /// <summary>R0311 / ARH 028 — InsuredPerson address child rows (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.ContributorAddress> ContributorAddresses { get; }

    /// <summary>R0311 / ARH 028 — InsuredPerson contact child rows (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.ContributorContact> ContributorContacts { get; }

    /// <summary>R0311 / ARH 028 — InsuredPerson activity-period child rows (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.ContributorActivityPeriod> ContributorActivityPeriods { get; }

    /// <summary>R0311 / ARH 028 — InsuredPerson civil-status child rows (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.ContributorCivilStatus> ContributorCivilStatuses { get; }

    /// <summary>R0311 / ARH 028 — InsuredPerson social-insurance contract child rows (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.ContributorSocialInsuranceContract> ContributorSocialInsuranceContracts { get; }

    /// <summary>R0311 / ARH 028 — InsuredPerson pre-1999 Carnet de muncă child rows (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.ContributorPre1999PeriodCarnetMunca> ContributorPre1999PeriodsCarnetMunca { get; }

    /// <summary>R0920 — labor-booklet master records (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.LaborBooklet> LaborBooklets { get; }

    /// <summary>R0921 — InsuredPerson pre-01.01.1999 activity periods (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.InsuredPersonPre1999Period> InsuredPersonPre1999Periods { get; }

    /// <summary>R0922 — InsuredPerson pre-1999 stagiu Years/Months/Days roll-up (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.Pre1999StagiuRecord> Pre1999StagiuRecords { get; }

    /// <summary>R0362 — workflow-driven profile-update requests (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.ProfileUpdateRequest> ProfileUpdateRequests { get; }

    /// <summary>R0363 — records of external-data profile-refresh runs (read-only).</summary>
    IQueryable<Cnas.Ps.Core.Domain.ProfileRefreshRun> ProfileRefreshRuns { get; }

    /// <summary>
    /// R0201 / TOR CF 20.02 — daily pre-aggregated KPI snapshot rows
    /// (read-only). See <see cref="ICnasDbContext.KpiSnapshots"/> for the
    /// write-side contract and the natural-key uniqueness rule.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.KpiSnapshot> KpiSnapshots { get; }

    /// <summary>
    /// R0153 / TOR CF 19.05 — period-aware projection rows for an
    /// <see cref="Cnas.Ps.Core.Domain.InsuredPerson"/> ("Persoană asigurată" /
    /// Contributor) (read-only). See
    /// <see cref="ICnasDbContext.ContributorPeriodProjections"/> for the
    /// write-side contract and the unique-index semantics.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ContributorPeriodProjection> ContributorPeriodProjections { get; }

    /// <summary>
    /// R0123 / TOR CF 16.05 — nodes of the persisted workflow execution graph
    /// (read-only). See <see cref="ICnasDbContext.WorkflowGraphNodes"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.WorkflowGraphNode> WorkflowGraphNodes { get; }

    /// <summary>
    /// R0123 / TOR CF 16.05 — directed edges of the persisted workflow execution
    /// graph (read-only). See <see cref="ICnasDbContext.WorkflowGraphEdges"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.WorkflowGraphEdge> WorkflowGraphEdges { get; }

    /// <summary>
    /// R0210 / TOR UI 007 / CF 17.16 — translation-key registry rows (read-only).
    /// See <see cref="ICnasDbContext.TranslationKeys"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.TranslationKey> TranslationKeys { get; }

    /// <summary>
    /// R0210 / TOR UI 007 / CF 17.16 — per-language translation values (read-only).
    /// See <see cref="ICnasDbContext.TranslationValues"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.TranslationValue> TranslationValues { get; }

    /// <summary>
    /// R0225 / TOR UI 015 — contextual-help topic registry rows (read-only).
    /// See <see cref="ICnasDbContext.HelpTopics"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.HelpTopic> HelpTopics { get; }

    /// <summary>
    /// R0225 / TOR UI 015 — per-language help-topic translations (read-only).
    /// See <see cref="ICnasDbContext.HelpTopicTranslations"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.HelpTopicTranslation> HelpTopicTranslations { get; }

    /// <summary>
    /// R0156 / TOR CF 09.02 / FLEX 003 — ad-hoc report templates (read-only).
    /// See <see cref="ICnasDbContext.ReportTemplates"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ReportTemplate> ReportTemplates { get; }

    /// <summary>
    /// R0156 — append-only report-run history (read-only).
    /// See <see cref="ICnasDbContext.ReportRuns"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ReportRun> ReportRuns { get; }

    /// <summary>
    /// R0227 / TOR UI 014 — file-attachment ledger rows (read-only). See
    /// <see cref="ICnasDbContext.AttachmentRecords"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.AttachmentRecord> AttachmentRecords { get; }

    /// <summary>
    /// R0583 / TOR CF 09.06 / CF 09.09 — background report-job rows (read-only). See
    /// <see cref="ICnasDbContext.ReportJobs"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ReportJob> ReportJobs { get; }

    /// <summary>
    /// R0810 / R0811 / R0812 — declaration rows registered in the contributions
    /// registry (read-only). See <see cref="ICnasDbContext.Declarations"/> for
    /// the write-side contract and the three registration paths.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.Declaration> Declarations { get; }

    /// <summary>
    /// R0813 — per-payer per-month roll-ups of declarations (read-only). See
    /// <see cref="ICnasDbContext.MonthlyContributionCalculations"/> for the
    /// write-side contract and the idempotent upsert semantics.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.MonthlyContributionCalculation> MonthlyContributionCalculations { get; }

    /// <summary>
    /// R0819 / TOR BP 1.2-J — late-payment-penalty rows (read-only). See
    /// <see cref="ICnasDbContext.LatePaymentPenalties"/> for the write-side
    /// contract and the natural-key idempotency rule.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.LatePaymentPenalty> LatePaymentPenalties { get; }

    /// <summary>
    /// R0820 / TOR BP 1.2-K — management-period closure rows (read-only). See
    /// <see cref="ICnasDbContext.ManagementPeriodCloses"/> for the write-side
    /// contract and the close / re-open lifecycle.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ManagementPeriodClose> ManagementPeriodCloses { get; }

    /// <summary>
    /// R0910 / TOR BP 2.2-A — REV-5 declaration headers (read-only). See
    /// <see cref="ICnasDbContext.Rev5Declarations"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.Rev5Declaration> Rev5Declarations { get; }

    /// <summary>
    /// R0910 / TOR BP 2.2-A — REV-5 per-employee child rows (read-only). See
    /// <see cref="ICnasDbContext.Rev5DeclarationRows"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.Rev5DeclarationRow> Rev5DeclarationRows { get; }

    /// <summary>
    /// R0913 / TOR BP 2.2-D — per-insured-person contribution adjustments
    /// (read-only). See
    /// <see cref="ICnasDbContext.InsuredPersonContributionAdjustments"/> for
    /// the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.InsuredPersonContributionAdjustment> InsuredPersonContributionAdjustments { get; }

    /// <summary>
    /// R0911 / TOR BP 2.2-B — Treasury payment receipts (read-only). See
    /// <see cref="ICnasDbContext.TreasuryPaymentReceipts"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.TreasuryPaymentReceipt> TreasuryPaymentReceipts { get; }

    /// <summary>
    /// R0831 / TOR BP 1.3-B — claims (creanțe) registry (read-only). See
    /// <see cref="ICnasDbContext.Claims"/> for the write-side contract and the
    /// lifecycle rules.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.Claim> Claims { get; }

    /// <summary>
    /// R0832 / TOR BP 1.3-C — claim payments (read-only). See
    /// <see cref="ICnasDbContext.ClaimPayments"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ClaimPayment> ClaimPayments { get; }

    /// <summary>
    /// R0814 / TOR BP 1.2-E — BASS-to-payer refund instructions (read-only).
    /// See <see cref="ICnasDbContext.BassRefunds"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.BassRefund> BassRefunds { get; }

    /// <summary>
    /// R0815 / TOR BP 1.2-F — Treasury-payment corrections (read-only). See
    /// <see cref="ICnasDbContext.PaymentCorrections"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.PaymentCorrection> PaymentCorrections { get; }

    /// <summary>
    /// R0817 / TOR BP 1.2-H — staggered-repayment plans for late-payment
    /// penalties (read-only). See
    /// <see cref="ICnasDbContext.PenaltyRepaymentPlans"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.PenaltyRepaymentPlan> PenaltyRepaymentPlans { get; }

    /// <summary>
    /// R0817 / TOR BP 1.2-H — installment rows belonging to a parent
    /// <see cref="PenaltyRepaymentPlans"/> row (read-only). See
    /// <see cref="ICnasDbContext.PenaltyRepaymentInstallments"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.PenaltyRepaymentInstallment> PenaltyRepaymentInstallments { get; }

    /// <summary>
    /// R1600 / R1406 / TOR Annex 3.8 — executory documents registry (read-only).
    /// See <see cref="ICnasDbContext.ExecutoryDocuments"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ExecutoryDocument> ExecutoryDocuments { get; }

    /// <summary>
    /// R2270 / TOR SEC 023-024 — first-class user-group registry (read-only).
    /// See <see cref="ICnasDbContext.UserGroups"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.UserGroup> UserGroups { get; }

    /// <summary>
    /// R2270 / TOR SEC 023-024 — group-nesting DAG rows (read-only). See
    /// <see cref="ICnasDbContext.UserGroupParents"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.UserGroupParent> UserGroupParents { get; }

    /// <summary>
    /// R2270 / TOR SEC 023-024 — direct user-membership rows (read-only). See
    /// <see cref="ICnasDbContext.UserGroupMemberships"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.UserGroupMembership> UserGroupMemberships { get; }

    /// <summary>
    /// R2282 / TOR SEC 036 — integrity-check runs (read-only). See
    /// <see cref="ICnasDbContext.IntegrityCheckRuns"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.IntegrityCheckRun> IntegrityCheckRuns { get; }

    /// <summary>
    /// R2282 / TOR SEC 036 — integrity-check findings (read-only). See
    /// <see cref="ICnasDbContext.IntegrityCheckFindings"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.IntegrityCheckFinding> IntegrityCheckFindings { get; }

    /// <summary>
    /// R1906 / TOR Annex 6 — per-report distribution rules (read-only). See
    /// <see cref="ICnasDbContext.ReportDistributionRules"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ReportDistributionRule> ReportDistributionRules { get; }

    /// <summary>
    /// R1906 / TOR Annex 6 — per-attempt distribution dispatch ledger (read-only). See
    /// <see cref="ICnasDbContext.ReportDistributionDispatches"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ReportDistributionDispatch> ReportDistributionDispatches { get; }

    /// <summary>
    /// R1503 / TOR §3.7-D — legal-change events registry (read-only). See
    /// <see cref="ICnasDbContext.LegalChangeEvents"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.LegalChangeEvent> LegalChangeEvents { get; }

    /// <summary>
    /// R1503 / TOR §3.7-D — mass-recalculation run rows (read-only). See
    /// <see cref="ICnasDbContext.RecalculationRuns"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.RecalculationRun> RecalculationRuns { get; }

    /// <summary>
    /// R1503 / TOR §3.7-D — per-decision result rows (read-only). See
    /// <see cref="ICnasDbContext.RecalculationDecisionResults"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.RecalculationDecisionResult> RecalculationDecisionResults { get; }

    /// <summary>
    /// R1710 / TOR INT 002 — offline-batch submissions (read-only). See
    /// <see cref="ICnasDbContext.OfflineBatchSubmissions"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.OfflineBatchSubmission> OfflineBatchSubmissions { get; }

    /// <summary>
    /// R1710 / TOR INT 002 — per-row payloads (read-only). See
    /// <see cref="ICnasDbContext.OfflineBatchRows"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.OfflineBatchRow> OfflineBatchRows { get; }

    /// <summary>
    /// R1810 / TOR BP 1.2-I — daily Treasury feed import registry
    /// (read-only). See <see cref="ICnasDbContext.TreasuryFeedImports"/> for
    /// the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.TreasuryFeedImport> TreasuryFeedImports { get; }

    /// <summary>
    /// R1810 / TOR BP 1.2-I — per-row payloads inside each Treasury feed
    /// import (read-only). See <see cref="ICnasDbContext.TreasuryFeedImportRows"/>
    /// for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.TreasuryFeedImportRow> TreasuryFeedImportRows { get; }

    /// <summary>
    /// R0203 / TOR CF 20.06 — per-source external-system ingestion-run registry
    /// (read-only). See <see cref="ICnasDbContext.ExternalSourceIngestionRuns"/>
    /// for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ExternalSourceIngestionRun> ExternalSourceIngestionRuns { get; }

    /// <summary>
    /// R2273 / TOR SEC 027 — generic 4-eyes admin requests (read-only). See
    /// <see cref="ICnasDbContext.SensitiveAdminActions"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.SensitiveAdminAction> SensitiveAdminActions { get; }

    /// <summary>
    /// R1202 / TOR §3.4-C — capitalised-payment requests (read-only). See
    /// <see cref="ICnasDbContext.CapitalisedPaymentRequests"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.CapitalisedPaymentRequest> CapitalisedPaymentRequests { get; }

    /// <summary>
    /// R1202 / TOR §3.4-C — finalised computation outcomes (read-only). See
    /// <see cref="ICnasDbContext.CapitalisedPaymentDecisions"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.CapitalisedPaymentDecision> CapitalisedPaymentDecisions { get; }

    /// <summary>
    /// R2279 / TOR SEC 033 — classification-catalog snapshots (read-only). See
    /// <see cref="ICnasDbContext.ClassificationCatalogSnapshots"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ClassificationCatalogSnapshot> ClassificationCatalogSnapshots { get; }

    /// <summary>
    /// R2279 / TOR SEC 033 — classification-catalog entries (read-only). See
    /// <see cref="ICnasDbContext.ClassificationCatalogEntries"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ClassificationCatalogEntry> ClassificationCatalogEntries { get; }

    /// <summary>
    /// R2279 / TOR SEC 033 — classification-drift findings (read-only). See
    /// <see cref="ICnasDbContext.ClassificationDriftFindings"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ClassificationDriftFinding> ClassificationDriftFindings { get; }

    /// <summary>
    /// R1403 / TOR §3.6-D — lifetime athlete-pension awards (read-only). See
    /// <see cref="ICnasDbContext.AthletePensionAwards"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.AthletePensionAward> AthletePensionAwards { get; }

    /// <summary>
    /// R1403 / TOR §3.6-D — athlete career-record child rows (read-only). See
    /// <see cref="ICnasDbContext.AthleteCareerRecords"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.AthleteCareerRecord> AthleteCareerRecords { get; }

    /// <summary>
    /// R1201 / R1402 / TOR §3.4-B / §3.6-C — international-agreements
    /// review cases (read-only). See
    /// <see cref="ICnasDbContext.IntlAgreementReviewCases"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.IntlAgreementReviewCase> IntlAgreementReviewCases { get; }

    /// <summary>
    /// R1201 / R1402 / TOR §3.4-B / §3.6-C — international-agreements
    /// review-step rows (read-only). See
    /// <see cref="ICnasDbContext.IntlAgreementReviewSteps"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.IntlAgreementReviewStep> IntlAgreementReviewSteps { get; }

    /// <summary>
    /// R2271 / TOR SEC 025 — ABAC rule sets (read-only). See
    /// <see cref="ICnasDbContext.AbacRuleSets"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.AbacRuleSet> AbacRuleSets { get; }

    /// <summary>
    /// R2271 / TOR SEC 025 — ABAC rules (read-only). See
    /// <see cref="ICnasDbContext.AbacRules"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.AbacRule> AbacRules { get; }

    /// <summary>
    /// R2430 / TOR M4 — migration-plan registry (read-only). See
    /// <see cref="ICnasDbContext.MigrationPlans"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.MigrationPlan> MigrationPlans { get; }

    /// <summary>
    /// R2430 / R2431 / TOR M4 — migration run records (read-only). See
    /// <see cref="ICnasDbContext.MigrationRuns"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.MigrationRun> MigrationRuns { get; }

    /// <summary>
    /// R2430 / R2431 / TOR M4 — migration batch counters (read-only). See
    /// <see cref="ICnasDbContext.MigrationBatches"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.MigrationBatch> MigrationBatches { get; }

    /// <summary>
    /// R2430 / R2433 / TOR M4 — migration findings (read-only). See
    /// <see cref="ICnasDbContext.MigrationFindings"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.MigrationFinding> MigrationFindings { get; }

    /// <summary>
    /// R2433 / TOR M4 — reconciliation reports (read-only). See
    /// <see cref="ICnasDbContext.ReconciliationReports"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ReconciliationReport> ReconciliationReports { get; }

    /// <summary>
    /// R2431 / TOR M4 — migration staging rows (read-only). See
    /// <see cref="ICnasDbContext.MigrationStagingRows"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.MigrationStagingRow> MigrationStagingRows { get; }

    /// <summary>
    /// R2307 / TOR SEC 060 — backup-policy registry (read-only). See
    /// <see cref="ICnasDbContext.BackupPolicies"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.BackupPolicy> BackupPolicies { get; }

    /// <summary>
    /// R2307 / TOR SEC 060 — backup-run ledger (read-only). See
    /// <see cref="ICnasDbContext.BackupRuns"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.BackupRun> BackupRuns { get; }

    /// <summary>
    /// R2307 / TOR SEC 060 — backup-integrity-check records (read-only). See
    /// <see cref="ICnasDbContext.BackupIntegrityChecks"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.BackupIntegrityCheck> BackupIntegrityChecks { get; }

    /// <summary>
    /// R2500 / TOR PIR 020-023 — helpdesk category registry (read-only). See
    /// <see cref="ICnasDbContext.SupportTicketCategories"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.SupportTicketCategory> SupportTicketCategories { get; }

    /// <summary>
    /// R2500 / TOR PIR 020-023 — helpdesk ticket aggregate (read-only). See
    /// <see cref="ICnasDbContext.SupportTickets"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.SupportTicket> SupportTickets { get; }

    /// <summary>
    /// R2500 / TOR PIR 020-023 — helpdesk ticket comments (read-only). See
    /// <see cref="ICnasDbContext.SupportTicketComments"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.SupportTicketComment> SupportTicketComments { get; }

    /// <summary>
    /// R2500 / TOR PIR 020-023 — helpdesk ticket SLA events (read-only). See
    /// <see cref="ICnasDbContext.SupportTicketSlaEvents"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.SupportTicketSlaEvent> SupportTicketSlaEvents { get; }

    /// <summary>
    /// R2501 / TOR PIR 024 — business-hours policy registry (read-only). See
    /// <see cref="ICnasDbContext.BusinessHoursPolicies"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.BusinessHoursPolicy> BusinessHoursPolicies { get; }

    /// <summary>
    /// R2502 / TOR PIR 025 — maintenance-window registry (read-only). See
    /// <see cref="ICnasDbContext.MaintenanceWindows"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.MaintenanceWindow> MaintenanceWindows { get; }

    /// <summary>
    /// R2503 / TOR PIR 022-023 — system-update schedule registry (read-only).
    /// See <see cref="ICnasDbContext.SystemUpdateSchedules"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.SystemUpdateSchedule> SystemUpdateSchedules { get; }

    /// <summary>
    /// R2504 / TOR PIR 024 — system-update event ledger (read-only). See
    /// <see cref="ICnasDbContext.SystemUpdateEvents"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.SystemUpdateEvent> SystemUpdateEvents { get; }

    /// <summary>
    /// R2505 / TOR PIR 030-033 — change-management aggregate (read-only). See
    /// <see cref="ICnasDbContext.ChangeRequests"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ChangeRequest> ChangeRequests { get; }

    /// <summary>
    /// R2506 / TOR PIR 037-040 — quality-risk registry (read-only). See
    /// <see cref="ICnasDbContext.QualityRisks"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.QualityRisk> QualityRisks { get; }

    /// <summary>
    /// R2506 / TOR PIR 037-040 — preventive actions (read-only). See
    /// <see cref="ICnasDbContext.QualityRiskPreventiveActions"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.QualityRiskPreventiveAction> QualityRiskPreventiveActions { get; }

    /// <summary>
    /// R0103 / TOR CF 14.02 — inbound integration-event dedup ledger (read-only).
    /// See <see cref="ICnasDbContext.ProcessedIntegrationEvents"/> for the
    /// write-side contract and idempotency rules.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ProcessedIntegrationEvent> ProcessedIntegrationEvents { get; }

    /// <summary>
    /// R0196 / TOR CF 23.02 — audit-category registry (read-only). See
    /// <see cref="ICnasDbContext.AuditCategories"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.AuditCategory> AuditCategories { get; }

    /// <summary>
    /// R0302 / TOR §2.1 — contributor source-system change history (read-only).
    /// See <see cref="ICnasDbContext.ContributorSourceChangeHistory"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ContributorSourceChangeHistory> ContributorSourceChangeHistory { get; }

    /// <summary>
    /// R0322 / TOR UI 014 — application-attachment metadata rows (read-only).
    /// See <see cref="ICnasDbContext.ApplicationAttachments"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ApplicationAttachment> ApplicationAttachments { get; }

    /// <summary>
    /// R0191 / TOR SEC 050 / TOR ARH 028 — application-level entity-history
    /// snapshot rows (read-only). See <see cref="ICnasDbContext.EntityHistoryRows"/>
    /// for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.EntityHistoryRow> EntityHistoryRows { get; }

    /// <summary>
    /// R0570 / TOR CF 08.02 — round-robin examiner assignment cursor
    /// (read-only). See <see cref="ICnasDbContext.ExaminerAssignmentCursors"/>
    /// for the write-side contract and the singleton-row pattern.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.ExaminerAssignmentCursor> ExaminerAssignmentCursors { get; }

    /// <summary>
    /// R0200 / TOR CF 20.01-03, MR 012 — operator-configurable cron-override rows
    /// (read-only). See <see cref="ICnasDbContext.JobScheduleOverrides"/> for the
    /// write-side contract and the natural-key uniqueness rule.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.JobScheduleOverride> JobScheduleOverrides { get; }

    /// <summary>
    /// R0540 / TOR CF 05.01 (iter 134) — admin-configurable rules driving the
    /// rule-based workflow-task auto-creation path (read-only). See
    /// <see cref="ICnasDbContext.WorkflowAutoCreationRules"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.WorkflowAutoCreationRule> WorkflowAutoCreationRules { get; }

    /// <summary>
    /// R0673 / TOR CF 18.12 — granular permission matrix rows (read-only).
    /// See <see cref="ICnasDbContext.GranularPermissionAssignments"/> for the
    /// write-side contract and the triple-uniqueness rule.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.GranularPermissionAssignment> GranularPermissionAssignments { get; }

    /// <summary>
    /// R0602 / TOR CF 11.03 — territorial paper-channel fulfilment workflow
    /// rows (read-only). See <see cref="ICnasDbContext.PaperFulfilmentRecords"/>
    /// for the write-side contract and the state machine.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.PaperFulfilmentRecord> PaperFulfilmentRecords { get; }

    /// <summary>
    /// R0933 / TOR §10.1 — append-only supersession rows (read-only). See
    /// <see cref="ICnasDbContext.DecisionSupersessions"/> for the write-side
    /// contract and the natural-key + idempotency rules.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.DecisionSupersession> DecisionSupersessions { get; }

    /// <summary>
    /// R1504 / TOR §3.7-E — payment-suspension lifecycle records (read-only).
    /// See <see cref="ICnasDbContext.PaymentSuspensionRecords"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.PaymentSuspensionRecord> PaymentSuspensionRecords { get; }

    /// <summary>
    /// R0115 / TOR CF 14.07 — MNotify template registry (read-only).
    /// See <see cref="ICnasDbContext.MNotifyTemplates"/> for the write-side
    /// contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.MNotifyTemplate> MNotifyTemplates { get; }

    /// <summary>
    /// R0116 + R0195 / TOR SEC 054-055 — MLog dual-write category filter
    /// (read-only). See <see cref="ICnasDbContext.MLogCategoryConfigs"/> for
    /// the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.MLogCategoryConfig> MLogCategoryConfigs { get; }

    /// <summary>
    /// R0057 / TOR SEC 026 + CF 16.11 — time-bounded delegation grants
    /// (read-only). See <see cref="ICnasDbContext.DelegationGrants"/> for the
    /// write-side contract and the active-grant predicate.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.DelegationGrant> DelegationGrants { get; }

    /// <summary>
    /// R2161 / TOR INT 002 — generic CnasUser-facing offline-batch job registry
    /// (read-only). See <see cref="ICnasDbContext.OfflineBatchJobs"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.OfflineBatchJob> OfflineBatchJobs { get; }

    /// <summary>
    /// R2190-R2200 / TOR §15.6 FLEX 006 — read-only projection over the
    /// dynamic-entity-attribute (EAV) sidecar. See
    /// <see cref="ICnasDbContext.EntityAttributeValues"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.EntityAttributeValue> EntityAttributeValues { get; }

    /// <summary>
    /// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — read-only projection over the
    /// insolvency lifecycle registry. See
    /// <see cref="ICnasDbContext.InsolvencyCases"/> for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.InsolvencyCase> InsolvencyCases { get; }

    /// <summary>
    /// R0834 / TOR Annex 1 §8.1.4.5 — read-only projection over the insolvency
    /// claims sub-table. See <see cref="ICnasDbContext.InsolvencyClaims"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.InsolvencyClaim> InsolvencyClaims { get; }

    /// <summary>
    /// R0834 / TOR Annex 1 §8.1.4.5 — read-only projection over the insolvency
    /// payments sub-table. See <see cref="ICnasDbContext.InsolvencyPayments"/>
    /// for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.InsolvencyPayment> InsolvencyPayments { get; }

    /// <summary>
    /// R1000..R1034 / TOR §3.2-AB..AD — read-only projection over the
    /// voucher-quota registry. See <see cref="ICnasDbContext.VoucherQuotas"/>
    /// for the write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.VoucherQuota> VoucherQuotas { get; }

    /// <summary>
    /// R1000..R1034 / TOR §3.2-Z — read-only projection over the
    /// recurrent-payment schedule registry. See
    /// <see cref="ICnasDbContext.RecurrentPaymentSchedules"/> for the
    /// write-side contract.
    /// </summary>
    IQueryable<Cnas.Ps.Core.Domain.RecurrentPaymentSchedule> RecurrentPaymentSchedules { get; }
}
