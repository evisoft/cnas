using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Cnas.Ps.Infrastructure.Persistence;

/// <summary>
/// EF Core <see cref="DbContext"/> for SI PS. Maps all 12 information objects from TOR §2.3
/// to PostgreSQL tables, applies global soft-delete and concurrency-token conventions, and
/// enforces case-sensitive citext lookups where required.
/// </summary>
/// <remarks>
/// <para>
/// Two constructors are provided. The runtime composition root uses the
/// <see cref="CnasDbContext(DbContextOptions{CnasDbContext}, IFieldEncryptor)"/>
/// overload so that highly-confidential string columns (per CLAUDE.md §5.7
/// and TOR SEC 035) round-trip through <see cref="EncryptedStringConverter"/>
/// transparently. The single-argument overload remains for tests and tooling
/// (e.g. <c>dotnet ef migrations add</c>) that legitimately do not exercise
/// encrypted columns — those callers see plaintext at rest in the InMemory
/// store, which is exactly the right behaviour for unit tests that don't
/// care about the converter.
/// </para>
/// </remarks>
public class CnasDbContext : DbContext, ICnasDbContext, IReadOnlyCnasDbContext
{
    /// <summary>
    /// Field encryptor wired into the model's value converters when present.
    /// <c>null</c> for callers that constructed the context via the
    /// single-argument constructor (tests and EF tooling).
    /// </summary>
    private readonly IFieldEncryptor? _fieldEncryptor;

    /// <summary>
    /// Whether this context was constructed with a field encryptor. Retained
    /// for callers that only need a boolean check — model-cache partitioning
    /// now uses the richer <see cref="EncryptorIdentity"/> instead (see
    /// BUG-003 in <see cref="CnasModelCacheKeyFactory"/>).
    /// </summary>
    internal bool HasFieldEncryptor => _fieldEncryptor is not null;

    /// <summary>
    /// Stable identity of the wired <see cref="IFieldEncryptor"/> implementation —
    /// the encryptor's CLR <see cref="System.Type.FullName"/>, or <c>null</c>
    /// when no encryptor is wired. Read by <see cref="CnasModelCacheKeyFactory"/>
    /// to partition the EF compiled-model cache by encryptor type so two
    /// fixtures sharing a process with different encryptor implementations
    /// (e.g. AES vs missing-key sentinel) compile independent models. See
    /// that factory's remarks for the BUG-003 failure mode this discriminator
    /// prevents.
    /// </summary>
    /// <remarks>
    /// The full CLR type name is sufficient because the value-converter wiring
    /// depends on the encryptor's CLR type, not on its key material — two
    /// <see cref="Cnas.Ps.Infrastructure.Security.AesFieldEncryptor"/>
    /// instances built with different key bytes share an identical model
    /// shape and therefore share a cache entry.
    /// </remarks>
    internal string? EncryptorIdentity => _fieldEncryptor?.GetType().FullName;

    /// <summary>
    /// Test/tooling constructor — no field-encryption converter is wired.
    /// Encrypted columns are persisted and read as plaintext. Do NOT use this
    /// constructor in production; the composition root uses the DI-driven
    /// overload that resolves <see cref="IFieldEncryptor"/>.
    /// </summary>
    /// <param name="options">EF Core options (provider, connection, warnings).</param>
    public CnasDbContext(DbContextOptions<CnasDbContext> options) : base(options)
    {
        _fieldEncryptor = null;
    }

    /// <summary>
    /// Production constructor — wires the application-level field encryptor
    /// into the entity model so that columns annotated for encryption are
    /// transparently sealed at rest. Resolved by DI through
    /// <c>ActivatorUtilities</c> as part of <c>AddDbContext</c>.
    /// </summary>
    /// <param name="options">EF Core options (provider, connection, warnings).</param>
    /// <param name="fieldEncryptor">
    /// Application-level field encryptor used by the encrypted-string value
    /// converter. Resolved as a singleton from DI; thread-safe per
    /// <see cref="Cnas.Ps.Infrastructure.Security.AesFieldEncryptor"/>.
    /// </param>
    public CnasDbContext(DbContextOptions<CnasDbContext> options, IFieldEncryptor fieldEncryptor) : base(options)
    {
        ArgumentNullException.ThrowIfNull(fieldEncryptor);
        _fieldEncryptor = fieldEncryptor;
    }

    /// <summary>
    /// Subclass-only constructor (R0026) — accepts the EF Core non-generic
    /// <see cref="DbContextOptions"/> so derived contexts (e.g.
    /// <see cref="CnasReadOnlyDbContext"/>) can forward their own
    /// <c>DbContextOptions&lt;T&gt;</c> instance into the base without paying
    /// the cost of duplicating <c>OnModelCreating</c>. The encryptor is left
    /// unwired — the read-only context never writes, so it never reaches the
    /// converter; if a future subclass needs encrypted writes it must pass the
    /// encryptor through to the two-argument overload.
    /// </summary>
    /// <param name="options">EF Core options forwarded by the derived context.</param>
    protected CnasDbContext(DbContextOptions options) : base(options)
    {
        _fieldEncryptor = null;
    }

    /// <inheritdoc />
    public DbSet<Solicitant> Solicitants => Set<Solicitant>();

    /// <inheritdoc />
    public DbSet<Contributor> Contributors => Set<Contributor>();

    /// <inheritdoc />
    public DbSet<InsuredPerson> InsuredPersons => Set<InsuredPerson>();

    /// <inheritdoc />
    public DbSet<ServiceApplication> Applications => Set<ServiceApplication>();

    /// <inheritdoc />
    public DbSet<ApplicationVersion> ApplicationVersions => Set<ApplicationVersion>();

    /// <inheritdoc />
    public DbSet<Dossier> Dossiers => Set<Dossier>();

    /// <inheritdoc />
    public DbSet<Document> Documents => Set<Document>();

    /// <inheritdoc />
    public DbSet<Report> Reports => Set<Report>();

    /// <inheritdoc />
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    /// <inheritdoc />
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <inheritdoc />
    public DbSet<WorkflowTask> WorkflowTasks => Set<WorkflowTask>();

    /// <inheritdoc />
    public DbSet<ServicePassport> ServicePassports => Set<ServicePassport>();

    /// <inheritdoc />
    public DbSet<Classifier> Classifiers => Set<Classifier>();

    /// <inheritdoc />
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <inheritdoc />
    public DbSet<FailedJob> FailedJobs => Set<FailedJob>();

    /// <inheritdoc />
    public DbSet<FileImmutabilityRecord> FileImmutabilityRecords => Set<FileImmutabilityRecord>();

    /// <inheritdoc />
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();

    /// <inheritdoc />
    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();

    /// <inheritdoc />
    public DbSet<TemplateVariant> TemplateVariants => Set<TemplateVariant>();

    /// <inheritdoc />
    public DbSet<TemplateLanguageCoverageFinding> TemplateLanguageCoverageFindings =>
        Set<TemplateLanguageCoverageFinding>();

    /// <inheritdoc />
    public DbSet<MPayOrder> MPayOrders => Set<MPayOrder>();

    /// <inheritdoc />
    public DbSet<PendingAdminAction> PendingAdminActions => Set<PendingAdminAction>();

    /// <inheritdoc />
    public DbSet<SensitiveAdminAction> SensitiveAdminActions => Set<SensitiveAdminAction>();

    /// <inheritdoc />
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <inheritdoc />
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    /// <inheritdoc />
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();

