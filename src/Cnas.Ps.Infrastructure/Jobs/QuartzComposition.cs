using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// Registers Quartz.NET as the background-job framework (MR 010-012).
/// Schedules are stored in memory for dev; production wires the AdoJobStore against
/// Postgres so schedules survive restarts and are coordinated across instances.
/// </summary>
public static class QuartzComposition
{
    /// <summary>Adds Quartz services and registers the built-in CNAS jobs.</summary>
    /// <remarks>
    /// Registered jobs and cadence:
    /// <list type="bullet">
    ///   <item><description><see cref="DossierSlaMonitorJob"/> — every 15 minutes.</description></item>
    ///   <item><description><see cref="MPayDispatcherJob"/> — every 5 minutes.</description></item>
    ///   <item><description><see cref="MConnectSyncJob"/> — daily at 03:00 UTC.</description></item>
    ///   <item><description><see cref="AuditArchiveReplayJob"/> — every 5 minutes (R0188).</description></item>
    ///   <item><description><see cref="MissingDocsSlaJob"/> — every hour, on the hour (R0934).</description></item>
    ///   <item><description><see cref="UnclaimedTaskEscalationJob"/> — every hour, on the hour (R0202 / CF 20.05).</description></item>
    ///   <item><description><see cref="SiemForwarderJob"/> — every minute (R0190 / SEC 049).</description></item>
    /// </list>
    /// <para>
    /// Also wires the <see cref="FailedJobListener"/> as a scheduler-wide
    /// <see cref="IJobListener"/> so every job failure becomes a row in the
    /// <c>FailedJobs</c> dead-letter queue (CLAUDE.md §6.2). The listener consumes
    /// <see cref="IFailedJobStore"/> for persistence and replay scheduling.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCnasJobs(this IServiceCollection services)
    {
        // DLQ store is scoped (depends on EF Core's per-request DbContext). The listener
        // is registered as transient — it is instantiated by Quartz's listener manager
        // and resolved per-listener-creation, not per scheduler fire.
        services.AddScoped<IFailedJobStore, FailedJobStore>();
        services.AddTransient<FailedJobListener>();

        services.AddQuartz(q =>
        {
            q.SchedulerName = "Cnas.Ps";

            // Dossier SLA monitor: every 15 minutes flags overdue WorkflowTasks and posts a
            // notification to the assignee. Idempotent — already-overdue rows are skipped.
            var slaJobKey = new JobKey("dossier-sla-monitor");
            q.AddJob<DossierSlaMonitorJob>(opts => opts.WithIdentity(slaJobKey));
            q.AddTrigger(opts => opts
                .ForJob(slaJobKey)
                .WithIdentity("dossier-sla-monitor-trigger")
                .WithSimpleSchedule(s => s.WithIntervalInMinutes(15).RepeatForever()));

            // MPay dispatcher: every 5 minutes drains the approved-but-not-yet-paid queue
            // and sends each application through MPay. Critical-severity audit on each
            // dispatch (PAYMENT.DISPATCHED) ensures MLog mirroring per SEC 056.
            var mpayJobKey = new JobKey("mpay-dispatcher");
            q.AddJob<MPayDispatcherJob>(opts => opts.WithIdentity(mpayJobKey));
            q.AddTrigger(opts => opts
                .ForJob(mpayJobKey)
                .WithIdentity("mpay-dispatcher-trigger")
                .WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever()));

            // MConnect refresh: daily at 03:00 UTC pulls stale InsuredPerson rows from RSP.
            // Stale = LastRspSyncUtc null or older than 30 days. Failures are tolerated.
            var mconnectJobKey = new JobKey("mconnect-sync");
            q.AddJob<MConnectSyncJob>(opts => opts.WithIdentity(mconnectJobKey));
            q.AddTrigger(opts => opts
                .ForJob(mconnectJobKey)
                .WithIdentity("mconnect-sync-trigger")
                .WithCronSchedule("0 0 3 * * ?"));

            // R0188 — Audit archive replay: every 5 minutes scans IAuditArchive for
            // batches AuditDrainer spilled after a primary-flush failure and re-attempts
            // the DB + MLog write. Hard-coded to "0 */5 * ? * *" to mirror
            // AuditArchiveOptions.ReplayCron — the options surface is documented as the
            // future seam if operators ever need to change the cadence per environment.
            var auditReplayJobKey = new JobKey("audit-archive-replay");
            q.AddJob<AuditArchiveReplayJob>(opts => opts.WithIdentity(auditReplayJobKey));
            q.AddTrigger(opts => opts
                .ForJob(auditReplayJobKey)
                .WithIdentity("audit-archive-replay-trigger")
                .WithCronSchedule("0 0/5 * ? * *"));

            // R0934 — Missing-documents SLA: every hour, on the hour, scan for applications
            // parked in RejectedIncomplete for >30 days, flip them to Rejected, audit, and
            // notify the citizen. The job is purely declarative (idempotent on a status
            // filter) so the [DisallowConcurrentExecution] guard is belt-and-braces — even
            // an overlapping fire would only re-query an empty result set.
            var missingDocsJobKey = new JobKey(MissingDocsSlaJob.JobIdentity);
            q.AddJob<MissingDocsSlaJob>(opts => opts.WithIdentity(missingDocsJobKey));
            q.AddTrigger(opts => opts
                .ForJob(missingDocsJobKey)
                .WithIdentity(MissingDocsSlaJob.TriggerIdentity)
                .WithCronSchedule(MissingDocsSlaJob.Cron));

            // R0202 / CF 20.05 — Unclaimed-task escalation: every hour, on the hour, scan
            // for WorkflowTask rows sitting in a group inbox past the configured window
            // (default 4h). The job is purely declarative (idempotent on the
            // UnclaimedSinceUtc stamp it clears) so the [DisallowConcurrentExecution] guard
            // is belt-and-braces. The cron is hard-coded here for parity with the
            // missing-docs sweeper; TODO[r0202-cron]: pipe UnclaimedTaskEscalationOptions.Cron
            // through QuartzComposition once operators need per-environment cadence.
            var unclaimedJobKey = new JobKey(UnclaimedTaskEscalationJob.JobIdentity);
            q.AddJob<UnclaimedTaskEscalationJob>(opts => opts.WithIdentity(unclaimedJobKey));
            q.AddTrigger(opts => opts
                .ForJob(unclaimedJobKey)
                .WithIdentity(UnclaimedTaskEscalationJob.TriggerIdentity)
                .WithCronSchedule("0 0 0/1 * * ?"));

            // R0190 / SEC 049 — SIEM CEF / syslog forwarder. Every minute on the second
            // boundary the job polls newly persisted AuditLog rows past the checkpoint
            // stored on SiemForwarderState, formats them as ArcSight CEF, and writes them
            // to the configured syslog endpoint. The job no-ops when
            // SiemExporterOptions.Enabled is false (the default) so the chart ships safely
            // without forcing operators to configure a SIEM. Cron is hard-coded here for
            // parity with the rest of the job set; TODO[r0190-cron]: pipe
            // SiemExporterOptions.Cron through QuartzComposition once operators need
            // per-environment cadence.
            var siemForwarderJobKey = new JobKey(SiemForwarderJob.JobIdentity);
            q.AddJob<SiemForwarderJob>(opts => opts.WithIdentity(siemForwarderJobKey));
            q.AddTrigger(opts => opts
                .ForJob(siemForwarderJobKey)
                .WithIdentity(SiemForwarderJob.TriggerIdentity)
                .WithCronSchedule("0 0/1 * * * ?"));

            // R0166 / CF 03.11 — Bulk-selection cleanup. Daily at 03:15 UTC the job sweeps
            // BulkSelection rows whose ExpiresAtUtc is older than the configured grace window
            // (default 7 days) and hard-deletes them. Cron hard-coded here for parity with the
            // rest of the job set.
            var bulkSelectionCleanupJobKey = new JobKey(BulkSelectionCleanupJob.JobIdentity);
            q.AddJob<BulkSelectionCleanupJob>(opts => opts.WithIdentity(bulkSelectionCleanupJobKey));
            q.AddTrigger(opts => opts
                .ForJob(bulkSelectionCleanupJobKey)
                .WithIdentity(BulkSelectionCleanupJob.TriggerIdentity)
                .WithCronSchedule(BulkSelectionCleanupJob.Cron));

            // R0189 / SEC 048 — Security-alert evaluator. Every minute the job scans
            // newly persisted AuditLog rows past the SecurityAlertEvaluatorState
            // checkpoint, scores them against the active SecurityAlertRule set, and
            // fires per-rule alerts (in-app notification + audit row +
            // cnas.security_alert.fired counter) when a rule's rolling-window
            // threshold is met and the per-rule cooldown has elapsed. The job no-ops
            // when SecurityAlertOptions.Enabled is false; default is true because
            // the migration seeds four common rules. Cron hard-coded here for parity
            // with the rest of the job set; TODO[r0189-cron]: pipe
            // SecurityAlertOptions.Cron through QuartzComposition once operators
            // need per-environment cadence.
            var securityAlertJobKey = new JobKey(SecurityAlertEvaluatorJob.JobIdentity);
            q.AddJob<SecurityAlertEvaluatorJob>(opts => opts.WithIdentity(securityAlertJobKey));
            q.AddTrigger(opts => opts
                .ForJob(securityAlertJobKey)
                .WithIdentity(SecurityAlertEvaluatorJob.TriggerIdentity)
                .WithCronSchedule("0 0/1 * * * ?"));

            // R0127 / CF 16.11 — User-absence lifecycle job. Every 5 minutes the job
            // activates Planned absences whose StartDateUtc has been reached (routing
            // open tasks to the delegate), and completes Active absences past
            // EndDateUtc (reverting still-open tasks to their original assignee). The
            // mutations are idempotent (the row predicate excludes already-flipped
            // rows) so the [DisallowConcurrentExecution] guard is belt-and-braces.
            var userAbsenceJobKey = new JobKey(UserAbsenceLifecycleJob.JobIdentity);
            q.AddJob<UserAbsenceLifecycleJob>(opts => opts.WithIdentity(userAbsenceJobKey));
            q.AddTrigger(opts => opts
                .ForJob(userAbsenceJobKey)
                .WithIdentity(UserAbsenceLifecycleJob.TriggerIdentity)
                .WithCronSchedule(UserAbsenceLifecycleJob.Cron));

            // R2267 / SEC 020 — Session auto-lock sweep. Every 5 minutes the job flips
            // live, non-locked sessions whose LastActivityUtc is older than
            // SessionLimitOptions.IdleLockMinutes (default 15) to IsLocked=true. The
            // predicate excludes already-locked + already-terminated rows so a second
            // fire on the same data set is a no-op (the [DisallowConcurrentExecution]
            // guard is belt-and-braces).
            var sessionAutoLockJobKey = new JobKey(SessionAutoLockJob.JobIdentity);
            q.AddJob<SessionAutoLockJob>(opts => opts.WithIdentity(sessionAutoLockJobKey));
            q.AddTrigger(opts => opts
                .ForJob(sessionAutoLockJobKey)
                .WithIdentity(SessionAutoLockJob.TriggerIdentity)
                .WithCronSchedule(SessionAutoLockJob.Cron));

            // R0201 / CF 20.02 — KPI pre-aggregation. Daily at 02:00 UTC the job
            // invokes IKpiSnapshotService.RunForDateAsync(today - 1) to produce the
            // operator-dashboard snapshot. Idempotent — the service upserts on the
            // natural key, so a re-fire (or a manual recompute via the admin endpoint)
            // overwrites the previous values in place rather than appending duplicates.
            var kpiSnapshotJobKey = new JobKey(KpiSnapshotJob.JobIdentity);
            q.AddJob<KpiSnapshotJob>(opts => opts.WithIdentity(kpiSnapshotJobKey));
            q.AddTrigger(opts => opts
                .ForJob(kpiSnapshotJobKey)
                .WithIdentity(KpiSnapshotJob.TriggerIdentity)
                .WithCronSchedule(KpiSnapshotJob.Cron));

            // R0153 / CF 19.05 — Contributor period-aware projection. Daily at
            // 03:00 UTC (after the KPI snapshot at 02:00) the job invokes
            // IContributorPeriodProjectionService.RebuildAllAsync to refresh the
            // pre-aggregated period-projection table. Idempotent — DELETE-then-INSERT
            // per contributor, so a re-fire (or manual recompute via the admin
            // endpoint) overwrites the previous values in place.
            var contributorProjectionJobKey = new JobKey(ContributorPeriodProjectionJob.JobIdentity);
            q.AddJob<ContributorPeriodProjectionJob>(opts => opts.WithIdentity(contributorProjectionJobKey));
            q.AddTrigger(opts => opts
                .ForJob(contributorProjectionJobKey)
                .WithIdentity(ContributorPeriodProjectionJob.TriggerIdentity)
                .WithCronSchedule(ContributorPeriodProjectionJob.Cron));

            // R0583 / TOR CF 09.06 / CF 09.09 — background report runner. Every
            // 60 seconds the job drains up to ReportJobBackgroundJob.BatchSize (10)
            // queued ReportJob rows by calling IReportJobRunner.RunBatchAsync. The
            // [DisallowConcurrentExecution] guard combined with FIFO row-level state
            // transitions (Queued -> Running) gives deterministic drainage.
            var reportJobKey = new JobKey(ReportJobBackgroundJob.JobIdentity);
            q.AddJob<ReportJobBackgroundJob>(opts => opts.WithIdentity(reportJobKey));
            q.AddTrigger(opts => opts
                .ForJob(reportJobKey)
                .WithIdentity(ReportJobBackgroundJob.TriggerIdentity)
                .WithCronSchedule(ReportJobBackgroundJob.Cron));

            // R0911 / TOR BP 2.2-B — Treasury payment-receipt distribution. Every
            // 15 minutes the job drains up to TreasuryDistributionJob.BatchSize (100)
            // Pending receipts by calling ITreasuryPaymentService.DistributeAsync on
            // each. Idempotent — the service rejects non-Pending receipts with the
            // stable ALREADY_DISTRIBUTED message so the [DisallowConcurrentExecution]
            // guard is belt-and-braces.
            var treasuryJobKey = new JobKey(TreasuryDistributionJob.JobIdentity);
            q.AddJob<TreasuryDistributionJob>(opts => opts.WithIdentity(treasuryJobKey));
            q.AddTrigger(opts => opts
                .ForJob(treasuryJobKey)
                .WithIdentity(TreasuryDistributionJob.TriggerIdentity)
                .WithCronSchedule(TreasuryDistributionJob.Cron));

            // R0817 / TOR BP 1.2-H — staggered-penalty default-detection. Daily
            // at 04:00 UTC the job iterates Active PenaltyRepaymentPlan rows and
            // flips any plan whose earliest unpaid installment is past due AND
            // not paid for > 30 days to Defaulted. Idempotent on the Status
            // predicate so the [DisallowConcurrentExecution] guard is
            // belt-and-braces.
            var penaltyDefaultJobKey = new JobKey(PenaltyRepaymentDefaultDetectionJob.JobIdentity);
            q.AddJob<PenaltyRepaymentDefaultDetectionJob>(opts => opts.WithIdentity(penaltyDefaultJobKey));
            q.AddTrigger(opts => opts
                .ForJob(penaltyDefaultJobKey)
                .WithIdentity(PenaltyRepaymentDefaultDetectionJob.TriggerIdentity)
                .WithCronSchedule(PenaltyRepaymentDefaultDetectionJob.Cron));

            // R0818 / TOR BP 1.2-I — daily BASS-receipts summary. Daily at 23:55
            // UTC the job groups all TreasuryPaymentReceipt rows distributed
            // during the operating day by DistributionStatus and emits a single
            // BASS_RECEIPTS.DAILY_SUMMARY Information-severity audit row plus a
            // cnas.bass.daily_summary{outcome=executed} counter increment. The
            // job is read-only against the receipts table so the
            // [DisallowConcurrentExecution] guard is belt-and-braces.
            var dailyBassSummaryJobKey = new JobKey(DailyBassReceiptsSummaryJob.JobIdentity);
            q.AddJob<DailyBassReceiptsSummaryJob>(opts => opts.WithIdentity(dailyBassSummaryJobKey));
            q.AddTrigger(opts => opts
                .ForJob(dailyBassSummaryJobKey)
                .WithIdentity(DailyBassReceiptsSummaryJob.TriggerIdentity)
                .WithCronSchedule(DailyBassReceiptsSummaryJob.Cron));

            // R2282 / TOR SEC 036 — nightly row-integrity check sweep. Daily
            // at 03:00 UTC the job creates an IntegrityCheckRun row, iterates
            // every registered IIntegrityCheck, persists findings, and
            // finalises the run. Read-only over real tables — the
            // [DisallowConcurrentExecution] guard is belt-and-braces and the
            // peak-hour gate's OffPeakOnly profile keeps it out of business
            // hours.
            var integrityCheckJobKey = new JobKey(IntegrityCheckJob.JobIdentity);
            q.AddJob<IntegrityCheckJob>(opts => opts.WithIdentity(integrityCheckJobKey));
            q.AddTrigger(opts => opts
                .ForJob(integrityCheckJobKey)
                .WithIdentity(IntegrityCheckJob.TriggerIdentity)
                .WithCronSchedule(IntegrityCheckJob.Cron));

            // R1503 / TOR §3.7-D — mass-recalculation apply sweeper. Daily at
            // 02:30 UTC the job picks up the OLDEST Ready LegalChangeEvent
            // whose EffectiveFrom is in the past and starts a DryRun. The
            // [DisallowConcurrentExecution] guard is belt-and-braces and the
            // peak-hour gate's OffPeakOnly profile keeps it off business hours.
            var massRecalcJobKey = new JobKey(MassRecalculationApplyJob.JobIdentity);
            q.AddJob<MassRecalculationApplyJob>(opts => opts.WithIdentity(massRecalcJobKey));
            q.AddTrigger(opts => opts
                .ForJob(massRecalcJobKey)
                .WithIdentity(MassRecalculationApplyJob.TriggerIdentity)
                .WithCronSchedule(MassRecalculationApplyJob.Cron));

            // R1710 / TOR INT 002 — offline-batch processing sweeper. Every
            // 5 minutes the job picks up the OLDEST Queued OfflineBatchSubmission
            // and invokes IOfflineBatchProcessor.ProcessAsync. The peak-hour
            // gate's OffPeakOnly profile keeps it off business hours.
            var offlineBatchJobKey = new JobKey(OfflineBatchProcessingJob.JobIdentity);
            q.AddJob<OfflineBatchProcessingJob>(opts => opts.WithIdentity(offlineBatchJobKey));
            q.AddTrigger(opts => opts
                .ForJob(offlineBatchJobKey)
                .WithIdentity(OfflineBatchProcessingJob.TriggerIdentity)
                .WithCronSchedule(OfflineBatchProcessingJob.Cron));

            // R1810 / TOR BP 1.2-I — daily Treasury feed import. At 04:00 UTC
            // the job pulls yesterday's BASS-receipts feed from the configured
            // source, parses each row, and upserts TreasuryPaymentReceipt rows
            // idempotently. Skips when a Completed import already exists for
            // the target date. OffPeakOnly profile keeps it inside the
            // off-peak window.
            var treasuryFeedJobKey = new JobKey(TreasuryFeedImportJob.JobIdentity);
            q.AddJob<TreasuryFeedImportJob>(opts => opts.WithIdentity(treasuryFeedJobKey));
            q.AddTrigger(opts => opts
                .ForJob(treasuryFeedJobKey)
                .WithIdentity(TreasuryFeedImportJob.TriggerIdentity)
                .WithCronSchedule(TreasuryFeedImportJob.Cron));

            // R2273 / TOR SEC 027 — sensitive-admin-action expiry sweep. Every 15
            // minutes the job calls ISensitiveAdminActionService.SweepExpiredAsync which
            // flips stale PendingApproval rows to Expired + emits a Critical audit row.
            // Always-on profile so the housekeeping runs regardless of peak hour.
            var sensActionSweepKey = new JobKey(SensitiveAdminActionExpirySweepJob.JobIdentity);
            q.AddJob<SensitiveAdminActionExpirySweepJob>(opts => opts.WithIdentity(sensActionSweepKey));
            q.AddTrigger(opts => opts
                .ForJob(sensActionSweepKey)
                .WithIdentity(SensitiveAdminActionExpirySweepJob.TriggerIdentity)
                .WithCronSchedule(SensitiveAdminActionExpirySweepJob.Cron));

            // R2279 / TOR SEC 033 — weekly classification-catalog snapshot. On
            // Sunday at 03:30 UTC the job captures a fresh snapshot via
            // IClassificationCatalogService.CaptureScheduledSnapshotAsync and
            // automatically computes drift against the most-recent prior
            // Captured snapshot. OffPeakOnly profile keeps it off business hours.
            var classificationSnapshotKey = new JobKey(ClassificationCatalogSnapshotJob.JobIdentity);
            q.AddJob<ClassificationCatalogSnapshotJob>(opts => opts.WithIdentity(classificationSnapshotKey));
            q.AddTrigger(opts => opts
                .ForJob(classificationSnapshotKey)
                .WithIdentity(ClassificationCatalogSnapshotJob.TriggerIdentity)
                .WithCronSchedule(ClassificationCatalogSnapshotJob.Cron));

            // R2003 / R0133 — daily template-language coverage scan. At 03:45
            // UTC the job calls ITemplateLanguageCoverageService.RecordCoverageRunAsync
            // with the canonical RO/EN/RU + OnlyApproved=true filter. Each new
            // (TemplateId, MissingLanguage) gap is persisted as a finding +
            // emits a Critical TEMPLATE.COVERAGE.GAP_DETECTED audit row. The
            // OffPeakOnly profile keeps the scan off business hours.
            var coverageScanKey = new JobKey(TemplateLanguageCoverageScanJob.JobIdentity);
            q.AddJob<TemplateLanguageCoverageScanJob>(opts => opts.WithIdentity(coverageScanKey));
            q.AddTrigger(opts => opts
                .ForJob(coverageScanKey)
                .WithIdentity(TemplateLanguageCoverageScanJob.TriggerIdentity)
                .WithCronSchedule(TemplateLanguageCoverageScanJob.Cron));

            // R2430 / R2431 / R2433 / TOR M4 — daily migration DryRun. At 02:15
            // UTC the job picks the OLDEST Active MigrationPlan that has NOT
            // been DryRun-imported in the last 7 days and triggers a DryRun
            // import via IMigrationImporter. OffPeakOnly profile keeps the
            // job off business hours.
            var migrationDryRunKey = new JobKey(MigrationDryRunJob.JobIdentity);
            q.AddJob<MigrationDryRunJob>(opts => opts.WithIdentity(migrationDryRunKey));
            q.AddTrigger(opts => opts
                .ForJob(migrationDryRunKey)
                .WithIdentity(MigrationDryRunJob.TriggerIdentity)
                .WithCronSchedule(MigrationDryRunJob.Cron));

            // R2307 / TOR SEC 060 — every 30 minutes the job enumerates Active
            // BackupPolicy rows, checks whether each policy's cron fired
            // within the look-back window, and triggers a Scheduled backup
            // run via IBackupOrchestrator when due. OffPeakOnly profile keeps
            // the job off business hours; DisallowConcurrentExecution +
            // per-policy idempotency predicate make a re-fire safe.
            var backupExecKey = new JobKey(BackupExecutionJob.JobIdentity);
            q.AddJob<BackupExecutionJob>(opts => opts.WithIdentity(backupExecKey));
            q.AddTrigger(opts => opts
                .ForJob(backupExecKey)
                .WithIdentity(BackupExecutionJob.TriggerIdentity)
                .WithCronSchedule(BackupExecutionJob.Cron));

            // R2307 / TOR SEC 060 — daily at 03:30 UTC the job invokes
            // IBackupOrchestrator.SweepExpiredRunsAsync to purge run payloads
            // past their retention window. OffPeakOnly profile keeps the job
            // off business hours.
            var backupSweepKey = new JobKey(BackupRetentionSweepJob.JobIdentity);
            q.AddJob<BackupRetentionSweepJob>(opts => opts.WithIdentity(backupSweepKey));
            q.AddTrigger(opts => opts
                .ForJob(backupSweepKey)
                .WithIdentity(BackupRetentionSweepJob.TriggerIdentity)
                .WithCronSchedule(BackupRetentionSweepJob.Cron));

            // R2500 / TOR PIR 020-023 — helpdesk SLA evaluation. Every 5
            // minutes the job invokes ISupportTicketSlaEvaluator.EvaluateAsync
            // which records newly-detected SLA events idempotently and
            // auto-escalates breached tickets. Always-on profile keeps the
            // sweep running 24/7 — the 5-minute Critical first-response
            // target (PIR 020) cannot wait for an off-peak window.
            var supportSlaKey = new JobKey(SupportTicketSlaEvaluationJob.JobIdentity);
            q.AddJob<SupportTicketSlaEvaluationJob>(opts => opts.WithIdentity(supportSlaKey));
            q.AddTrigger(opts => opts
                .ForJob(supportSlaKey)
                .WithIdentity(SupportTicketSlaEvaluationJob.TriggerIdentity)
                .WithCronSchedule(SupportTicketSlaEvaluationJob.Cron));

            // R2504 / TOR PIR 024 — daily at 09:00 UTC the job invokes
            // ISystemUpdateEventService.NotifyAsync on every Planned event
            // whose lead-time deadline is approaching, guaranteeing CNAS
            // gets the advance-notice signal per the parent schedule's
            // NoticeLeadTimeDays setting. Always-on profile keeps the
            // job firing regardless of peak hour.
            var updateNoticeKey = new JobKey(SystemUpdateNotificationJob.JobIdentity);
            q.AddJob<SystemUpdateNotificationJob>(opts => opts.WithIdentity(updateNoticeKey));
            q.AddTrigger(opts => opts
                .ForJob(updateNoticeKey)
                .WithIdentity(SystemUpdateNotificationJob.TriggerIdentity)
                .WithCronSchedule(SystemUpdateNotificationJob.Cron));

            // R2506 / TOR PIR 037-040 — daily at 04:15 UTC the job calls
            // IQualityRiskService.ListOverdueForReviewAsync(365) and emits a
            // QA_RISK.REVIEW_OVERDUE Information-severity audit row per
            // overdue risk. Always-on profile because overdue-review
            // reminders should not be gated by the off-peak window.
            var qaRiskSweepKey = new JobKey(QualityRiskReviewSweepJob.JobIdentity);
            q.AddJob<QualityRiskReviewSweepJob>(opts => opts.WithIdentity(qaRiskSweepKey));
            q.AddTrigger(opts => opts
                .ForJob(qaRiskSweepKey)
                .WithIdentity(QualityRiskReviewSweepJob.TriggerIdentity)
                .WithCronSchedule(QualityRiskReviewSweepJob.Cron));

            // R0203 / TOR CF 20.06 — daily at 02:00 UTC the RSP ingestion
            // job invokes IExternalSourceIngestionService.TriggerScheduledRunAsync
            // for the RSP source code. OffPeakOnly profile keeps the upstream
            // MConnect calls off business hours. Until the MEGA cert + MConnect
            // agreement land the connector returns EXT_SRC.RSP_NOT_CONFIGURED
            // deterministically; the job still records a Failed run row so
            // operators have a cadence trail.
            var rspIngestionKey = new JobKey(RspIngestionJob.JobIdentity);
            q.AddJob<RspIngestionJob>(opts => opts.WithIdentity(rspIngestionKey));
            q.AddTrigger(opts => opts
                .ForJob(rspIngestionKey)
                .WithIdentity(RspIngestionJob.TriggerIdentity)
                .WithCronSchedule(RspIngestionJob.Cron));

            // R1000..R1034 / TOR §3.2-Z — daily at 03:00 UTC the
            // recurrent-payment dispatcher picks every Active schedule with
            // NextPaymentDate ≤ today, generates the corresponding
            // MPayOrder rows, and advances NextPaymentDate per cadence.
            // DisallowConcurrentExecution + per-row idempotency keep a
            // re-fire safe.
            var recurrentPaymentKey = new JobKey(RecurrentPaymentJob.JobIdentity);
            q.AddJob<RecurrentPaymentJob>(opts => opts.WithIdentity(recurrentPaymentKey));
            q.AddTrigger(opts => opts
                .ForJob(recurrentPaymentKey)
                .WithIdentity(RecurrentPaymentJob.TriggerIdentity)
                .WithCronSchedule(RecurrentPaymentJob.Cron));
        });

