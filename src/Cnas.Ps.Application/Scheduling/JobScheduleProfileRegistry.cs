using System.Collections.Generic;

namespace Cnas.Ps.Application.Scheduling;

/// <summary>
/// R2173 / TOR PSR 004 — static registry mapping well-known Quartz job codes to
/// their default <see cref="JobScheduleProfile"/>. The peak-hour gate looks up
/// this dictionary on every evaluation; unknown codes fall back to
/// <see cref="JobScheduleProfileMode.Anytime"/> (fire on the cron, no gating).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a static registry.</b> The profile-to-job mapping is a deployment
/// constant — operators do not edit individual profiles at runtime today. The
/// static dictionary keeps the gate allocation-free on the hot path and
/// surfaces every declared job code in one place for code review.
/// </para>
/// <para>
/// <b>Job-code constants.</b> Each constant on this type matches the literal
/// string each job exposes as <c>JobCode</c> in
/// <c>Cnas.Ps.Infrastructure.Jobs</c>. The two surfaces are intentionally
/// referenced by string so the Application layer does not have to take a
/// dependency on Infrastructure.
/// </para>
/// </remarks>
public static class JobScheduleProfileRegistry
{
    /// <summary>Daily KPI dashboard snapshot (R0201 / CF 20.02).</summary>
    public const string KpiSnapshot = "KpiSnapshot";

    /// <summary>Daily contributor period-projection rebuild (R0153 / CF 19.05).</summary>
    public const string ContributorPeriodProjection = "ContributorPeriodProjection";

    /// <summary>Audit archive replay sweeper (R0188).</summary>
    public const string AuditArchiveReplay = "AuditArchiveReplay";

    /// <summary>SIEM forwarder (R0190 / SEC 049) — security-critical, runs continuously.</summary>
    public const string SiemForwarder = "SiemForwarder";

    /// <summary>Security alert evaluator (R0189 / SEC 048) — security-critical, runs continuously.</summary>
    public const string SecurityAlertEvaluator = "SecurityAlertEvaluator";

    /// <summary>Pending admin-action backlog observer (R0058).</summary>
    public const string AdminActionBacklogObserver = "AdminActionBacklogObserver";

    /// <summary>Treasury payment-receipt distribution (R0911 / BP 2.2-B) — must run continuously.</summary>
    public const string TreasuryDistribution = "TreasuryDistribution";

    /// <summary>Background report-job runner (R0583 / CF 09.06).</summary>
    public const string ReportJobBackground = "ReportJobBackground";

    /// <summary>Workflow notification strategy cache refresh (R0128 / CF 16.14).</summary>
    public const string WorkflowNotificationStrategyCacheRefresh = "WorkflowNotificationStrategyCacheRefresh";

    /// <summary>Penalty-repayment default detection (R0817 / BP 1.2-H).</summary>
    public const string PenaltyRepaymentDefaultDetection = "PenaltyRepaymentDefaultDetection";

    /// <summary>Daily BASS-receipts summary (R0818 / BP 1.2-I).</summary>
    public const string DailyBassReceiptsSummary = "DailyBassReceiptsSummary";

    /// <summary>Session auto-lock sweep (R2267 / SEC 020).</summary>
    public const string SessionAutoLock = "SessionAutoLock";

    /// <summary>Bulk-selection cleanup (R0166 / CF 03.11).</summary>
    public const string BulkSelectionCleanup = "BulkSelectionCleanup";

    /// <summary>User-absence lifecycle sweep (R0127 / CF 16.11).</summary>
    public const string UserAbsenceLifecycle = "UserAbsenceLifecycle";

    /// <summary>Nightly row-integrity check sweep (R2282 / SEC 036).</summary>
    public const string IntegrityCheck = "IntegrityCheck";

    /// <summary>Daily mass-recalculation apply sweep (R1503 / §3.7-D).</summary>
    public const string MassRecalculationApply = "MassRecalculationApply";

    /// <summary>Offline-batch processing sweep (R1710 / INT 002).</summary>
    public const string OfflineBatchProcessing = "OfflineBatchProcessing";

    /// <summary>Daily Treasury feed import (R1810 / BP 1.2-I).</summary>
    public const string TreasuryFeedImport = "TreasuryFeedImport";

    /// <summary>
    /// R2273 / SEC 027 — sensitive-admin-action expiry sweep. Operational housekeeping
    /// that must run regardless of peak hour so stale pending requests don't linger.
    /// </summary>
    public const string SensitiveAdminActionExpirySweep = "SensitiveAdminActionExpirySweep";

    /// <summary>Weekly classification-catalog snapshot job (R2279 / SEC 033).</summary>
    public const string ClassificationCatalogSnapshot = "ClassificationCatalogSnapshot";

    /// <summary>Daily template-language coverage scan job (R2003 / R0133).</summary>
    public const string TemplateLanguageCoverageScan = "TemplateLanguageCoverageScan";

    /// <summary>Daily migration DryRun job (R2430 / R2431 / R2433 / M4).</summary>
    public const string MigrationDryRun = "MigrationDryRun";

    /// <summary>Backup-execution orchestrator job (R2307 / SEC 060).</summary>
    public const string BackupExecution = "BackupExecution";

    /// <summary>Backup-retention sweep job (R2307 / SEC 060).</summary>
    public const string BackupRetentionSweep = "BackupRetentionSweep";