    /// <inheritdoc />
    public DbSet<SiemForwarderState> SiemForwarderStates => Set<SiemForwarderState>();

    /// <inheritdoc />
    public DbSet<SecurityAlertRule> SecurityAlertRules => Set<SecurityAlertRule>();

    /// <inheritdoc />
    public DbSet<SecurityAlertEvaluatorState> SecurityAlertEvaluatorStates => Set<SecurityAlertEvaluatorState>();

    /// <inheritdoc />
    public DbSet<AuditPolicy> AuditPolicies => Set<AuditPolicy>();

    /// <inheritdoc />
    public DbSet<AuditFieldPolicy> AuditFieldPolicies => Set<AuditFieldPolicy>();

    /// <inheritdoc />
    public DbSet<WorkflowNotificationStrategy> WorkflowNotificationStrategies => Set<WorkflowNotificationStrategy>();

    /// <inheritdoc />
    public DbSet<BulkSelection> BulkSelections => Set<BulkSelection>();

    /// <inheritdoc />
    public DbSet<BulkOperationRun> BulkOperationRuns => Set<BulkOperationRun>();

    /// <inheritdoc />
    public DbSet<UserAbsence> UserAbsences => Set<UserAbsence>();

    /// <inheritdoc />
    public DbSet<WorkflowStepAcl> WorkflowStepAcls => Set<WorkflowStepAcl>();

    /// <inheritdoc />
    public DbSet<WorkflowTaskStepHistory> WorkflowTaskStepHistories => Set<WorkflowTaskStepHistory>();

    /// <inheritdoc />
    public DbSet<CnasBranch> CnasBranches => Set<CnasBranch>();

    /// <inheritdoc />
    public DbSet<PersonalAccount> PersonalAccounts => Set<PersonalAccount>();

    /// <inheritdoc />
    public DbSet<PersonalAccountEntry> PersonalAccountEntries => Set<PersonalAccountEntry>();

    /// <inheritdoc />
    public DbSet<BenefitPayment> BenefitPayments => Set<BenefitPayment>();

    /// <inheritdoc />
    public DbSet<PayerAddress> PayerAddresses => Set<PayerAddress>();

    /// <inheritdoc />
    public DbSet<PayerContact> PayerContacts => Set<PayerContact>();

    /// <inheritdoc />
    public DbSet<PayerActivityCAEM> PayerActivities => Set<PayerActivityCAEM>();

    /// <inheritdoc />
    public DbSet<PayerHistory> PayerHistory => Set<PayerHistory>();

    /// <inheritdoc />
    public DbSet<PayerBankAccount> PayerBankAccounts => Set<PayerBankAccount>();

    /// <inheritdoc />
    public DbSet<PayerSecondaryContact> PayerSecondaryContacts => Set<PayerSecondaryContact>();

    /// <inheritdoc />
    public DbSet<ContributorAddress> ContributorAddresses => Set<ContributorAddress>();

    /// <inheritdoc />
    public DbSet<ContributorContact> ContributorContacts => Set<ContributorContact>();

    /// <inheritdoc />
    public DbSet<ContributorActivityPeriod> ContributorActivityPeriods => Set<ContributorActivityPeriod>();

    /// <inheritdoc />
    public DbSet<ContributorCivilStatus> ContributorCivilStatuses => Set<ContributorCivilStatus>();

    /// <inheritdoc />
    public DbSet<ContributorSocialInsuranceContract> ContributorSocialInsuranceContracts =>
        Set<ContributorSocialInsuranceContract>();

    /// <inheritdoc />
    public DbSet<ContributorPre1999PeriodCarnetMunca> ContributorPre1999PeriodsCarnetMunca =>
        Set<ContributorPre1999PeriodCarnetMunca>();

    /// <inheritdoc />
    public DbSet<LaborBooklet> LaborBooklets => Set<LaborBooklet>();

    /// <inheritdoc />
    public DbSet<InsuredPersonPre1999Period> InsuredPersonPre1999Periods => Set<InsuredPersonPre1999Period>();

    /// <inheritdoc />
    public DbSet<Pre1999StagiuRecord> Pre1999StagiuRecords => Set<Pre1999StagiuRecord>();

    /// <inheritdoc />
    public DbSet<ProfileUpdateRequest> ProfileUpdateRequests => Set<ProfileUpdateRequest>();

    /// <inheritdoc />
    public DbSet<ProfileRefreshRun> ProfileRefreshRuns => Set<ProfileRefreshRun>();

    /// <inheritdoc />
    public DbSet<KpiSnapshot> KpiSnapshots => Set<KpiSnapshot>();

    /// <inheritdoc />
    public DbSet<ContributorPeriodProjection> ContributorPeriodProjections =>
        Set<ContributorPeriodProjection>();

    /// <inheritdoc />
    public DbSet<WorkflowGraphNode> WorkflowGraphNodes => Set<WorkflowGraphNode>();

    /// <inheritdoc />
    public DbSet<WorkflowGraphEdge> WorkflowGraphEdges => Set<WorkflowGraphEdge>();

    /// <inheritdoc />
    public DbSet<TranslationKey> TranslationKeys => Set<TranslationKey>();

    /// <inheritdoc />
    public DbSet<TranslationValue> TranslationValues => Set<TranslationValue>();

    /// <inheritdoc />
    public DbSet<HelpTopic> HelpTopics => Set<HelpTopic>();

    /// <inheritdoc />
    public DbSet<HelpTopicTranslation> HelpTopicTranslations => Set<HelpTopicTranslation>();

    /// <inheritdoc />
    public DbSet<ReportTemplate> ReportTemplates => Set<ReportTemplate>();

    /// <inheritdoc />
    public DbSet<ReportRun> ReportRuns => Set<ReportRun>();

    /// <inheritdoc />
    public DbSet<AttachmentRecord> AttachmentRecords => Set<AttachmentRecord>();

    /// <inheritdoc />
    public DbSet<ReportJob> ReportJobs => Set<ReportJob>();

    /// <inheritdoc />
    public DbSet<Declaration> Declarations => Set<Declaration>();

    /// <inheritdoc />
    public DbSet<MonthlyContributionCalculation> MonthlyContributionCalculations =>
        Set<MonthlyContributionCalculation>();

    /// <inheritdoc />
    public DbSet<LatePaymentPenalty> LatePaymentPenalties => Set<LatePaymentPenalty>();

    /// <inheritdoc />
    public DbSet<ManagementPeriodClose> ManagementPeriodCloses => Set<ManagementPeriodClose>();

    /// <inheritdoc />
    public DbSet<Rev5Declaration> Rev5Declarations => Set<Rev5Declaration>();

    /// <inheritdoc />
    public DbSet<Rev5DeclarationRow> Rev5DeclarationRows => Set<Rev5DeclarationRow>();

    /// <inheritdoc />
    public DbSet<InsuredPersonContributionAdjustment> InsuredPersonContributionAdjustments =>
        Set<InsuredPersonContributionAdjustment>();

    /// <inheritdoc />
    public DbSet<TreasuryPaymentReceipt> TreasuryPaymentReceipts => Set<TreasuryPaymentReceipt>();

    /// <inheritdoc />
    public DbSet<Claim> Claims => Set<Claim>();

    /// <inheritdoc />
    public DbSet<ClaimPayment> ClaimPayments => Set<ClaimPayment>();

    /// <inheritdoc />
    public DbSet<BassRefund> BassRefunds => Set<BassRefund>();