        services.AddQuartzHostedService(opts =>
        {
            opts.WaitForJobsToComplete = true;
        });

        // Attach the failed-job listener once the scheduler has been constructed. The
        // listener is appended to the scheduler-wide listener manager so EVERY job
        // failure (regardless of which JobKey raised it) ends up in the DLQ.
        services.AddHostedService<FailedJobListenerInstaller>();

        // R0200 / TOR CF 20.01-03, MR 012 — startup reconciler that applies persisted
        // cron / pause overrides to the running scheduler. Registered AFTER the
        // FailedJobListenerInstaller so it runs after the scheduler is ready.
        services.AddHostedService<JobScheduleApplicator>();

        return services;
    }
}

/// <summary>
/// Background-service shim that attaches <see cref="FailedJobListener"/> to the
/// running Quartz scheduler at startup. Quartz 3.x has no DI-side
/// <c>AddJobListener</c> extension on <see cref="IServiceCollection"/> — the canonical
/// path is to resolve <see cref="ISchedulerFactory"/>, await
/// <see cref="ISchedulerFactory.GetScheduler(System.Threading.CancellationToken)"/>,
/// and call <c>ListenerManager.AddJobListener</c>. We do that in
/// <see cref="StartAsync(System.Threading.CancellationToken)"/> so the listener is
/// present for the very first fire of every registered job.
/// </summary>
/// <remarks>
/// The installer creates a dedicated DI scope to resolve the listener so the listener's
/// scoped <see cref="IFailedJobStore"/> dependency is satisfied. The scope is kept
/// alive for the lifetime of the installer to mirror the scheduler's lifetime — when
/// the scheduler is torn down at host shutdown the scope is disposed alongside it.
/// </remarks>
internal sealed class FailedJobListenerInstaller(
    ISchedulerFactory schedulerFactory,
    IServiceProvider serviceProvider) : IHostedService
{
    private readonly ISchedulerFactory _schedulerFactory = schedulerFactory;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    /// <summary>
    /// Long-lived DI scope that holds the listener (and therefore its
    /// <see cref="IFailedJobStore"/>) alive for the scheduler's lifetime.
    /// </summary>
    private IServiceScope? _scope;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _scope = _serviceProvider.CreateScope();
        var listener = _scope.ServiceProvider.GetRequiredService<FailedJobListener>();
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        scheduler.ListenerManager.AddJobListener(listener);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _scope?.Dispose();
        _scope = null;
        return Task.CompletedTask;
    }
}