    /// <summary>
    /// R2500 / PIR 020-023 — helpdesk SLA evaluation job. Always-on profile
    /// because SLA enforcement must run 24/7 (the 5-minute Critical first-
    /// response target cannot wait for an off-peak window).
    /// </summary>
    public const string SupportTicketSlaEvaluation = "SupportTicketSlaEvaluation";

    /// <summary>
    /// R2504 / PIR 024 — system-update notification orchestrator. Always-on
    /// profile so notice cadence is honoured regardless of peak hour (the
    /// lead-time guarantee must not depend on the off-peak window).
    /// </summary>
    public const string SystemUpdateNotification = "SystemUpdateNotification";

    /// <summary>
    /// R2506 / PIR 037-040 — quality-risk annual-review sweep. Always-on
    /// profile so the overdue-review reminders do not depend on the
    /// off-peak window.
    /// </summary>
    public const string QualityRiskReviewSweep = "QualityRiskReviewSweep";

    /// <summary>
    /// R0203 / CF 20.06 — RSP (Registrul de Stat al Populației) ingestion
    /// job. OffPeakOnly profile because the upstream MConnect call is heavy
    /// and the citizen-side store does not need sub-day freshness.
    /// </summary>
    public const string RspIngestion = "RspIngestion";

    /// <summary>
    /// Read-only dictionary mapping every well-known job code to its
    /// <see cref="JobScheduleProfile"/>. The peak-hour gate looks up by code;
    /// unknown codes default to <see cref="JobScheduleProfileMode.Anytime"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordering is alphabetical-ish (with security-critical jobs grouped) to
    /// keep diff review readable. The dictionary is case-sensitive — job codes
    /// match the literal string each job publishes as <c>JobCode</c>.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, JobScheduleProfile> Defaults =
        new Dictionary<string, JobScheduleProfile>(System.StringComparer.Ordinal)
        {
            [KpiSnapshot] = new(KpiSnapshot, JobScheduleProfileMode.OffPeakOnly),
            [ContributorPeriodProjection] = new(ContributorPeriodProjection, JobScheduleProfileMode.OffPeakOnly),
            [AuditArchiveReplay] = new(AuditArchiveReplay, JobScheduleProfileMode.OffPeakOnly),
            [SiemForwarder] = new(SiemForwarder, JobScheduleProfileMode.Always),
            [SecurityAlertEvaluator] = new(SecurityAlertEvaluator, JobScheduleProfileMode.Always),
            [AdminActionBacklogObserver] = new(AdminActionBacklogObserver, JobScheduleProfileMode.Anytime),
            [TreasuryDistribution] = new(TreasuryDistribution, JobScheduleProfileMode.Anytime),
            [ReportJobBackground] = new(ReportJobBackground, JobScheduleProfileMode.OffPeakOnly),
            [WorkflowNotificationStrategyCacheRefresh] = new(WorkflowNotificationStrategyCacheRefresh, JobScheduleProfileMode.Always),
            [PenaltyRepaymentDefaultDetection] = new(PenaltyRepaymentDefaultDetection, JobScheduleProfileMode.OffPeakOnly),
            [DailyBassReceiptsSummary] = new(DailyBassReceiptsSummary, JobScheduleProfileMode.OffPeakOnly),
            [SessionAutoLock] = new(SessionAutoLock, JobScheduleProfileMode.Always),
            [BulkSelectionCleanup] = new(BulkSelectionCleanup, JobScheduleProfileMode.OffPeakOnly),
            [UserAbsenceLifecycle] = new(UserAbsenceLifecycle, JobScheduleProfileMode.Anytime),
            [IntegrityCheck] = new(IntegrityCheck, JobScheduleProfileMode.OffPeakOnly),
            [MassRecalculationApply] = new(MassRecalculationApply, JobScheduleProfileMode.OffPeakOnly),
            [OfflineBatchProcessing] = new(OfflineBatchProcessing, JobScheduleProfileMode.OffPeakOnly),
            [TreasuryFeedImport] = new(TreasuryFeedImport, JobScheduleProfileMode.OffPeakOnly),
            [SensitiveAdminActionExpirySweep] = new(SensitiveAdminActionExpirySweep, JobScheduleProfileMode.Always),
            [ClassificationCatalogSnapshot] = new(ClassificationCatalogSnapshot, JobScheduleProfileMode.OffPeakOnly),
            [TemplateLanguageCoverageScan] = new(TemplateLanguageCoverageScan, JobScheduleProfileMode.OffPeakOnly),
            [MigrationDryRun] = new(MigrationDryRun, JobScheduleProfileMode.OffPeakOnly),
            [BackupExecution] = new(BackupExecution, JobScheduleProfileMode.OffPeakOnly),
            [BackupRetentionSweep] = new(BackupRetentionSweep, JobScheduleProfileMode.OffPeakOnly),
            [SupportTicketSlaEvaluation] = new(SupportTicketSlaEvaluation, JobScheduleProfileMode.Always),
            [SystemUpdateNotification] = new(SystemUpdateNotification, JobScheduleProfileMode.Always),
            [QualityRiskReviewSweep] = new(QualityRiskReviewSweep, JobScheduleProfileMode.Always),
            [RspIngestion] = new(RspIngestion, JobScheduleProfileMode.OffPeakOnly),
        };
}