    /// <inheritdoc />
    public DbSet<PaymentCorrection> PaymentCorrections => Set<PaymentCorrection>();

    /// <inheritdoc />
    public DbSet<PenaltyRepaymentPlan> PenaltyRepaymentPlans => Set<PenaltyRepaymentPlan>();

    /// <inheritdoc />
    public DbSet<PenaltyRepaymentInstallment> PenaltyRepaymentInstallments => Set<PenaltyRepaymentInstallment>();

    /// <inheritdoc />
    public DbSet<ExecutoryDocument> ExecutoryDocuments => Set<ExecutoryDocument>();

    /// <inheritdoc />
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();

    /// <inheritdoc />
    public DbSet<UserGroupParent> UserGroupParents => Set<UserGroupParent>();

    /// <inheritdoc />
    public DbSet<UserGroupMembership> UserGroupMemberships => Set<UserGroupMembership>();

    /// <inheritdoc />
    public DbSet<IntegrityCheckRun> IntegrityCheckRuns => Set<IntegrityCheckRun>();

    /// <inheritdoc />
    public DbSet<IntegrityCheckFinding> IntegrityCheckFindings => Set<IntegrityCheckFinding>();

    /// <inheritdoc />
    public DbSet<ReportDistributionRule> ReportDistributionRules => Set<ReportDistributionRule>();

    /// <inheritdoc />
    public DbSet<ReportDistributionDispatch> ReportDistributionDispatches => Set<ReportDistributionDispatch>();

    /// <inheritdoc />
    public DbSet<LegalChangeEvent> LegalChangeEvents => Set<LegalChangeEvent>();

    /// <inheritdoc />
    public DbSet<RecalculationRun> RecalculationRuns => Set<RecalculationRun>();

    /// <inheritdoc />
    public DbSet<RecalculationDecisionResult> RecalculationDecisionResults =>
        Set<RecalculationDecisionResult>();

    /// <inheritdoc />
    public DbSet<OfflineBatchSubmission> OfflineBatchSubmissions => Set<OfflineBatchSubmission>();

    /// <inheritdoc />
    public DbSet<OfflineBatchRow> OfflineBatchRows => Set<OfflineBatchRow>();

    /// <inheritdoc />
    public DbSet<TreasuryFeedImport> TreasuryFeedImports => Set<TreasuryFeedImport>();

    /// <inheritdoc />
    public DbSet<TreasuryFeedImportRow> TreasuryFeedImportRows => Set<TreasuryFeedImportRow>();

    /// <inheritdoc />
    public DbSet<ExternalSourceIngestionRun> ExternalSourceIngestionRuns =>
        Set<ExternalSourceIngestionRun>();

    /// <inheritdoc />
    public DbSet<CapitalisedPaymentRequest> CapitalisedPaymentRequests => Set<CapitalisedPaymentRequest>();

    /// <inheritdoc />
    public DbSet<CapitalisedPaymentDecision> CapitalisedPaymentDecisions => Set<CapitalisedPaymentDecision>();

    /// <inheritdoc />
    public DbSet<ClassificationCatalogSnapshot> ClassificationCatalogSnapshots =>
        Set<ClassificationCatalogSnapshot>();

    /// <inheritdoc />
    public DbSet<ClassificationCatalogEntry> ClassificationCatalogEntries =>
        Set<ClassificationCatalogEntry>();

    /// <inheritdoc />
    public DbSet<ClassificationDriftFinding> ClassificationDriftFindings =>
        Set<ClassificationDriftFinding>();

    /// <inheritdoc />
    public DbSet<AthletePensionAward> AthletePensionAwards => Set<AthletePensionAward>();

    /// <inheritdoc />
    public DbSet<AthleteCareerRecord> AthleteCareerRecords => Set<AthleteCareerRecord>();

    /// <inheritdoc />
    public DbSet<IntlAgreementReviewCase> IntlAgreementReviewCases =>
        Set<IntlAgreementReviewCase>();

    /// <inheritdoc />
    public DbSet<IntlAgreementReviewStep> IntlAgreementReviewSteps =>
        Set<IntlAgreementReviewStep>();

    /// <inheritdoc />
    public DbSet<AbacRuleSet> AbacRuleSets => Set<AbacRuleSet>();

    /// <inheritdoc />
    public DbSet<AbacRule> AbacRules => Set<AbacRule>();

    /// <inheritdoc />
    public DbSet<MigrationPlan> MigrationPlans => Set<MigrationPlan>();

    /// <inheritdoc />
    public DbSet<MigrationRun> MigrationRuns => Set<MigrationRun>();

    /// <inheritdoc />
    public DbSet<MigrationBatch> MigrationBatches => Set<MigrationBatch>();

    /// <inheritdoc />
    public DbSet<MigrationFinding> MigrationFindings => Set<MigrationFinding>();

    /// <inheritdoc />
    public DbSet<ReconciliationReport> ReconciliationReports => Set<ReconciliationReport>();

    /// <inheritdoc />
    public DbSet<MigrationStagingRow> MigrationStagingRows => Set<MigrationStagingRow>();

    /// <inheritdoc />
    public DbSet<BackupPolicy> BackupPolicies => Set<BackupPolicy>();

    /// <inheritdoc />
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();

    /// <inheritdoc />
    public DbSet<BackupIntegrityCheck> BackupIntegrityChecks => Set<BackupIntegrityCheck>();

    /// <inheritdoc />
    public DbSet<SupportTicketCategory> SupportTicketCategories => Set<SupportTicketCategory>();

    /// <inheritdoc />
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();

    /// <inheritdoc />
    public DbSet<SupportTicketComment> SupportTicketComments => Set<SupportTicketComment>();

    /// <inheritdoc />
    public DbSet<SupportTicketSlaEvent> SupportTicketSlaEvents => Set<SupportTicketSlaEvent>();

    /// <inheritdoc />
    public DbSet<BusinessHoursPolicy> BusinessHoursPolicies => Set<BusinessHoursPolicy>();

    /// <inheritdoc />
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();

    /// <inheritdoc />
    public DbSet<SystemUpdateSchedule> SystemUpdateSchedules => Set<SystemUpdateSchedule>();

    /// <inheritdoc />
    public DbSet<SystemUpdateEvent> SystemUpdateEvents => Set<SystemUpdateEvent>();

    /// <inheritdoc />
    public DbSet<ChangeRequest> ChangeRequests => Set<ChangeRequest>();

    /// <inheritdoc />
    public DbSet<QualityRisk> QualityRisks => Set<QualityRisk>();

    /// <inheritdoc />
    public DbSet<QualityRiskPreventiveAction> QualityRiskPreventiveActions => Set<QualityRiskPreventiveAction>();

    /// <inheritdoc />
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    /// <inheritdoc />
    public DbSet<AuditCategory> AuditCategories => Set<AuditCategory>();

    /// <inheritdoc />
    public DbSet<ContributorSourceChangeHistory> ContributorSourceChangeHistory =>
        Set<ContributorSourceChangeHistory>();

    /// <inheritdoc />
    public DbSet<ApplicationAttachment> ApplicationAttachments => Set<ApplicationAttachment>();

    /// <inheritdoc />
    public DbSet<EntityHistoryRow> EntityHistoryRows => Set<EntityHistoryRow>();

    /// <inheritdoc />
    public DbSet<ExaminerAssignmentCursor> ExaminerAssignmentCursors => Set<ExaminerAssignmentCursor>();

    /// <inheritdoc />
    public DbSet<JobScheduleOverride> JobScheduleOverrides => Set<JobScheduleOverride>();

    /// <inheritdoc />
    public DbSet<WorkflowAutoCreationRule> WorkflowAutoCreationRules => Set<WorkflowAutoCreationRule>();

    /// <inheritdoc />
    public DbSet<GranularPermissionAssignment> GranularPermissionAssignments => Set<GranularPermissionAssignment>();

    /// <inheritdoc />
    public DbSet<PaperFulfilmentRecord> PaperFulfilmentRecords => Set<PaperFulfilmentRecord>();

    /// <inheritdoc />
    public DbSet<DecisionSupersession> DecisionSupersessions => Set<DecisionSupersession>();

    /// <inheritdoc />
    public DbSet<PaymentSuspensionRecord> PaymentSuspensionRecords => Set<PaymentSuspensionRecord>();

    /// <inheritdoc />
    public DbSet<MNotifyTemplate> MNotifyTemplates => Set<MNotifyTemplate>();

    /// <inheritdoc />
    public DbSet<MLogCategoryConfig> MLogCategoryConfigs => Set<MLogCategoryConfig>();

    /// <inheritdoc />
    public DbSet<DelegationGrant> DelegationGrants => Set<DelegationGrant>();

    /// <inheritdoc />
    public DbSet<OfflineBatchJob> OfflineBatchJobs => Set<OfflineBatchJob>();

    /// <inheritdoc />
    public DbSet<EntityAttributeValue> EntityAttributeValues => Set<EntityAttributeValue>();

    /// <inheritdoc />
    public DbSet<InsolvencyCase> InsolvencyCases => Set<InsolvencyCase>();

    /// <inheritdoc />
    public DbSet<InsolvencyClaim> InsolvencyClaims => Set<InsolvencyClaim>();

    /// <inheritdoc />
    public DbSet<InsolvencyPayment> InsolvencyPayments => Set<InsolvencyPayment>();

    /// <inheritdoc />
    public DbSet<VoucherQuota> VoucherQuotas => Set<VoucherQuota>();

    /// <inheritdoc />
    public DbSet<RecurrentPaymentSchedule> RecurrentPaymentSchedules => Set<RecurrentPaymentSchedule>();

    // ─────────────────────── IReadOnlyCnasDbContext explicit projections ───────────────────────
    //
    // R0026 — every DbSet exposed via ICnasDbContext also surfaces here as an
    // IQueryable<T> with AsNoTracking() applied. Reporting and listing services
    // depend on IReadOnlyCnasDbContext; the explicit-interface impl below means
    // the SAME CnasDbContext instance can satisfy both contracts. This keeps the
    // 51 existing test sites (each instantiating ReportingService with a vanilla
    // CnasDbContext) compiling and lets the test fixture share one InMemory
    // store between writer and reader views.
    //
    // AsNoTracking is safe here because these projections are reached ONLY via
    // the read-only interface — code paths that go through ICnasDbContext
    // continue to use the tracked DbSet<T> properties above.

    /// <inheritdoc />
    IQueryable<Solicitant> IReadOnlyCnasDbContext.Solicitants => Solicitants.AsNoTracking();

    /// <inheritdoc />
    IQueryable<Contributor> IReadOnlyCnasDbContext.Contributors => Contributors.AsNoTracking();

    /// <inheritdoc />
    IQueryable<InsuredPerson> IReadOnlyCnasDbContext.InsuredPersons => InsuredPersons.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ServiceApplication> IReadOnlyCnasDbContext.Applications => Applications.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ApplicationVersion> IReadOnlyCnasDbContext.ApplicationVersions => ApplicationVersions.AsNoTracking();

    /// <inheritdoc />
    IQueryable<Dossier> IReadOnlyCnasDbContext.Dossiers => Dossiers.AsNoTracking();

    /// <inheritdoc />
    IQueryable<Document> IReadOnlyCnasDbContext.Documents => Documents.AsNoTracking();

    /// <inheritdoc />
    IQueryable<Report> IReadOnlyCnasDbContext.Reports => Reports.AsNoTracking();

    /// <inheritdoc />
    IQueryable<UserProfile> IReadOnlyCnasDbContext.UserProfiles => UserProfiles.AsNoTracking();

    /// <inheritdoc />
    IQueryable<Notification> IReadOnlyCnasDbContext.Notifications => Notifications.AsNoTracking();

    /// <inheritdoc />
    IQueryable<WorkflowTask> IReadOnlyCnasDbContext.WorkflowTasks => WorkflowTasks.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ServicePassport> IReadOnlyCnasDbContext.ServicePassports => ServicePassports.AsNoTracking();

    /// <inheritdoc />
    IQueryable<Classifier> IReadOnlyCnasDbContext.Classifiers => Classifiers.AsNoTracking();

    /// <inheritdoc />
    IQueryable<AuditLog> IReadOnlyCnasDbContext.AuditLogs => AuditLogs.AsNoTracking();

    /// <inheritdoc />
    IQueryable<FailedJob> IReadOnlyCnasDbContext.FailedJobs => FailedJobs.AsNoTracking();

    /// <inheritdoc />
    IQueryable<FileImmutabilityRecord> IReadOnlyCnasDbContext.FileImmutabilityRecords =>
        FileImmutabilityRecords.AsNoTracking();

    /// <inheritdoc />
    IQueryable<WorkflowDefinition> IReadOnlyCnasDbContext.WorkflowDefinitions => WorkflowDefinitions.AsNoTracking();

    /// <inheritdoc />
    IQueryable<DocumentTemplate> IReadOnlyCnasDbContext.DocumentTemplates => DocumentTemplates.AsNoTracking();

    /// <inheritdoc />
    IQueryable<TemplateVariant> IReadOnlyCnasDbContext.TemplateVariants => TemplateVariants.AsNoTracking();

    /// <inheritdoc />
    IQueryable<TemplateLanguageCoverageFinding> IReadOnlyCnasDbContext.TemplateLanguageCoverageFindings =>
        TemplateLanguageCoverageFindings.AsNoTracking();

    /// <inheritdoc />
    IQueryable<MPayOrder> IReadOnlyCnasDbContext.MPayOrders => MPayOrders.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PendingAdminAction> IReadOnlyCnasDbContext.PendingAdminActions => PendingAdminActions.AsNoTracking();

    /// <inheritdoc />
    IQueryable<SensitiveAdminAction> IReadOnlyCnasDbContext.SensitiveAdminActions => SensitiveAdminActions.AsNoTracking();

    /// <inheritdoc />
    IQueryable<RefreshToken> IReadOnlyCnasDbContext.RefreshTokens => RefreshTokens.AsNoTracking();

    /// <inheritdoc />
    IQueryable<UserSession> IReadOnlyCnasDbContext.UserSessions => UserSessions.AsNoTracking();

    /// <inheritdoc />
    IQueryable<SavedSearch> IReadOnlyCnasDbContext.SavedSearches => SavedSearches.AsNoTracking();

    /// <inheritdoc />
    IQueryable<SiemForwarderState> IReadOnlyCnasDbContext.SiemForwarderStates => SiemForwarderStates.AsNoTracking();

    /// <inheritdoc />
    IQueryable<SecurityAlertRule> IReadOnlyCnasDbContext.SecurityAlertRules => SecurityAlertRules.AsNoTracking();

    /// <inheritdoc />
    IQueryable<SecurityAlertEvaluatorState> IReadOnlyCnasDbContext.SecurityAlertEvaluatorStates => SecurityAlertEvaluatorStates.AsNoTracking();

    /// <inheritdoc />
    IQueryable<AuditPolicy> IReadOnlyCnasDbContext.AuditPolicies => AuditPolicies.AsNoTracking();

    /// <inheritdoc />
    IQueryable<AuditFieldPolicy> IReadOnlyCnasDbContext.AuditFieldPolicies => AuditFieldPolicies.AsNoTracking();

    /// <inheritdoc />
    IQueryable<WorkflowNotificationStrategy> IReadOnlyCnasDbContext.WorkflowNotificationStrategies =>
        WorkflowNotificationStrategies.AsNoTracking();

    /// <inheritdoc />
    IQueryable<BulkSelection> IReadOnlyCnasDbContext.BulkSelections => BulkSelections.AsNoTracking();

    /// <inheritdoc />
    IQueryable<BulkOperationRun> IReadOnlyCnasDbContext.BulkOperationRuns => BulkOperationRuns.AsNoTracking();

    /// <inheritdoc />
    IQueryable<UserAbsence> IReadOnlyCnasDbContext.UserAbsences => UserAbsences.AsNoTracking();

    /// <inheritdoc />
    IQueryable<WorkflowStepAcl> IReadOnlyCnasDbContext.WorkflowStepAcls => WorkflowStepAcls.AsNoTracking();

    /// <inheritdoc />
    IQueryable<WorkflowTaskStepHistory> IReadOnlyCnasDbContext.WorkflowTaskStepHistories =>
        WorkflowTaskStepHistories.AsNoTracking();

    /// <inheritdoc />
    IQueryable<CnasBranch> IReadOnlyCnasDbContext.CnasBranches => CnasBranches.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PersonalAccount> IReadOnlyCnasDbContext.PersonalAccounts => PersonalAccounts.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PersonalAccountEntry> IReadOnlyCnasDbContext.PersonalAccountEntries => PersonalAccountEntries.AsNoTracking();

    /// <inheritdoc />
    IQueryable<BenefitPayment> IReadOnlyCnasDbContext.BenefitPayments => BenefitPayments.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PayerAddress> IReadOnlyCnasDbContext.PayerAddresses => PayerAddresses.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PayerContact> IReadOnlyCnasDbContext.PayerContacts => PayerContacts.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PayerActivityCAEM> IReadOnlyCnasDbContext.PayerActivities => PayerActivities.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PayerHistory> IReadOnlyCnasDbContext.PayerHistory => PayerHistory.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PayerBankAccount> IReadOnlyCnasDbContext.PayerBankAccounts => PayerBankAccounts.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PayerSecondaryContact> IReadOnlyCnasDbContext.PayerSecondaryContacts => PayerSecondaryContacts.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ContributorAddress> IReadOnlyCnasDbContext.ContributorAddresses => ContributorAddresses.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ContributorContact> IReadOnlyCnasDbContext.ContributorContacts => ContributorContacts.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ContributorActivityPeriod> IReadOnlyCnasDbContext.ContributorActivityPeriods =>
        ContributorActivityPeriods.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ContributorCivilStatus> IReadOnlyCnasDbContext.ContributorCivilStatuses =>
        ContributorCivilStatuses.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ContributorSocialInsuranceContract> IReadOnlyCnasDbContext.ContributorSocialInsuranceContracts =>
        ContributorSocialInsuranceContracts.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ContributorPre1999PeriodCarnetMunca> IReadOnlyCnasDbContext.ContributorPre1999PeriodsCarnetMunca =>
        ContributorPre1999PeriodsCarnetMunca.AsNoTracking();

    /// <inheritdoc />
    IQueryable<LaborBooklet> IReadOnlyCnasDbContext.LaborBooklets => LaborBooklets.AsNoTracking();

    /// <inheritdoc />
    IQueryable<InsuredPersonPre1999Period> IReadOnlyCnasDbContext.InsuredPersonPre1999Periods =>
        InsuredPersonPre1999Periods.AsNoTracking();

    /// <inheritdoc />
    IQueryable<Pre1999StagiuRecord> IReadOnlyCnasDbContext.Pre1999StagiuRecords =>
        Pre1999StagiuRecords.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ProfileUpdateRequest> IReadOnlyCnasDbContext.ProfileUpdateRequests => ProfileUpdateRequests.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ProfileRefreshRun> IReadOnlyCnasDbContext.ProfileRefreshRuns => ProfileRefreshRuns.AsNoTracking();

    /// <inheritdoc />
    IQueryable<KpiSnapshot> IReadOnlyCnasDbContext.KpiSnapshots => KpiSnapshots.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ContributorPeriodProjection> IReadOnlyCnasDbContext.ContributorPeriodProjections =>
        ContributorPeriodProjections.AsNoTracking();

    /// <inheritdoc />
    IQueryable<WorkflowGraphNode> IReadOnlyCnasDbContext.WorkflowGraphNodes => WorkflowGraphNodes.AsNoTracking();

    /// <inheritdoc />
    IQueryable<WorkflowGraphEdge> IReadOnlyCnasDbContext.WorkflowGraphEdges => WorkflowGraphEdges.AsNoTracking();

    /// <inheritdoc />
    IQueryable<TranslationKey> IReadOnlyCnasDbContext.TranslationKeys => TranslationKeys.AsNoTracking();

    /// <inheritdoc />
    IQueryable<TranslationValue> IReadOnlyCnasDbContext.TranslationValues => TranslationValues.AsNoTracking();

    /// <inheritdoc />
    IQueryable<HelpTopic> IReadOnlyCnasDbContext.HelpTopics => HelpTopics.AsNoTracking();

    /// <inheritdoc />
    IQueryable<HelpTopicTranslation> IReadOnlyCnasDbContext.HelpTopicTranslations =>
        HelpTopicTranslations.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ReportTemplate> IReadOnlyCnasDbContext.ReportTemplates => ReportTemplates.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ReportRun> IReadOnlyCnasDbContext.ReportRuns => ReportRuns.AsNoTracking();

    /// <inheritdoc />
    IQueryable<AttachmentRecord> IReadOnlyCnasDbContext.AttachmentRecords => AttachmentRecords.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ReportJob> IReadOnlyCnasDbContext.ReportJobs => ReportJobs.AsNoTracking();

    /// <inheritdoc />
    IQueryable<Declaration> IReadOnlyCnasDbContext.Declarations => Declarations.AsNoTracking();

    /// <inheritdoc />
    IQueryable<MonthlyContributionCalculation> IReadOnlyCnasDbContext.MonthlyContributionCalculations =>
        MonthlyContributionCalculations.AsNoTracking();

    /// <inheritdoc />
    IQueryable<LatePaymentPenalty> IReadOnlyCnasDbContext.LatePaymentPenalties =>
        LatePaymentPenalties.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ManagementPeriodClose> IReadOnlyCnasDbContext.ManagementPeriodCloses =>
        ManagementPeriodCloses.AsNoTracking();

    /// <inheritdoc />
    IQueryable<Rev5Declaration> IReadOnlyCnasDbContext.Rev5Declarations =>
        Rev5Declarations.AsNoTracking();

    /// <inheritdoc />
    IQueryable<Rev5DeclarationRow> IReadOnlyCnasDbContext.Rev5DeclarationRows =>
        Rev5DeclarationRows.AsNoTracking();

    /// <inheritdoc />
    IQueryable<InsuredPersonContributionAdjustment> IReadOnlyCnasDbContext.InsuredPersonContributionAdjustments =>
        InsuredPersonContributionAdjustments.AsNoTracking();

    /// <inheritdoc />
    IQueryable<TreasuryPaymentReceipt> IReadOnlyCnasDbContext.TreasuryPaymentReceipts =>
        TreasuryPaymentReceipts.AsNoTracking();

    /// <inheritdoc />
    IQueryable<Claim> IReadOnlyCnasDbContext.Claims =>
        Claims.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ClaimPayment> IReadOnlyCnasDbContext.ClaimPayments =>
        ClaimPayments.AsNoTracking();

    /// <inheritdoc />
    IQueryable<BassRefund> IReadOnlyCnasDbContext.BassRefunds =>
        BassRefunds.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PaymentCorrection> IReadOnlyCnasDbContext.PaymentCorrections =>
        PaymentCorrections.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PenaltyRepaymentPlan> IReadOnlyCnasDbContext.PenaltyRepaymentPlans =>
        PenaltyRepaymentPlans.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PenaltyRepaymentInstallment> IReadOnlyCnasDbContext.PenaltyRepaymentInstallments =>
        PenaltyRepaymentInstallments.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ExecutoryDocument> IReadOnlyCnasDbContext.ExecutoryDocuments =>
        ExecutoryDocuments.AsNoTracking();

    /// <inheritdoc />
    IQueryable<UserGroup> IReadOnlyCnasDbContext.UserGroups => UserGroups.AsNoTracking();

    /// <inheritdoc />
    IQueryable<UserGroupParent> IReadOnlyCnasDbContext.UserGroupParents => UserGroupParents.AsNoTracking();

    /// <inheritdoc />
    IQueryable<UserGroupMembership> IReadOnlyCnasDbContext.UserGroupMemberships => UserGroupMemberships.AsNoTracking();

    /// <inheritdoc />
    IQueryable<IntegrityCheckRun> IReadOnlyCnasDbContext.IntegrityCheckRuns => IntegrityCheckRuns.AsNoTracking();

    /// <inheritdoc />
    IQueryable<IntegrityCheckFinding> IReadOnlyCnasDbContext.IntegrityCheckFindings => IntegrityCheckFindings.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ReportDistributionRule> IReadOnlyCnasDbContext.ReportDistributionRules =>
        ReportDistributionRules.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ReportDistributionDispatch> IReadOnlyCnasDbContext.ReportDistributionDispatches =>
        ReportDistributionDispatches.AsNoTracking();

    /// <inheritdoc />
    IQueryable<LegalChangeEvent> IReadOnlyCnasDbContext.LegalChangeEvents =>
        LegalChangeEvents.AsNoTracking();

    /// <inheritdoc />
    IQueryable<RecalculationRun> IReadOnlyCnasDbContext.RecalculationRuns =>
        RecalculationRuns.AsNoTracking();

    /// <inheritdoc />
    IQueryable<RecalculationDecisionResult> IReadOnlyCnasDbContext.RecalculationDecisionResults =>
        RecalculationDecisionResults.AsNoTracking();

    /// <inheritdoc />
    IQueryable<OfflineBatchSubmission> IReadOnlyCnasDbContext.OfflineBatchSubmissions =>
        OfflineBatchSubmissions.AsNoTracking();

    /// <inheritdoc />
    IQueryable<OfflineBatchRow> IReadOnlyCnasDbContext.OfflineBatchRows =>
        OfflineBatchRows.AsNoTracking();

    /// <inheritdoc />
    IQueryable<TreasuryFeedImport> IReadOnlyCnasDbContext.TreasuryFeedImports =>
        TreasuryFeedImports.AsNoTracking();

    /// <inheritdoc />
    IQueryable<TreasuryFeedImportRow> IReadOnlyCnasDbContext.TreasuryFeedImportRows =>
        TreasuryFeedImportRows.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ExternalSourceIngestionRun> IReadOnlyCnasDbContext.ExternalSourceIngestionRuns =>
        ExternalSourceIngestionRuns.AsNoTracking();

    /// <inheritdoc />
    IQueryable<CapitalisedPaymentRequest> IReadOnlyCnasDbContext.CapitalisedPaymentRequests =>
        CapitalisedPaymentRequests.AsNoTracking();

    /// <inheritdoc />
    IQueryable<CapitalisedPaymentDecision> IReadOnlyCnasDbContext.CapitalisedPaymentDecisions =>
        CapitalisedPaymentDecisions.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ClassificationCatalogSnapshot> IReadOnlyCnasDbContext.ClassificationCatalogSnapshots =>
        ClassificationCatalogSnapshots.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ClassificationCatalogEntry> IReadOnlyCnasDbContext.ClassificationCatalogEntries =>
        ClassificationCatalogEntries.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ClassificationDriftFinding> IReadOnlyCnasDbContext.ClassificationDriftFindings =>
        ClassificationDriftFindings.AsNoTracking();

    /// <inheritdoc />
    IQueryable<AthletePensionAward> IReadOnlyCnasDbContext.AthletePensionAwards =>
        AthletePensionAwards.AsNoTracking();

    /// <inheritdoc />
    IQueryable<AthleteCareerRecord> IReadOnlyCnasDbContext.AthleteCareerRecords =>
        AthleteCareerRecords.AsNoTracking();

    /// <inheritdoc />
    IQueryable<IntlAgreementReviewCase> IReadOnlyCnasDbContext.IntlAgreementReviewCases =>
        IntlAgreementReviewCases.AsNoTracking();

    /// <inheritdoc />
    IQueryable<IntlAgreementReviewStep> IReadOnlyCnasDbContext.IntlAgreementReviewSteps =>
        IntlAgreementReviewSteps.AsNoTracking();

    /// <inheritdoc />
    IQueryable<AbacRuleSet> IReadOnlyCnasDbContext.AbacRuleSets =>
        AbacRuleSets.AsNoTracking();

    /// <inheritdoc />
    IQueryable<AbacRule> IReadOnlyCnasDbContext.AbacRules =>
        AbacRules.AsNoTracking();

    /// <inheritdoc />
    IQueryable<MigrationPlan> IReadOnlyCnasDbContext.MigrationPlans =>
        MigrationPlans.AsNoTracking();

    /// <inheritdoc />
    IQueryable<MigrationRun> IReadOnlyCnasDbContext.MigrationRuns =>
        MigrationRuns.AsNoTracking();

    /// <inheritdoc />
    IQueryable<MigrationBatch> IReadOnlyCnasDbContext.MigrationBatches =>
        MigrationBatches.AsNoTracking();

    /// <inheritdoc />
    IQueryable<MigrationFinding> IReadOnlyCnasDbContext.MigrationFindings =>
        MigrationFindings.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ReconciliationReport> IReadOnlyCnasDbContext.ReconciliationReports =>
        ReconciliationReports.AsNoTracking();

    /// <inheritdoc />
    IQueryable<MigrationStagingRow> IReadOnlyCnasDbContext.MigrationStagingRows =>
        MigrationStagingRows.AsNoTracking();

    /// <inheritdoc />
    IQueryable<BackupPolicy> IReadOnlyCnasDbContext.BackupPolicies =>
        BackupPolicies.AsNoTracking();

    /// <inheritdoc />
    IQueryable<BackupRun> IReadOnlyCnasDbContext.BackupRuns =>
        BackupRuns.AsNoTracking();

    /// <inheritdoc />
    IQueryable<BackupIntegrityCheck> IReadOnlyCnasDbContext.BackupIntegrityChecks =>
        BackupIntegrityChecks.AsNoTracking();

    /// <inheritdoc />
    IQueryable<SupportTicketCategory> IReadOnlyCnasDbContext.SupportTicketCategories =>
        SupportTicketCategories.AsNoTracking();

    /// <inheritdoc />
    IQueryable<SupportTicket> IReadOnlyCnasDbContext.SupportTickets =>
        SupportTickets.AsNoTracking();

    /// <inheritdoc />
    IQueryable<SupportTicketComment> IReadOnlyCnasDbContext.SupportTicketComments =>
        SupportTicketComments.AsNoTracking();

    /// <inheritdoc />
    IQueryable<SupportTicketSlaEvent> IReadOnlyCnasDbContext.SupportTicketSlaEvents =>
        SupportTicketSlaEvents.AsNoTracking();

    /// <inheritdoc />
    IQueryable<BusinessHoursPolicy> IReadOnlyCnasDbContext.BusinessHoursPolicies =>
        BusinessHoursPolicies.AsNoTracking();

    /// <inheritdoc />
    IQueryable<MaintenanceWindow> IReadOnlyCnasDbContext.MaintenanceWindows =>
        MaintenanceWindows.AsNoTracking();

    /// <inheritdoc />
    IQueryable<SystemUpdateSchedule> IReadOnlyCnasDbContext.SystemUpdateSchedules =>
        SystemUpdateSchedules.AsNoTracking();

    /// <inheritdoc />
    IQueryable<SystemUpdateEvent> IReadOnlyCnasDbContext.SystemUpdateEvents =>
        SystemUpdateEvents.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ChangeRequest> IReadOnlyCnasDbContext.ChangeRequests =>
        ChangeRequests.AsNoTracking();

    /// <inheritdoc />
    IQueryable<QualityRisk> IReadOnlyCnasDbContext.QualityRisks =>
        QualityRisks.AsNoTracking();

    /// <inheritdoc />
    IQueryable<QualityRiskPreventiveAction> IReadOnlyCnasDbContext.QualityRiskPreventiveActions =>
        QualityRiskPreventiveActions.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ProcessedIntegrationEvent> IReadOnlyCnasDbContext.ProcessedIntegrationEvents =>
        ProcessedIntegrationEvents.AsNoTracking();

    /// <inheritdoc />
    IQueryable<AuditCategory> IReadOnlyCnasDbContext.AuditCategories =>
        AuditCategories.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ContributorSourceChangeHistory> IReadOnlyCnasDbContext.ContributorSourceChangeHistory =>
        ContributorSourceChangeHistory.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ApplicationAttachment> IReadOnlyCnasDbContext.ApplicationAttachments =>
        ApplicationAttachments.AsNoTracking();

    /// <inheritdoc />
    IQueryable<EntityHistoryRow> IReadOnlyCnasDbContext.EntityHistoryRows =>
        EntityHistoryRows.AsNoTracking();

    /// <inheritdoc />
    IQueryable<ExaminerAssignmentCursor> IReadOnlyCnasDbContext.ExaminerAssignmentCursors =>
        ExaminerAssignmentCursors.AsNoTracking();

    /// <inheritdoc />
    IQueryable<JobScheduleOverride> IReadOnlyCnasDbContext.JobScheduleOverrides =>
        JobScheduleOverrides.AsNoTracking();

    /// <inheritdoc />
    IQueryable<WorkflowAutoCreationRule> IReadOnlyCnasDbContext.WorkflowAutoCreationRules =>
        WorkflowAutoCreationRules.AsNoTracking();

    /// <inheritdoc />
    IQueryable<GranularPermissionAssignment> IReadOnlyCnasDbContext.GranularPermissionAssignments =>
        GranularPermissionAssignments.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PaperFulfilmentRecord> IReadOnlyCnasDbContext.PaperFulfilmentRecords =>
        PaperFulfilmentRecords.AsNoTracking();

    /// <inheritdoc />
    IQueryable<DecisionSupersession> IReadOnlyCnasDbContext.DecisionSupersessions =>
        DecisionSupersessions.AsNoTracking();

    /// <inheritdoc />
    IQueryable<PaymentSuspensionRecord> IReadOnlyCnasDbContext.PaymentSuspensionRecords =>
        PaymentSuspensionRecords.AsNoTracking();

    /// <inheritdoc />
    IQueryable<MNotifyTemplate> IReadOnlyCnasDbContext.MNotifyTemplates =>
        MNotifyTemplates.AsNoTracking();

    /// <inheritdoc />
    IQueryable<MLogCategoryConfig> IReadOnlyCnasDbContext.MLogCategoryConfigs =>
        MLogCategoryConfigs.AsNoTracking();

    /// <inheritdoc />
    IQueryable<DelegationGrant> IReadOnlyCnasDbContext.DelegationGrants =>
        DelegationGrants.AsNoTracking();

    /// <inheritdoc />
    IQueryable<OfflineBatchJob> IReadOnlyCnasDbContext.OfflineBatchJobs =>
        OfflineBatchJobs.AsNoTracking();

    /// <inheritdoc />
    IQueryable<EntityAttributeValue> IReadOnlyCnasDbContext.EntityAttributeValues =>
        EntityAttributeValues.AsNoTracking();

    /// <inheritdoc />
    IQueryable<InsolvencyCase> IReadOnlyCnasDbContext.InsolvencyCases =>
        InsolvencyCases.AsNoTracking();

    /// <inheritdoc />
    IQueryable<InsolvencyClaim> IReadOnlyCnasDbContext.InsolvencyClaims =>
        InsolvencyClaims.AsNoTracking();

    /// <inheritdoc />
    IQueryable<InsolvencyPayment> IReadOnlyCnasDbContext.InsolvencyPayments =>
        InsolvencyPayments.AsNoTracking();

    /// <inheritdoc />
    IQueryable<VoucherQuota> IReadOnlyCnasDbContext.VoucherQuotas =>
        VoucherQuotas.AsNoTracking();

    /// <inheritdoc />
    IQueryable<RecurrentPaymentSchedule> IReadOnlyCnasDbContext.RecurrentPaymentSchedules =>
        RecurrentPaymentSchedules.AsNoTracking();

    /// <inheritdoc />
    /// <remarks>
    /// Registers <see cref="CnasModelCacheKeyFactory"/> so that the EF compiled-model
    /// cache distinguishes encryptor-aware contexts from encryptor-less ones —
    /// without this override, the first context to run wins the cache and the
    /// second variant inherits the wrong converter wiring. See the factory's
    /// remarks for the failure mode.
    /// </remarks>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, CnasModelCacheKeyFactory>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("cnas");

        // R0162 / CF 03.13 — map CnasDbFunctions.Unaccent to public.unaccent(text).
        // The Postgres unaccent extension is installed by the EnableUnaccentExtension
        // migration; the InMemory test provider never executes the function (search
        // sites fall through to DiacriticFolding.Fold on the in-process branch).
        // IsBuiltIn(false) tells EF Core to qualify the function name with its schema
        // when it has one and to treat it as a user-defined function for translation.
        modelBuilder.HasDbFunction(typeof(CnasDbFunctions).GetMethod(nameof(CnasDbFunctions.Unaccent))!)
            .HasName("unaccent")
            .HasSchema("public")
            .IsBuiltIn(false);

        // Pick up all IEntityTypeConfiguration<T> implementations defined in this assembly.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CnasDbContext).Assembly);

        // Apply field-encryption converters to columns annotated for confidentiality
        // (CLAUDE.md §5.7). The converter is wired only when an IFieldEncryptor was
        // supplied — tests using the single-arg constructor see plaintext at rest.
        if (_fieldEncryptor is not null)
        {
            // The non-generic HasConversion overload accepts a ValueConverter
            // whose model-side type is non-nullable string even though the
            // entity property is string? — EF Core wraps null short-circuiting
            // around the converter automatically, so nulls remain NULL at rest
            // (no sentinel ciphertext) and the converter sees only non-null
            // values. See EncryptedStringConverter remarks.
            var encryptedStringConverter = new EncryptedStringConverter(_fieldEncryptor);
            modelBuilder.Entity<Solicitant>()
                .Property(s => s.BankIban)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);

            // National identifier columns — extended encryption pass (TOR SEC 035 follow-up).
            // Each plaintext column is encrypted at rest; equality lookups against these
            // columns are served by the *Hash shadow column maintained by the application
            // layer via IDeterministicHasher. See the entity properties' XML doc for the
            // synchronization contract — never write the plaintext without also writing
            // the corresponding *Hash, or the row is unreachable through the normal lookup
            // paths and the unique-index constraint will not catch duplicates.
            modelBuilder.Entity<Solicitant>()
                .Property(s => s.NationalId)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);
            modelBuilder.Entity<Contributor>()
                .Property(c => c.Idno)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);
            modelBuilder.Entity<InsuredPerson>()
                .Property(p => p.Idnp)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);
            modelBuilder.Entity<UserProfile>()
                .Property(u => u.NationalId)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);

            // UserProfile.PhoneE164 — citizen-supplied contact phone (PII per TOR SEC 035).
            // No hash shadow column: phone is a display field, never a search key, so the
            // loss of equality lookups on the encrypted column is acceptable. The service
            // layer normalises and validates via Cnas.Ps.Core.ValueObjects.PhoneE164 before
            // assigning, so the ciphertext at rest always wraps a canonical-form value.
            modelBuilder.Entity<UserProfile>()
                .Property(u => u.PhoneE164)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);

            // R0803 — PayerBankAccount.Iban — bank IBAN at rest is encrypted with the
            // same envelope as Solicitant.BankIban. Equality lookups go through the
            // PayerBankAccount.IbanHash shadow column (deterministic HMAC populated by
            // PayerLinkedEntitiesService via IDeterministicHasher.ComputeHash on the
            // canonicalised IBAN — uppercase, no spaces).
            modelBuilder.Entity<PayerBankAccount>()
                .Property(b => b.Iban)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);

            // R1600 / R1406 — ExecutoryDocument PII / financial columns.
            // DebtorIdnp (13-digit IDNP) and CreditorAccountIban (MD IBAN) are
            // encrypted at rest. Equality lookups go through the shadow hash
            // columns DebtorIdnpHash / CreditorAccountIbanHash maintained by
            // the application layer via IDeterministicHasher.
            modelBuilder.Entity<ExecutoryDocument>()
                .Property(e => e.DebtorIdnp)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);
            modelBuilder.Entity<ExecutoryDocument>()
                .Property(e => e.CreditorAccountIban)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);

            // R1906 / TOR Annex 6 — distribution-rule + dispatch recipient codes are
            // encrypted at rest. For the EmailAddress kind the value is PII; for
            // the other kinds it is a small opaque code that encrypts cheaply.
            // Equality lookups against the rule are served by the
            // RecipientCodeHash shadow column populated only for emails.
            modelBuilder.Entity<ReportDistributionRule>()
                .Property(r => r.RecipientCode)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);
            modelBuilder.Entity<ReportDistributionDispatch>()
                .Property(d => d.RecipientCode)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);

            // R1202 / TOR §3.4-C — CapitalisedPaymentRequest PII columns.
            // BeneficiaryIdnp (13-digit IDNP) and LiquidatedDebtorIdno (13-digit
            // IDNO) are encrypted at rest. Equality lookups go through the
            // shadow hash columns BeneficiaryIdnpHash / LiquidatedDebtorIdnoHash
            // maintained by the application layer via IDeterministicHasher.
            modelBuilder.Entity<CapitalisedPaymentRequest>()
                .Property(e => e.BeneficiaryIdnp)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);
            modelBuilder.Entity<CapitalisedPaymentRequest>()
                .Property(e => e.LiquidatedDebtorIdno)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);

            // R1403 / TOR §3.6-D — AthletePensionAward.BeneficiaryIdnp
            // (13-digit IDNP) is encrypted at rest. Equality lookups go
            // through the shadow hash column BeneficiaryIdnpHash maintained
            // by the application layer via IDeterministicHasher.
            modelBuilder.Entity<AthletePensionAward>()
                .Property(e => e.BeneficiaryIdnp)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);

            // R1201 / R1402 / TOR §3.4-B / §3.6-C —
            // IntlAgreementReviewCase.BeneficiaryIdnp (13-digit IDNP) is
            // encrypted at rest. Equality lookups go through the shadow
            // hash column BeneficiaryIdnpHash maintained by the application
            // layer via IDeterministicHasher.
            modelBuilder.Entity<IntlAgreementReviewCase>()
                .Property(e => e.BeneficiaryIdnp)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);

            // R0805 / Annex 1 §8.1.1.6 — Contributor sub-table PII columns.
            // ContributorAddress.BuildingNumber + Apartment carry the street-address
            // fragments mandated by the Annex 1 §8.1.1.6 schema; ContributorContact.Value
            // carries the kind-typed phone/email/fax payload. All three are PII per
            // CLAUDE.md §5.7 and are encrypted at rest. Equality lookups are not
            // expected against these columns (registry browsing keys on the parent
            // Contributor row), so no shadow hash columns are introduced.
            modelBuilder.Entity<ContributorAddress>()
                .Property(e => e.BuildingNumber)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);
            modelBuilder.Entity<ContributorAddress>()
                .Property(e => e.Apartment)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);
            modelBuilder.Entity<ContributorContact>()
                .Property(e => e.Value)
                .HasConversion((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)encryptedStringConverter);
        }

        // NOTE: Soft-delete filtering is the SERVICE LAYER's responsibility — each
        // query that wants to hide deactivated rows MUST include
        // `Where(e => e.IsActive)` explicitly. There is intentionally NO global
        // HasQueryFilter wired here because several background paths (audit replay,
        // forensics, integrity-check sweep, GDPR right-to-erasure surfaces) read
        // soft-deleted rows on purpose; a blanket filter would silently hide them
        // and require pervasive `IgnoreQueryFilters()` annotations across the
        // codebase. Deferring the global filter to a dedicated migration phase
        // (tracked in TODO.md under R0026 / R0623) keeps the contract auditable.
        //
        // The block below is the per-entity Xmin (PG row version) wiring — every
        // AuditableEntity row exposes the system column `xmin` as a concurrency
        // token so optimistic-concurrency conflicts surface at SaveChanges instead
        // of silently lost-updating.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(AuditableEntity).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType).Property("Xmin")
                    .HasColumnName("xmin")
                    .HasColumnType("xid")
                    .ValueGeneratedOnAddOrUpdate()
                    .IsConcurrencyToken();
            }
        }
    }
}
