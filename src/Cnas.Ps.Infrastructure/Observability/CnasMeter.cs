using System.Diagnostics.Metrics;
using Cnas.Ps.Infrastructure.Services;

namespace Cnas.Ps.Infrastructure.Observability;

/// <summary>
/// Static holder for CNAS-owned <see cref="Meter"/> instruments. All custom metrics
/// emitted by the Infrastructure subsystems hang off this one meter, registered with
/// the OTel pipeline in <c>ApiCompositionRoot.AddCnasObservability</c> via
/// <c>AddMeter("Cnas.Ps.*")</c> (wildcard). R0040 follow-up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a single meter.</b> Every subsystem counter shares the same meter so
/// operators have one place to set retention / cardinality / view configuration.
/// The instrument <i>names</i> (e.g. <c>cnas.audit.dropped</c>) provide the per-
/// subsystem facet without exploding the meter inventory.
/// </para>
/// <para>
/// <b>Counters are monotonic and cheap.</b> Each <c>Add</c> call is allocation-free
/// when measurements are not being collected and constant-time when they are.
/// Counters never carry user identifiers, IDNPs, IP addresses, or token material —
/// CLAUDE.md §5.6 invariant. Tags are bounded cardinality only
/// (e.g. <c>reason="queue_full"</c>, <c>chain.valid=true</c>,
/// <c>batch.size_bucket="50"</c>).
/// </para>
/// <para>
/// <b>Gauges sample on each export interval.</b> Subsystems whose gauge value
/// is cheap to read in-process (queue depth, file count) register a callback
/// directly. Subsystems whose gauge requires scoped state (DbContext queries)
/// rely on a background updater that publishes a primitive <c>long</c> the gauge
/// callback can read non-blocking.
/// </para>
/// <para>
/// <b>Process-static state.</b> Because <see cref="Meter"/> instruments are
/// process-static there is no per-test isolation seam — tests observe the meter
/// via <c>MeterListener</c> instead. The increment APIs are thread-safe.
/// </para>
/// </remarks>
public static class CnasMeter
{
    /// <summary>Well-known meter name registered with the OTel pipeline.</summary>
    public const string MeterName = "Cnas.Ps.Subsystems";

    /// <summary>The single <see cref="Meter"/> instance owning every CNAS instrument.</summary>
    internal static readonly Meter Meter = new(MeterName, version: "1.0.0");

    /// <summary>Audit records successfully enqueued to the in-memory queue.</summary>
    public static readonly Counter<long> AuditEnqueued = Meter.CreateCounter<long>(
        name: "cnas.audit.enqueued",
        description: "Audit records successfully enqueued to the in-memory queue.");

    /// <summary>
    /// Audit records dropped due to queue-full, flush failure, or archive failure.
    /// Tagged with <c>reason</c> = <c>queue_full</c> | <c>flush_failed</c> |
    /// <c>archive_failed</c> so operators can distinguish the failure mode.
    /// </summary>
    public static readonly Counter<long> AuditDropped = Meter.CreateCounter<long>(
        name: "cnas.audit.dropped",
        description: "Audit records dropped due to queue-full or flush/archive failure.");

    /// <summary>
    /// Audit batches flushed to the DB and MLog. Tagged with <c>batch.size_bucket</c>
    /// = <c>1</c> | <c>5</c> | <c>10</c> | <c>50</c> so operators can chart
    /// flush sizes without paying for unbounded cardinality on the raw size.
    /// </summary>
    public static readonly Counter<long> AuditFlushed = Meter.CreateCounter<long>(
        name: "cnas.audit.flushed",
        description: "Audit batches flushed to the DB and MLog.");

    /// <summary>Audit batches archived after flush failure (R0188 spill).</summary>
    public static readonly Counter<long> AuditArchived = Meter.CreateCounter<long>(
        name: "cnas.audit.archived",
        description: "Audit batches archived to durable storage after flush failure.");

    /// <summary>Replay job iterations that processed at least one archive file.</summary>
    public static readonly Counter<long> AuditReplayAttempted = Meter.CreateCounter<long>(
        name: "cnas.audit.replay.attempted",
        description: "Replay job iterations that processed at least one archive file.");

    /// <summary>Archive files replayed successfully.</summary>
    public static readonly Counter<long> AuditReplaySucceeded = Meter.CreateCounter<long>(
        name: "cnas.audit.replay.succeeded",
        description: "Archive files replayed successfully.");

    /// <summary>Archive files that failed to replay.</summary>
    public static readonly Counter<long> AuditReplayFailed = Meter.CreateCounter<long>(
        name: "cnas.audit.replay.failed",
        description: "Archive files that failed to replay.");

    /// <summary>
    /// Audit chain verification runs (R0194). Tagged with <c>chain.valid</c>
    /// = <c>true</c> | <c>false</c> so operators can chart the validity outcome
    /// alongside the run rate.
    /// </summary>
    public static readonly Counter<long> AuditChainVerified = Meter.CreateCounter<long>(
        name: "cnas.audit.chain.verified",
        description: "Audit chain verification runs; tag chain.valid records the outcome.");

    /// <summary>
    /// R0184 / TOR SEC 042 — incremented once per audit event emitted by the
    /// universal <c>AuditingInterceptor</c> SaveChanges hook. Tagged with
    /// <c>event_code</c> (the composed entity/state code, e.g.
    /// <c>USERPROFILE.MODIFIED</c>) so operators can chart auto-audit volume
    /// per entity kind. Volume is naturally bounded by the
    /// <c>AutoAuditAttribute</c> opt-in scope.
    /// </summary>
    public static readonly Counter<long> AuditInterceptorEventEmitted = Meter.CreateCounter<long>(
        name: "cnas.audit.interceptor.emitted",
        description: "Audit events emitted by the universal SaveChanges interceptor; tagged with event_code.");

    /// <summary>
    /// R0196 / TOR CF 23.02 — incremented once per audit-category mutation
    /// (create / modify / activate / deactivate). Tagged with
    /// <c>change_kind</c> = <c>created</c> | <c>modified</c> | <c>activated</c>
    /// | <c>deactivated</c>.
    /// </summary>
    public static readonly Counter<long> AuditCategoryMutated = Meter.CreateCounter<long>(
        name: "cnas.audit.category.mutated",
        description: "Audit-category mutations; tagged with change_kind.");

    /// <summary>
    /// R0200 / TOR CF 20.01-03, MR 012 — incremented once per cron-schedule
    /// override mutation (upsert / pause / resume). Tagged with
    /// <c>change_kind</c> = <c>upserted</c> | <c>paused</c> | <c>resumed</c>
    /// and <c>job_code</c> (bounded by the registered Quartz job set).
    /// </summary>
    public static readonly Counter<long> CronScheduleMutated = Meter.CreateCounter<long>(
        name: "cnas.cron.schedule.mutated",
        description: "Cron-schedule override mutations; tagged with change_kind and job_code.");

    /// <summary>JWT access tokens issued (R0053).</summary>
    public static readonly Counter<long> JwtAccessIssued = Meter.CreateCounter<long>(
        name: "cnas.jwt.access.issued",
        description: "JWT access tokens issued.");

    /// <summary>Refresh tokens issued (new login families) (R0053).</summary>
    public static readonly Counter<long> RefreshIssued = Meter.CreateCounter<long>(
        name: "cnas.refresh.issued",
        description: "Refresh tokens issued (new login families).");

    /// <summary>Refresh tokens rotated successfully (R0053).</summary>
    public static readonly Counter<long> RefreshRotated = Meter.CreateCounter<long>(
        name: "cnas.refresh.rotated",
        description: "Refresh tokens rotated successfully.");

    /// <summary>
    /// Refresh-token reuse detected — the security-critical event. Tagged with
    /// <c>family.revoked</c> = <c>true</c> so operators can chart the revoke
    /// outcome alongside the detection rate. Should pager on-call when sustained.
    /// </summary>
    public static readonly Counter<long> RefreshReuseDetected = Meter.CreateCounter<long>(
        name: "cnas.refresh.reuse_detected",
        description: "Refresh-token reuse detected; family revoked.");

    /// <summary>Refresh token families explicitly revoked (logout) (R0053).</summary>
    public static readonly Counter<long> RefreshRevoked = Meter.CreateCounter<long>(
        name: "cnas.refresh.revoked",
        description: "Refresh token families explicitly revoked (logout).");

    /// <summary>Pending admin actions submitted by makers (R0058).</summary>
    public static readonly Counter<long> AdminActionSubmitted = Meter.CreateCounter<long>(
        name: "cnas.admin.action.submitted",
        description: "Pending admin actions submitted by makers.");

    /// <summary>Pending admin actions approved by checkers (R0058).</summary>
    public static readonly Counter<long> AdminActionApproved = Meter.CreateCounter<long>(
        name: "cnas.admin.action.approved",
        description: "Pending admin actions approved by checkers.");

    /// <summary>Pending admin actions rejected by checkers (R0058).</summary>
    public static readonly Counter<long> AdminActionRejected = Meter.CreateCounter<long>(
        name: "cnas.admin.action.rejected",
        description: "Pending admin actions rejected by checkers.");

    /// <summary>Pending admin actions auto-expired by TTL (R0058).</summary>
    public static readonly Counter<long> AdminActionExpired = Meter.CreateCounter<long>(
        name: "cnas.admin.action.expired",
        description: "Pending admin actions auto-expired by TTL.");

    /// <summary>
    /// R2273 / SEC 027 — generic 4-eyes admin requests opened. Tagged with
    /// <c>action_code</c> so operators can chart volume per concrete sensitive action.
    /// </summary>
    public static readonly Counter<long> SensitiveAdminActionRequested = Meter.CreateCounter<long>(
        name: "cnas.sensitive_admin_action.requested",
        description: "Generic 4-eyes admin requests opened (R2273 / SEC 027).");

    /// <summary>
    /// R2273 / SEC 027 — 4-eyes decision outcome. Tagged with <c>action_code</c> +
    /// <c>outcome</c> = <c>approved</c> | <c>rejected</c> | <c>cancelled</c> |
    /// <c>expired</c>.
    /// </summary>
    public static readonly Counter<long> SensitiveAdminActionOutcome = Meter.CreateCounter<long>(
        name: "cnas.sensitive_admin_action.outcome",
        description: "Generic 4-eyes admin decision outcomes (R2273 / SEC 027).");

    /// <summary>
    /// R2273 / SEC 027 — handler execution result after approval. Tagged with
    /// <c>action_code</c> + <c>result</c> = <c>succeeded</c> | <c>failed</c> |
    /// <c>no_handler</c>.
    /// </summary>
    public static readonly Counter<long> SensitiveAdminActionExecutionResult = Meter.CreateCounter<long>(
        name: "cnas.sensitive_admin_action.execution_result",
        description: "Generic 4-eyes admin handler execution result (R2273 / SEC 027).");

    /// <summary>
    /// R2273 / SEC 027 — sensitive-admin-action rows auto-expired by the sweep job.
    /// </summary>
    public static readonly Counter<long> SensitiveAdminActionExpired = Meter.CreateCounter<long>(
        name: "cnas.sensitive_admin_action.expired",
        description: "Generic 4-eyes admin rows auto-expired by the sweep job (R2273 / SEC 027).");

    /// <summary>
    /// Applications auto-closed by the missing-documents SLA timer (R0934). Incremented
    /// once per application that the <c>MissingDocsSlaJob</c> flips from
    /// <c>RejectedIncomplete</c> to <c>Rejected</c> after the 30-day window elapses.
    /// </summary>
    public static readonly Counter<long> ApplicationAutoClosed = Meter.CreateCounter<long>(
        name: "cnas.application.auto_closed",
        description: "Applications auto-closed by the missing-docs SLA timer (R0934).");

    /// <summary>
    /// Workflow tasks escalated by the unclaimed-task SLA job (R0202 / CF 20.05).
    /// Incremented once per task that the <c>UnclaimedTaskEscalationJob</c> finds sitting
    /// in a group inbox without being claimed past the configured window. The job emits
    /// an audit row + this counter per task; it does NOT auto-reassign (supervisor
    /// notification is gated on R0056 ABAC).
    /// </summary>
    public static readonly Counter<long> WorkflowTaskEscalated = Meter.CreateCounter<long>(
        name: "cnas.workflow.task.escalated",
        description: "Workflow tasks escalated by the unclaimed-task SLA job (R0202).");

    /// <summary>
    /// Notification dispatches that the dispatcher refused to deliver because the recipient's
    /// per-channel preference is opted out (R0171 / CF 22.02 / CF 04.08). Incremented once
    /// per persisted row whose <c>DeliveryStatus</c> ended up as
    /// <c>NotificationDeliveryStatus.Suppressed</c>. Tagged with <c>channel</c>
    /// (= <c>Email</c> | <c>Sms</c> | <c>InApp</c>) so operators can chart suppression by
    /// channel without unbounded cardinality.
    /// </summary>
    public static readonly Counter<long> NotificationSuppressed = Meter.CreateCounter<long>(
        name: "cnas.notification.suppressed",
        description: "Notifications suppressed by recipient channel opt-out (R0171).");

    /// <summary>
    /// Workflow-event notifications the orchestrator refused to dispatch because the
    /// per-workflow strategy explicitly disabled notifications for this event
    /// (R0128 / R0173 / CF 16.14). Incremented once per orchestrator call where the
    /// resolved strategy carried <c>IsEnabled = false</c>. Tagged with <c>event</c> (=
    /// the canonical event code, e.g. <c>Task.Assigned</c>) so operators can chart
    /// suppression by lifecycle moment without unbounded cardinality.
    /// </summary>
    public static readonly Counter<long> WorkflowNotifySuppressed = Meter.CreateCounter<long>(
        name: "cnas.workflow.notify.suppressed",
        description: "Workflow notifications suppressed by per-workflow strategy override (R0128).");

    /// <summary>
    /// R0381 — supervisor-driven task reassignments. Incremented once per successful
    /// <c>POST /api/tasks/{sqid}/reassign</c> call routed through
    /// <c>ITaskInboxService.ReassignTaskAsync</c>. Tagged with <c>reason_bucket</c>
    /// (<c>short</c> for reasons ≤30 chars, <c>long</c> otherwise) — the bucket is the
    /// only tag so cardinality stays bounded (the raw reason string is unbounded user
    /// input and must never become a metric label).
    /// </summary>
    public static readonly Counter<long> TaskReassignTotal = Meter.CreateCounter<long>(
        name: "cnas.task_reassign.total",
        description: "Supervisor-driven task reassignments by reason-length bucket (R0381).");

    /// <summary>
    /// Saved-search create / update operations (R0165 / CF 03.06). Incremented once per
    /// successful persistence — both fresh creates AND in-place updates by the owner.
    /// Idempotent duplicate-name creates (the service returns the existing row's Sqid
    /// without writing) do NOT increment because no new save took place. Tagless: the
    /// observable rate alone is the operator signal; sharing toggles and registry
    /// distribution are out of scope for this counter to keep cardinality bounded.
    /// </summary>
    public static readonly Counter<long> SavedSearchSaved = Meter.CreateCounter<long>(
        name: "cnas.saved_search.saved",
        description: "Saved-search create/update operations (R0165).");

    /// <summary>
    /// Audit rows forwarded to the SIEM via ArcSight CEF over syslog (R0190 / SEC 049).
    /// Incremented once per row included in a successful forwarding batch — i.e. rows
    /// the polling job scanned AND for which the exporter's transport call returned
    /// success. Rows the exporter filtered out by <c>MinSeverity</c> DO count toward
    /// this counter because the polling job advances the checkpoint past them
    /// regardless (see <c>SiemForwarderJob</c> remarks). Tagless: the observable rate
    /// alone is the operator signal that the SIEM feed is healthy.
    /// </summary>
    public static readonly Counter<long> SiemForwarded = Meter.CreateCounter<long>(
        name: "cnas.audit.siem_forwarded",
        description: "Audit rows forwarded to the SIEM via CEF syslog (R0190).");

    /// <summary>
    /// Security alerts fired by the rule evaluator (R0189 / SEC 048). Incremented once
    /// per <c>SecurityAlertRule</c> that the evaluator decided to fire — i.e. the
    /// matched-row count met the rule threshold AND the per-rule cooldown had elapsed.
    /// Tagged with <c>rule.code</c> (e.g. <c>FAILED_LOGIN_BURST</c>) so operators can
    /// chart per-rule fire rates and identify the noisiest patterns. Cardinality is
    /// bounded by the rule set size (≤100 in practice).
    /// </summary>
    public static readonly Counter<long> SecurityAlertFired = Meter.CreateCounter<long>(
        name: "cnas.security_alert.fired",
        description: "Security alerts fired by R0189 rules (tagged with rule code).");

    /// <summary>
    /// Audit rows suppressed by an admin-configured policy (R0182 / SEC 042). Incremented
    /// once per row whose matched <see cref="Cnas.Ps.Core.Domain.AuditPolicy"/> set
    /// <c>SuppressAudit=true</c> AND whose effective severity resolved to
    /// <see cref="Cnas.Ps.Core.Domain.AuditSeverity.Information"/>. Tagged with
    /// <c>policy</c> (the matched policy's natural code) so operators can chart per-
    /// policy suppression volume — cardinality is bounded by the policy-table size
    /// (≤100 rows in practice).
    /// </summary>
    public static readonly Counter<long> AuditPolicySuppressed = Meter.CreateCounter<long>(
        name: "cnas.audit.policy_suppressed",
        description: "Audit rows suppressed by an admin-configured policy (R0182).");

    /// <summary>
    /// Misconfigurations defended in depth at the drainer (R0182 / SEC 042). Incremented
    /// once per row where a matched policy attempted to suppress an event whose
    /// effective severity was NOT
    /// <see cref="Cnas.Ps.Core.Domain.AuditSeverity.Information"/> — the safeguard
    /// REFUSES the suppression and writes the row anyway. Tagged with <c>policy</c>
    /// (the offending policy code) so operators are alerted to the misconfiguration
    /// and can correct the policy.
    /// </summary>
    public static readonly Counter<long> AuditPolicyMisconfig = Meter.CreateCounter<long>(
        name: "cnas.audit.policy_misconfig",
        description: "Defense-in-depth refusals of policies that tried to suppress non-Information events (R0182).");

    /// <summary>
    /// Query-budget evaluations performed by <c>IQueryBudgetService.EvaluateAsync</c>
    /// (R0167 / CF 01.06 / CF 03.07-08). Incremented once per call regardless of
    /// outcome. Tagged with <c>registry</c> (e.g. <c>Solicitant</c>) and <c>allowed</c>
    /// (<c>true</c> | <c>false</c>) so operators can chart the per-registry refusal
    /// rate and detect spikes that indicate an unfilterable UI flow. Cardinality is
    /// bounded by <c>QueryBudgetRegistries.All.Count</c> × 2 (≈ 12 in practice).
    /// </summary>
    public static readonly Counter<long> QueryBudgetEvaluated = Meter.CreateCounter<long>(
        name: "cnas.query.budget_evaluated",
        description: "Query-budget evaluations per call (R0167).");

    /// <summary>
    /// Query-budget evaluations that REFUSED the underlying query (R0167 / CF 01.06).
    /// Incremented once per <c>EvaluateAsync</c> call whose verdict was
    /// <c>Allowed=false</c>. Tagged with <c>registry</c> so operators can chart the
    /// per-registry rejection volume. A sustained rejection rate on one registry
    /// indicates a UI flow that's letting users fire unbounded queries.
    /// </summary>
    public static readonly Counter<long> QueryBudgetRejected = Meter.CreateCounter<long>(
        name: "cnas.query.budget_rejected",
        description: "Query-budget evaluations that refused the underlying query (R0167).");

    /// <summary>
    /// User-absence rows flipped from <c>Planned</c> to <c>Active</c> by the
    /// <c>UserAbsenceLifecycleJob</c> (R0127 / CF 16.11). Incremented once per
    /// successfully activated row.
    /// </summary>
    public static readonly Counter<long> UserAbsenceActivated = Meter.CreateCounter<long>(
        name: "cnas.user_absence.activated",
        description: "User-absence rows activated by the lifecycle job (R0127).");

    /// <summary>
    /// User-absence rows flipped from <c>Active</c> to <c>Completed</c> by the
    /// <c>UserAbsenceLifecycleJob</c> (R0127 / CF 16.11). Incremented once per
    /// successfully completed row.
    /// </summary>
    public static readonly Counter<long> UserAbsenceCompleted = Meter.CreateCounter<long>(
        name: "cnas.user_absence.completed",
        description: "User-absence rows completed by the lifecycle job (R0127).");

    /// <summary>
    /// R2267 / SEC 020 — user sessions auto-locked by the <c>SessionAutoLockJob</c>
    /// for crossing the <c>SessionLimitOptions.IdleLockMinutes</c> threshold
    /// (default 15 minutes). Incremented once per successfully locked row;
    /// operators chart this counter alongside <c>cnas.auth.tokens_issued</c> to
    /// spot abnormal idle pressure (mass logouts, network outages, broken
    /// keep-alive heartbeats).
    /// </summary>
    public static readonly Counter<long> SessionAutoLocked = Meter.CreateCounter<long>(
        name: "cnas.session.auto_locked",
        description: "User sessions auto-locked by the idle-sweep job (R2267).");

    /// <summary>
    /// Template-variant render-time fall-back events (R0133 / CF 17.16). Incremented
    /// once per render whose requested language did not resolve to an approved
    /// variant and therefore fell back to the template's default language. Tagged
    /// with <c>from</c> (requested locale code) and <c>to</c> (default locale the
    /// renderer used instead) so operators can chart which locales are perpetually
    /// missing translations. Cardinality is bounded by
    /// <see cref="Cnas.Ps.Contracts.TemplateLanguages.All"/> ^ 2 (≤ 9 in practice).
    /// </summary>
    public static readonly Counter<long> TemplateRenderFallback = Meter.CreateCounter<long>(
        name: "cnas.template.render.fallback",
        description: "Template-variant renders that fell back to the default language (R0133).");

    /// <summary>
    /// R2003 / R0133 — incremented once per successful coverage-run
    /// completion. Tagged with <c>trigger_kind</c>
    /// (<c>scheduled</c> / <c>manual</c>); cardinality bounded to 2.
    /// </summary>
    public static readonly Counter<long> TemplateLanguageCoverageRunCompleted = Meter.CreateCounter<long>(
        name: "cnas.template.coverage.run_completed",
        description: "Template-language coverage runs completed, tagged by trigger_kind (R2003).");

    /// <summary>
    /// R2003 / R0133 — incremented once per NEW finding inserted by
    /// <c>RecordCoverageRunAsync</c>. Tagged with <c>language</c> (the
    /// missing-language code, lowercase ISO 639-1 / 639-2); cardinality
    /// bounded to the size of the configured required-language set
    /// (currently 3).
    /// </summary>
    public static readonly Counter<long> TemplateLanguageCoverageGapDetected = Meter.CreateCounter<long>(
        name: "cnas.template.coverage.gap_detected",
        description: "Template-language coverage gaps newly detected, tagged by language (R2003).");

    /// <summary>
    /// R2003 / R0133 — incremented once per finding acknowledged by an
    /// operator. Untagged — the rate alone signals operators are working
    /// through the backlog.
    /// </summary>
    public static readonly Counter<long> TemplateLanguageCoverageGapAcknowledged = Meter.CreateCounter<long>(
        name: "cnas.template.coverage.gap_acknowledged",
        description: "Template-language coverage gaps acknowledged by operators (R2003).");

    /// <summary>
    /// Workflow-lifecycle business-rule evaluations (R0124 / CF 16.08). Incremented
    /// once per call to <c>IWorkflowRuleEngine.Evaluate{Start|Transition|Completion}Async</c>.
    /// Tagged with <c>stage</c> (= <c>Start</c> | <c>Transition</c> | <c>Completion</c>)
    /// and <c>allowed</c> (= <c>true</c> | <c>false</c>) so operators can chart
    /// per-stage block rates and detect rule packs that started refusing en masse
    /// after a configuration change. Cardinality is bounded by 3 × 2 = 6.
    /// </summary>
    public static readonly Counter<long> WorkflowRuleEvaluated = Meter.CreateCounter<long>(
        name: "cnas.workflow.rule.evaluated",
        description: "Workflow-lifecycle business-rule evaluations per stage and outcome (R0124).");

    /// <summary>
    /// Workflow rule-pack backend invocations made by the
    /// <c>DecisionEngineBackedWorkflowRulePackEvaluator</c> (R0124 continuation).
    /// Incremented once per call to the backend regardless of outcome, including the
    /// exception-containment path. Tagged with <c>outcome</c> (= <c>allow</c> |
    /// <c>deny</c> | <c>error</c>) so operators can chart the backend-level verdict
    /// distribution independently of the engine-level
    /// <c>cnas.workflow.rule.evaluated</c> counter. Cardinality is bounded to three
    /// values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a second counter.</b> <c>cnas.workflow.rule.evaluated</c> charts the
    /// engine's verdict (which includes pack-not-configured pass-throughs that never
    /// reach the backend). This counter charts only the backend round-trips, so
    /// operators can spot the case where the engine is firing but the backend is
    /// short-circuiting (e.g. the no-op backend) or vice versa.
    /// </para>
    /// </remarks>
    public static readonly Counter<long> WorkflowRuleDecisionEngineInvoked = Meter.CreateCounter<long>(
        name: "cnas.workflow.rule.decision_engine_invoked",
        description: "Workflow rule-pack backend invocations per outcome (R0124 continuation).");

    /// <summary>
    /// MConnect partner-direct fallback invocations (R0104 / TOR CF 14.03). Incremented
    /// once per call where the MConnect endpoint failed AND the fallback closure was
    /// invoked. Tagged with <c>partner</c> (= the partner-system code, e.g. <c>RSP</c>)
    /// and <c>reason</c> (= <c>Timeout</c> | <c>Http5xx</c> | <c>Network</c>) so operators
    /// can chart per-partner fallback volume and identify the most common failure mode.
    /// Cardinality is bounded by the partner registry size (≤ 11 in practice) × 3.
    /// </summary>
    public static readonly Counter<long> MConnectFallbackInvoked = Meter.CreateCounter<long>(
        name: "cnas.mconnect.fallback_invoked",
        description: "MConnect partner-direct fallback invocations (R0104).");

    /// <summary>
    /// MConnect partner-direct fallback FAILURES (R0104 / TOR CF 14.03). Incremented
    /// once per call where the fallback closure was invoked AND itself returned failure
    /// (or threw). Tagged with <c>partner</c> so operators can chart per-partner
    /// reliability of the direct path independently of MConnect itself.
    /// </summary>
    public static readonly Counter<long> MConnectFallbackFailed = Meter.CreateCounter<long>(
        name: "cnas.mconnect.fallback_failed",
        description: "MConnect partner-direct fallback failures (R0104).");

    /// <summary>
    /// MSign signature-verification outcomes (R0112 / TOR CF 14.06). Incremented once per
    /// call to <c>IMSignClient.VerifySignatureAsync</c>. Tagged with <c>result</c>
    /// (= <c>valid</c> | <c>invalid</c>) so operators can chart per-outcome rate and
    /// alert on a spike in invalid signatures (potential attack indicator).
    /// </summary>
    public static readonly Counter<long> MSignVerifyResult = Meter.CreateCounter<long>(
        name: "cnas.msign.verify",
        description: "MSign signature-verification outcomes per result (R0112).");

    /// <summary>
    /// R0321 / R0224 / UI 008 — auto-save / manual-save calls that short-circuited because
    /// the supplied <c>FormDataJson</c> byte-matched the current version (no new row was
    /// written). A sustained high rate indicates the UI is firing autosave ticks faster
    /// than the citizen is editing — the operator can tune the client-side debounce.
    /// Tagless: the observable rate alone is the operator signal.
    /// </summary>
    public static readonly Counter<long> ApplicationVersionDedup = Meter.CreateCounter<long>(
        name: "cnas.application_version.dedup",
        description: "Application autosave/save calls deduplicated against the current version (R0321).");

    /// <summary>
    /// R0321 / R0224 / UI 008 — the oldest
    /// <see cref="Cnas.Ps.Core.Domain.ApplicationVersionSource.Autosave"/> row hard-deleted
    /// by the per-application cap enforcer. Manual saves, submits, and reverts are NEVER
    /// pruned even when older. A sustained rate above zero is expected behaviour on any
    /// long-lived draft — the cap is doing its job.
    /// </summary>
    public static readonly Counter<long> ApplicationVersionAutosavePruned = Meter.CreateCounter<long>(
        name: "cnas.application_version.autosave_pruned",
        description: "Oldest autosave row hard-deleted by the per-application cap (R0321).");

    /// <summary>
    /// R0226 / TOR UI 013 — universal grid-export requests issued through
    /// <see cref="Cnas.Ps.Application.Exports.IGridExporter.ExportAsync"/>. Incremented
    /// once per call regardless of outcome (success, row-cap rejection, or
    /// missing-renderer rejection — operators chart all three to spot abuse
    /// patterns AND adoption of the export feature). Tagged with <c>grid</c>
    /// (canonical grid name, e.g. <c>Solicitants</c>) and <c>format</c>
    /// (<c>csv</c> | <c>xlsx</c> | <c>pdf</c>) so the registry-level export
    /// volume can be charted. Cardinality is bounded: grid names come from a
    /// closed allow-list (≤ 12 in practice) and formats from the
    /// <see cref="Cnas.Ps.Contracts.ExportFormat"/> enum (3 today).
    /// </summary>
    public static readonly Counter<long> GridExportRequested = Meter.CreateCounter<long>(
        name: "cnas.grid_export.requested",
        description: "Universal grid-export requests per grid and format (R0226).");

    /// <summary>
    /// R0529 / TOR CF 03.14 — one increment per successful report-export
    /// dispatch through
    /// <see cref="Cnas.Ps.Application.Reporting.IReportExportSelector.ExportAsync"/>.
    /// Tagged with <c>format</c> (<c>Csv</c> | <c>Xlsx</c> | <c>Docx</c> |
    /// <c>Pdf</c>) so the per-format export volume can be charted.
    /// Cardinality is bounded by the
    /// <see cref="Cnas.Ps.Contracts.ReportExportFormat"/> enum (4 today).
    /// </summary>
    public static readonly Counter<long> ReportExportGenerated = Meter.CreateCounter<long>(
        name: "cnas.report_export.generated",
        description: "Universal report-export bytes produced per format (R0529).");

    /// <summary>
    /// R0529 / TOR CF 03.14 — sum-style counter recording the rendered byte
    /// size of each successful report export. Tagged with <c>format</c>
    /// (<c>Csv</c> | <c>Xlsx</c> | <c>Docx</c> | <c>Pdf</c>); operators can
    /// chart the running byte volume per format and alert on a sudden
    /// spike (the closest signal we have to detecting export abuse without
    /// inspecting payloads). Cardinality matches
    /// <see cref="ReportExportGenerated"/>.
    /// </summary>
    public static readonly Counter<long> ReportExportSizeBytes = Meter.CreateCounter<long>(
        name: "cnas.report_export.size_bytes",
        description: "Sum of rendered report-export byte sizes per format (R0529).");

    /// <summary>
    /// R0201 / TOR CF 20.02 — one increment per <c>KpiSnapshotJob</c> fire.
    /// Tagged with <c>status</c> = <c>success</c> | <c>failure</c> so operators
    /// chart the per-run success rate and alert on a sustained failure burst.
    /// Cardinality is bounded to two values.
    /// </summary>
    public static readonly Counter<long> KpiSnapshotRun = Meter.CreateCounter<long>(
        name: "cnas.kpi.snapshot_run",
        description: "KPI snapshot-job fires; status tag records the outcome (R0201).");

    /// <summary>
    /// R0153 / TOR CF 19.05 — one increment per
    /// <c>ContributorPeriodProjectionService.RebuildAllAsync</c> fire (i.e. once
    /// per <c>ContributorPeriodProjectionJob</c> run AND once per admin-triggered
    /// batch via the API). Tagged with <c>outcome</c> = <c>success</c> |
    /// <c>failure</c> so operators chart the per-run success rate and alert on a
    /// sustained failure burst. Cardinality is bounded to two values.
    /// </summary>
    public static readonly Counter<long> ContributorProjectionRun = Meter.CreateCounter<long>(
        name: "cnas.etl.contributor_projection_run",
        description: "Contributor period-projection batch runs; outcome tag records the result (R0153).");

    /// <summary>
    /// R0153 / TOR CF 19.05 — count of <c>ContributorPeriodProjection</c> rows
    /// inserted by the projection service. Incremented per
    /// <c>RebuildForContributorAsync</c> call by the count of slices it
    /// produced (zero-increment is permitted — a contributor with no source
    /// rows projects to zero slices). Tagless: the observable rate alone is
    /// the operator signal.
    /// </summary>
    public static readonly Counter<long> ContributorProjectionSlices = Meter.CreateCounter<long>(
        name: "cnas.etl.contributor_projection_slices",
        description: "Contributor period-projection slices created per run (R0153).");

    /// <summary>
    /// R0583 / TOR CF 09.06 / CF 09.09 — one increment per
    /// <see cref="Cnas.Ps.Infrastructure.Jobs.ReportJobBackgroundJob"/> fire (i.e.
    /// once per Quartz tick that drains the report-job queue). Tagged with
    /// <c>outcome</c> = <c>success</c> | <c>failure</c> so operators chart the
    /// per-tick success rate and alert on a sustained failure burst. Cardinality
    /// is bounded to two values.
    /// </summary>
    public static readonly Counter<long> ReportJobRun = Meter.CreateCounter<long>(
        name: "cnas.report_job.run",
        description: "Background report-job runner ticks; outcome tag records the result (R0583).");

    /// <summary>
    /// R0228 / TOR SEC 033 — incremented once per request whose response carries any
    /// <see cref="Cnas.Ps.Contracts.Security.SensitivityLabel.Restricted"/> field.
    /// Tagged with <c>resource</c> (DTO type name, e.g. <c>InsuredPersonOutput</c>) so
    /// operators can chart which surfaces dominate Restricted disclosures. Cardinality
    /// is bounded: the resource name is the response DTO's type name (≤ a few dozen in
    /// practice).
    /// </summary>
    public static readonly Counter<long> SensitivityRestrictedAccess = Meter.CreateCounter<long>(
        name: "cnas.sensitivity.restricted_access",
        description: "Responses carrying Restricted fields, per response DTO (R0228).");

    /// <summary>
    /// R0701 / TOR CF 21.01-02 — incremented once per successful
    /// <c>IApplicationProcessingContextService.GetForCurrentUserAsync</c> call.
    /// One CNAS-staff dossier-open = one increment. Operators chart this against
    /// the <c>APPLICATION.PROCESSING_CONTEXT_VIEWED</c> audit-row rate to detect
    /// abnormal bulk dossier-open patterns that may indicate scraping. Untagged:
    /// the observable rate alone is the operator signal.
    /// </summary>
    public static readonly Counter<long> ApplicationProcessingContextLoaded = Meter.CreateCounter<long>(
        name: "cnas.application_processing.context_loaded",
        description: "Successful application processing-context aggregations (R0701).");

    /// <summary>
    /// R0210 / TOR UI 007 / CF 17.16 — incremented once per
    /// <c>ITranslationResolver.Resolve</c> call whose exact (code, language) lookup
    /// missed. Tagged with <c>language</c> (= the requested language code) and
    /// <c>code</c> (= the missing key code) so operators can chart which strings
    /// most often fall through to the RO fallback or the code-as-fallback. Cardinality
    /// is bounded in practice by the size of the translation-key registry × 3 language
    /// codes; the tagging stays cheap because the resolver only emits an increment on
    /// the cache-miss path (not on every render).
    /// </summary>
    public static readonly Counter<long> TranslationMiss = Meter.CreateCounter<long>(
        name: "cnas.translation.miss",
        description: "Translation lookups that fell through to the RO / code fallback (R0210).");

    /// <summary>
    /// R0305 / TOR Annex 1 — incremented once per Contributor lifecycle business-process
    /// invocation on the service layer (BP 1.1 register, BP 1.2 update, BP 1.3 deactivate,
    /// BP 1.4 reactivate, BP 1.5 merge, BP 1.6 split, BP 1.7 admin-correct,
    /// BP 1.8 reassign-branch, BP 1.9 mark-deceased-or-dissolved). Tagged with
    /// <c>bp</c> (= the canonical BP name) so operators can chart per-BP volume.
    /// Cardinality is bounded by the 9 BPs declared in Annex 1.
    /// </summary>
    public static readonly Counter<long> ContributorBpInvoked = Meter.CreateCounter<long>(
        name: "cnas.contributor.bp_invoked",
        description: "Contributor registry BP invocations per BP code (R0305).");

    /// <summary>
    /// R0535 / CF 04.07-08 — incremented once per malformed
    /// <c>UserProfile.LayoutPreferences</c> column observed by the read path. The
    /// service returns the dispatcher's
    /// <see cref="Cnas.Ps.Core.ValueObjects.UserLayoutPreferences.Default"/> on a parse
    /// failure (fail-open contract) AND increments this counter so operators can chart
    /// silent schema drift without spamming logs. Tagless: the observable rate alone
    /// is the operator signal.
    /// </summary>
    public static readonly Counter<long> UserLayoutParseFailure = Meter.CreateCounter<long>(
        name: "cnas.user_layout.parse_failure",
        description: "Malformed UserProfile.LayoutPreferences observed on read (R0535).");

    /// <summary>
    /// R0810 / R0811 / R0812 — declaration registrations tagged with the
    /// canonical <c>DeclarationKind</c> name (<c>Sfs</c>, <c>BassFour</c>, ...,
    /// <c>Other</c>). Cardinality is bounded by the 8 kinds enumerated in
    /// <c>DeclarationKind</c>.
    /// </summary>
    public static readonly Counter<long> DeclarationRegistered = Meter.CreateCounter<long>(
        name: "cnas.declaration.registered",
        description: "Declarations registered into the contributions registry per kind (R0810-R0812).");

    /// <summary>
    /// R0821 / TOR BP 1.2-L — scanned-copy attachments accepted by
    /// <c>IDeclarationService.AttachScannedCopyAsync</c>. Incremented once
    /// per successful upload + row flip; tagless because the observable rate
    /// alone is the operator signal that paper declarations are being
    /// digitised at the expected pace.
    /// </summary>
    public static readonly Counter<long> DeclarationScannedAttached = Meter.CreateCounter<long>(
        name: "cnas.declaration.scanned_attached",
        description: "Scanned-copy attachments accepted onto declaration rows (R0821).");

    /// <summary>
    /// R0813 — monthly contribution-calculator invocations tagged with
    /// <c>outcome</c> = <c>succeeded</c> | <c>failed</c>. Operators chart the
    /// per-payer batch rate and the failure share.
    /// </summary>
    public static readonly Counter<long> ContributorMonthlyCalc = Meter.CreateCounter<long>(
        name: "cnas.contributor.monthly_calc",
        description: "Monthly contribution-calculator invocations per outcome (R0813).");

    /// <summary>
    /// R0819 / TOR BP 1.2-J — late-payment-penalty-calculator invocations tagged
    /// with <c>outcome</c> = <c>succeeded</c> | <c>failed</c>. Operators chart
    /// the per-payer batch rate and the failure share.
    /// </summary>
    public static readonly Counter<long> LatePenaltyCalculated = Meter.CreateCounter<long>(
        name: "cnas.late_penalty.calculated",
        description: "Late-payment-penalty calculations per outcome (R0819).");

    /// <summary>
    /// R0820 / TOR BP 1.2-K — management-period-close completions. Incremented
    /// once per successful <c>IManagementPeriodService.CloseAsync</c> invocation.
    /// Tagless because the observable rate alone is the operator signal.
    /// </summary>
    public static readonly Counter<long> ManagementPeriodClosed = Meter.CreateCounter<long>(
        name: "cnas.management_period.closed",
        description: "Management-period close completions (R0820).");

    /// <summary>
    /// R0920 / TOR BP 2.3-A — labor-booklet registrations.
    /// Incremented once per successful <c>ILaborBookletService.RegisterAsync</c>
    /// invocation. Tagless because the observable rate alone is the operator signal.
    /// </summary>
    public static readonly Counter<long> LaborBookletRegistered = Meter.CreateCounter<long>(
        name: "cnas.labor_booklet.registered",
        description: "Labor-booklet master rows registered (R0920).");

    /// <summary>
    /// R0920 / TOR BP 2.3-A — labor-booklet verifications.
    /// Incremented once per successful <c>ILaborBookletService.VerifyAsync</c>
    /// invocation. Tagless because the observable rate alone is the operator signal.
    /// </summary>
    public static readonly Counter<long> LaborBookletVerified = Meter.CreateCounter<long>(
        name: "cnas.labor_booklet.verified",
        description: "Labor-booklet master rows verified (R0920).");

    /// <summary>
    /// R0920 / TOR BP 2.3-A — labor-booklet rejections.
    /// Incremented once per successful <c>ILaborBookletService.RejectAsync</c>
    /// invocation. Tagless because the observable rate alone is the operator signal.
    /// </summary>
    public static readonly Counter<long> LaborBookletRejected = Meter.CreateCounter<long>(
        name: "cnas.labor_booklet.rejected",
        description: "Labor-booklet master rows rejected (R0920).");

    /// <summary>
    /// R0921 / TOR BP 2.3-B — pre-1999 activity-period rows added.
    /// Incremented once per successful <c>ILaborBookletService.AddPeriodAsync</c>
    /// invocation.
    /// </summary>
    public static readonly Counter<long> Pre1999PeriodAdded = Meter.CreateCounter<long>(
        name: "cnas.pre1999_period.added",
        description: "Pre-1999 activity-period rows added (R0921).");

    /// <summary>
    /// R0921 / TOR BP 2.3-B — pre-1999 activity-period rows amended (R0301-style
    /// supersession). Incremented once per successful
    /// <c>ILaborBookletService.AmendPeriodAsync</c> invocation.
    /// </summary>
    public static readonly Counter<long> Pre1999PeriodAmended = Meter.CreateCounter<long>(
        name: "cnas.pre1999_period.amended",
        description: "Pre-1999 activity-period rows amended (R0921).");

    /// <summary>
    /// R0921 / TOR BP 2.3-B — pre-1999 activity-period rows closed. Incremented
    /// once per successful <c>ILaborBookletService.ClosePeriodAsync</c>
    /// invocation.
    /// </summary>
    public static readonly Counter<long> Pre1999PeriodClosed = Meter.CreateCounter<long>(
        name: "cnas.pre1999_period.closed",
        description: "Pre-1999 activity-period rows closed (R0921).");

    /// <summary>
    /// R0910 / TOR BP 2.2-A — REV-5 declaration registrations tagged with
    /// <c>outcome</c> = <c>success</c> | <c>failed</c>. Operators chart the
    /// per-employer batch rate and the failure share.
    /// </summary>
    public static readonly Counter<long> Rev5Registered = Meter.CreateCounter<long>(
        name: "cnas.rev5.registered",
        description: "REV-5 declarations registered per outcome (R0910).");

    /// <summary>
    /// R0910 — count of REV-5 child rows whose IDNP hash could not be resolved
    /// to a known Solicitant at registration time. Tagless because the
    /// observable rate alone is the operator signal — chart against
    /// <see cref="Rev5Registered"/> to compute a unmatched-share ratio.
    /// </summary>
    public static readonly Counter<long> Rev5RowsUnmatched = Meter.CreateCounter<long>(
        name: "cnas.rev5.rows_unmatched",
        description: "REV-5 rows whose IDNP hash did not resolve to a Solicitant (R0910).");

    /// <summary>
    /// R0913 / TOR BP 2.2-D — per-insured-person contribution adjustments
    /// applied from non-REV-5 supporting documents. Tagged with
    /// <c>source_document_code</c> = <c>CourtDecision</c> | <c>AdminControl</c>
    /// | <c>IndividualContract</c> | <c>Other</c>. Bounded cardinality (4 codes).
    /// </summary>
    public static readonly Counter<long> InsuredPersonAdjustmentApplied = Meter.CreateCounter<long>(
        name: "cnas.insured_person.adjustment_applied",
        description: "Per-insured-person contribution adjustments applied (R0913).");

    /// <summary>
    /// R0911 / TOR BP 2.2-B — Treasury payment receipts distributed by the
    /// background job. Tagged with <c>outcome</c> = <c>distributed</c> |
    /// <c>partial</c> | <c>failed</c>. Operators chart the per-outcome rate +
    /// the partial/failed share to gauge feed quality.
    /// </summary>
    public static readonly Counter<long> TreasuryDistributed = Meter.CreateCounter<long>(
        name: "cnas.treasury.distributed",
        description: "Treasury payment receipts distributed per outcome (R0911).");

    /// <summary>
    /// R0831 / TOR BP 1.3-B — claim registrations tagged with the canonical
    /// <c>Cnas.Ps.Core.Domain.ClaimKind</c> enum name (<c>Contribution</c>,
    /// <c>LatePenalty</c>, <c>AdminFine</c>, <c>Court</c>, <c>Other</c>).
    /// Cardinality is bounded by the five kinds enumerated.
    /// </summary>
    public static readonly Counter<long> ClaimRegistered = Meter.CreateCounter<long>(
        name: "cnas.claim.registered",
        description: "Claims registered into the creanțe registry per kind (R0831).");

    /// <summary>
    /// R0832 / TOR BP 1.3-C — claim-payment applications tagged with
    /// <c>outcome</c> = <c>partial</c> | <c>settled</c>. Operators chart the
    /// per-outcome rate to track the rate at which claims close out vs.
    /// accumulate further. Cardinality is bounded to two values.
    /// </summary>
    public static readonly Counter<long> ClaimPaymentApplied = Meter.CreateCounter<long>(
        name: "cnas.claim.payment_applied",
        description: "Claim-payment applications per outcome (R0832).");

    /// <summary>
    /// R0814 / TOR BP 1.2-E — BASS refund lifecycle transitions tagged with
    /// <c>status</c> = the canonical
    /// <c>Cnas.Ps.Core.Domain.BassRefundStatus</c> enum name (e.g.
    /// <c>Requested</c>, <c>Approved</c>, <c>IssuedToTreasury</c>,
    /// <c>Confirmed</c>, <c>Cancelled</c>). Cardinality is bounded by the
    /// five enum values.
    /// </summary>
    public static readonly Counter<long> BassRefund = Meter.CreateCounter<long>(
        name: "cnas.bass.refund",
        description: "BASS refund lifecycle transitions per status (R0814).");

    /// <summary>
    /// R0815 / TOR BP 1.2-F — Treasury-payment corrections applied tagged
    /// with <c>kind</c> = the canonical
    /// <c>Cnas.Ps.Core.Domain.PaymentCorrectionKind</c> enum name (e.g.
    /// <c>Reverse</c>, <c>RedirectToPayer</c>, <c>RedirectToMonth</c>,
    /// <c>AdjustAmount</c>). Incremented when a correction transitions to
    /// <c>Applied</c>. Cardinality is bounded by the four enum values.
    /// </summary>
    public static readonly Counter<long> PaymentCorrected = Meter.CreateCounter<long>(
        name: "cnas.payment.corrected",
        description: "Treasury-payment corrections applied per kind (R0815).");

    /// <summary>
    /// R0817 / TOR BP 1.2-H — penalty-repayment-plan lifecycle transitions
    /// tagged with <c>outcome</c> = <c>created</c> | <c>installment_paid</c> |
    /// <c>completed</c> | <c>defaulted</c> | <c>cancelled</c>. Operators chart
    /// the per-outcome rate to gauge how staggered-repayment is adopted vs.
    /// how often plans default. Cardinality bounded by five outcome values.
    /// </summary>
    public static readonly Counter<long> PenaltyPlan = Meter.CreateCounter<long>(
        name: "cnas.penalty.plan",
        description: "Penalty-repayment-plan lifecycle transitions per outcome (R0817).");

    /// <summary>
    /// R0818 / TOR BP 1.2-I — daily BASS-receipts summary job invocations
    /// tagged with <c>outcome</c> = <c>executed</c>. Operators chart the
    /// daily run-rate to confirm the summary actually fired (a flat line
    /// means the scheduler is wedged).
    /// </summary>
    public static readonly Counter<long> BassDailySummary = Meter.CreateCounter<long>(
        name: "cnas.bass.daily_summary",
        description: "Daily BASS-receipts summary job invocations per outcome (R0818).");

    /// <summary>
    /// R2173 / TOR PSR 004 — peak-hour gate decisions emitted by
    /// <c>IPeakHourGate.EvaluateAsync</c>. Tagged with <c>decision</c> =
    /// <c>allow</c> | <c>skip</c> so operators can chart how often heavy
    /// maintenance jobs are deferred during peak hours, and confirm that
    /// the gate is firing for every job (a flat line on the
    /// <c>allow</c> series means the Quartz scheduler stopped firing the
    /// background fleet). Cardinality is bounded to two values.
    /// </summary>
    public static readonly Counter<long> PeakHourGate = Meter.CreateCounter<long>(
        name: "cnas.peak_hour.gate",
        description: "Peak-hour gate decisions per outcome (R2173).");

    /// <summary>
    /// R0671 continuation / TOR CF 18.06 — access-scope back-fill rows emitted by
    /// <c>IAccessScopeBackfillService</c>. Tagged with <c>kind</c> =
    /// <c>Solicitant</c> | <c>ServiceApplication</c> so operators can chart
    /// back-fill volume per axis. The value is the number of rows updated by the
    /// call (NOT a per-call increment of 1) so a single dashboard panel surfaces
    /// the cumulative back-fill throughput.
    /// </summary>
    public static readonly Counter<long> AccessScopeBackfilled = Meter.CreateCounter<long>(
        name: "cnas.access_scope.backfilled",
        description: "Rows updated by the access-scope back-fill helper, tagged by kind.");

    /// <summary>
    /// Registers an observable gauge that samples the audit-write queue depth on each
    /// OTel export interval. Called once at DI composition time with the singleton
    /// <see cref="AuditWriteQueue"/> instance.
    /// </summary>
    /// <remarks>
    /// The reader's <c>Count</c> property is supported because the channel is
    /// <c>Channel.CreateBounded</c> — see <see cref="AuditWriteQueue"/> remarks. The
    /// gauge callback runs on the OTel collection thread; reading the count is
    /// thread-safe and constant-time.
    /// </remarks>
    /// <param name="queue">Singleton audit queue whose depth is reported.</param>
    public static void RegisterAuditQueueDepthGauge(AuditWriteQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        Meter.CreateObservableGauge<long>(
            name: "cnas.audit.queue.depth",
            observeValue: () => queue.Reader.Count,
            description: "Current audit-write queue depth.");
    }

    /// <summary>
    /// Registers an observable gauge that samples the archive directory size (count of
    /// pending replay files) on each OTel export interval. The supplied callback MUST
    /// guard against I/O exceptions internally so the gauge thread never throws.
    /// </summary>
    /// <param name="safeRead">Failure-tolerant reader returning the current file count.</param>
    public static void RegisterAuditArchiveSizeGauge(Func<long> safeRead)
    {
        ArgumentNullException.ThrowIfNull(safeRead);
        Meter.CreateObservableGauge<long>(
            name: "cnas.audit.archive.size",
            observeValue: safeRead,
            description: "Count of files in the audit archive (pending replays).");
    }

    /// <summary>
    /// Registers an observable gauge for the pending admin-action backlog. The supplied
    /// callback MUST be non-blocking — the gauge thread cannot tolerate scoped DB queries.
    /// Composition wires it to a background updater's cached <c>long</c>.
    /// </summary>
    /// <param name="safeRead">Non-blocking reader returning the cached backlog.</param>
    public static void RegisterAdminActionBacklogGauge(Func<long> safeRead)
    {
        ArgumentNullException.ThrowIfNull(safeRead);
        Meter.CreateObservableGauge<long>(
            name: "cnas.admin.action.backlog",
            observeValue: safeRead,
            description: "Pending admin actions not yet expired or decided.");
    }

    /// <summary>
    /// R0634 / R1702-R1708 / TOR CF 14.12 / Annex 4 — incremented once per
    /// Annex-4 interop-op invocation. Tagged with <c>op_name</c> (= the
    /// canonical Annex-4 op identifier, e.g. <c>GetActiveDecisions</c>,
    /// <c>GetPaymentStatus</c>, ...) so operators can chart per-op volume
    /// and alert on abnormal traffic patterns from external consumers
    /// (RSP, MoFin, IPS, SIVE, SIAAS). Cardinality is bounded by the
    /// closed Annex-4 op set (≤ 12 in practice).
    /// </summary>
    public static readonly Counter<long> InteropOpInvoked = Meter.CreateCounter<long>(
        name: "cnas.interop.op_invoked",
        description: "Annex-4 interop-op invocations tagged with op_name (R0634 / R1702-R1708).");

    /// <summary>
    /// R1600 / TOR Annex 3.8 — incremented once per successful executory-
    /// document registration. Untagged: the observable rate alone is the
    /// operator signal that the registry is being populated at the expected
    /// pace.
    /// </summary>
    public static readonly Counter<long> ExecutoryDocumentRegistered = Meter.CreateCounter<long>(
        name: "cnas.executory_doc.registered",
        description: "Executory documents registered into the registry (R1600).");

    /// <summary>
    /// R1406 / TOR §3.6-G — incremented once per non-zero allocation row
    /// committed by <c>IUnemploymentBenefitWithholdingApplier.CommitPlanAsync</c>.
    /// Tagged with <c>priority_rank</c> (1..5) so operators can chart per-
    /// priority withholding volume and detect lower-priority rows being
    /// systematically starved. Cardinality is bounded to 5.
    /// </summary>
    public static readonly Counter<long> ExecutoryDocumentWithholdingApplied = Meter.CreateCounter<long>(
        name: "cnas.executory_doc.withholding_applied",
        description: "Per-priority withholding allocations committed against executory documents (R1406).");

    /// <summary>
    /// R1406 / TOR §3.6-G — incremented once per plan that hit the 70% cap
    /// (at least one row got <c>Rationale = CAP_EXCEEDED</c>). A sustained
    /// rate above zero indicates beneficiaries with multiple high-priority
    /// debts, an operationally-relevant signal for the registry team.
    /// </summary>
    public static readonly Counter<long> ExecutoryDocumentCapExceeded = Meter.CreateCounter<long>(
        name: "cnas.executory_doc.cap_exceeded",
        description: "Executory-document withholding plans that hit the 70% cap (R1406).");

    /// <summary>
    /// R2270 / TOR SEC 023-024 — incremented once per successful user-group
    /// creation. Tagless — the observable rate alone is the operator signal
    /// that the registry is being populated at the expected pace.
    /// </summary>
    public static readonly Counter<long> UserGroupCreated = Meter.CreateCounter<long>(
        name: "cnas.user_group.created",
        description: "User-group registry rows created (R2270).");

    /// <summary>
    /// R2270 / TOR SEC 023-024 — incremented once per add-child request that
    /// the service rejected because it would create a cycle (self-loop or
    /// transitive cycle). A sustained rate above zero indicates an admin
    /// flow that is letting users try to compose ill-formed hierarchies and
    /// is therefore an operator-actionable signal. Tagless: any non-zero
    /// rate is interesting.
    /// </summary>
    public static readonly Counter<long> UserGroupHierarchyCycleAttempted = Meter.CreateCounter<long>(
        name: "cnas.user_group.hierarchy_cycle_attempted",
        description: "User-group add-child requests rejected due to a cycle (R2270).");

    /// <summary>
    /// R2270 / TOR SEC 023-024 — incremented once per
    /// <c>IUserGroupRoleResolver.ResolveEffectiveRolesAsync</c> call.
    /// Tagged with <c>cache_hit</c> = <c>true</c> | <c>false</c> so the
    /// follow-up caching layer (out of scope for this iteration) can be
    /// measured without a schema change. Today the counter always reports
    /// <c>cache_hit=false</c>.
    /// </summary>
    public static readonly Counter<long> UserGroupRoleResolved = Meter.CreateCounter<long>(
        name: "cnas.user_group.role_resolved",
        description: "User-group transitive role resolutions, tagged by cache_hit (R2270).");

    /// <summary>
    /// R2274 / TOR SEC 028 — incremented once per
    /// <c>IAccessRightsReportService</c> projection invocation. Tagged with
    /// <c>report_kind</c> = <c>by_user</c> | <c>by_role</c> | <c>by_group</c>
    /// | <c>csv_by_role</c> | <c>csv_full_matrix</c> so operators can chart
    /// the per-projection rate independently and confirm CSV exports are
    /// proportionate to interactive lookups. Cardinality is bounded to five.
    /// </summary>
    public static readonly Counter<long> AccessRightsReportGenerated = Meter.CreateCounter<long>(
        name: "cnas.access_rights.report_generated",
        description: "Access-rights report generations per projection kind (R2274).");

    /// <summary>
    /// R2274 / TOR SEC 028 — total rows returned across all
    /// <c>IAccessRightsReportService</c> projections, tagged with
    /// <c>report_kind</c> identically to
    /// <see cref="AccessRightsReportGenerated"/>. Operators chart this
    /// against the call counter to compute the average rows-per-call and
    /// detect runaway full-matrix exports.
    /// </summary>
    public static readonly Counter<long> AccessRightsReportRowsReturned = Meter.CreateCounter<long>(
        name: "cnas.access_rights.report_rows_returned",
        description: "Rows returned by access-rights report projections (R2274).");

    /// <summary>
    /// R2282 / TOR SEC 036 — integrity-check runs finalised by the
    /// <c>IntegrityCheckJob</c> or the manual-trigger entry point. Tagged
    /// with <c>status</c> = <c>completed</c> | <c>failed</c> so operators
    /// can chart per-run success rate and alert on sustained failure bursts.
    /// Cardinality is bounded to two values.
    /// </summary>
    public static readonly Counter<long> IntegrityCheckRunCompleted = Meter.CreateCounter<long>(
        name: "cnas.integrity_check.run_completed",
        description: "Integrity-check runs finalised, per status (R2282).");

    /// <summary>
    /// R2282 / TOR SEC 036 — integrity-check findings recorded by the job.
    /// Tagged with <c>severity</c> = <c>Critical</c> | <c>High</c> |
    /// <c>Medium</c> | <c>Low</c> so operators can spot Critical bursts and
    /// drive the per-severity dashboard. Cardinality is bounded to four
    /// values.
    /// </summary>
    public static readonly Counter<long> IntegrityCheckFindingsRecorded = Meter.CreateCounter<long>(
        name: "cnas.integrity_check.findings_recorded",
        description: "Integrity-check findings recorded per severity (R2282).");

    /// <summary>
    /// R2282 / TOR SEC 036 — rows scanned by the integrity-check pipeline
    /// per run. Untagged: operators chart this against the per-run
    /// completion rate to compute throughput.
    /// </summary>
    public static readonly Counter<long> IntegrityCheckRowsScanned = Meter.CreateCounter<long>(
        name: "cnas.integrity_check.rows_scanned",
        description: "Rows scanned by the integrity-check pipeline per run (R2282).");

    /// <summary>
    /// R1503 / TOR §3.7-D — incremented once per successful
    /// <c>ILegalChangeEventService.RegisterAsync</c> call. Untagged: the
    /// observable rate is the operator signal that operators are configuring
    /// legal-change events at the expected pace.
    /// </summary>
    public static readonly Counter<long> LegalChangeEventRegistered = Meter.CreateCounter<long>(
        name: "cnas.mass_recalc.legal_change_event_registered",
        description: "Legal-change events registered (R1503).");

    /// <summary>
    /// R1503 / TOR §3.7-D — incremented once per mass-recalculation run START.
    /// Tagged with <c>mode</c> (<c>DryRun</c> / <c>Apply</c>) so operators can
    /// chart per-mode start rate. Cardinality bounded by 2.
    /// </summary>
    public static readonly Counter<long> MassRecalculationRunStarted = Meter.CreateCounter<long>(
        name: "cnas.mass_recalc.run_started",
        description: "Mass-recalculation runs started, tagged by mode (R1503).");

    /// <summary>
    /// R1503 / TOR §3.7-D — incremented once per per-decision result row
    /// finalised by the orchestrator. Tagged with <c>mode</c> and <c>status</c>
    /// (e.g. <c>DryRun/Computed</c>, <c>Apply/Skipped</c>) so operators can
    /// chart per-mode per-outcome volume. Cardinality bounded by 2 × 5 = 10.
    /// </summary>
    public static readonly Counter<long> MassRecalculationDecisionProcessed = Meter.CreateCounter<long>(
        name: "cnas.mass_recalc.decision_processed",
        description: "Per-decision outcomes recorded by the mass-recalc engine (R1503).");

    /// <summary>
    /// R1503 / TOR §3.7-D — incremented once per mass-recalculation run END.
    /// Tagged with <c>mode</c> and <c>status</c> (<c>Completed</c> /
    /// <c>Failed</c>) so operators can chart per-mode success rate.
    /// Cardinality bounded by 2 × 2 = 4.
    /// </summary>
    public static readonly Counter<long> MassRecalculationRunCompleted = Meter.CreateCounter<long>(
        name: "cnas.mass_recalc.run_completed",
        description: "Mass-recalculation runs finalised, tagged by mode + status (R1503).");

    /// <summary>
    /// R1906 / TOR Annex 6 — incremented once per successful
    /// <c>IReportDistributionService.CreateRuleAsync</c> call. Untagged: the
    /// observable rate alone is the operator signal that admins are configuring
    /// distribution rules at the expected pace.
    /// </summary>
    public static readonly Counter<long> ReportDistributionRuleCreated = Meter.CreateCounter<long>(
        name: "cnas.report_distribution.rule_created",
        description: "Per-report distribution rules created (R1906).");

    /// <summary>
    /// R1906 / TOR Annex 6 — incremented once per dispatch ATTEMPT made by
    /// <c>IReportDistributionDispatcher</c>. Tagged with <c>channel</c>
    /// (= <c>InSystem</c> | <c>Dashboard</c> | <c>Email</c> | <c>MNotify</c>) so
    /// operators can chart per-channel attempt volume. Cardinality bounded by 4.
    /// </summary>
    public static readonly Counter<long> ReportDistributionDispatchAttempted = Meter.CreateCounter<long>(
        name: "cnas.report_distribution.dispatch_attempted",
        description: "Per-rule dispatch attempts, tagged by channel (R1906).");

    /// <summary>
    /// R1906 / TOR Annex 6 — incremented once per dispatch OUTCOME row written.
    /// Tagged with <c>channel</c> and <c>status</c> (= <c>Delivered</c> |
    /// <c>Failed</c> | <c>Skipped</c>) so operators can chart per-channel
    /// success share. Cardinality bounded by 4 × 3 = 12.
    /// </summary>
    public static readonly Counter<long> ReportDistributionDispatchOutcome = Meter.CreateCounter<long>(
        name: "cnas.report_distribution.dispatch_outcome",
        description: "Per-rule dispatch outcomes, tagged by channel and status (R1906).");

    /// <summary>
    /// R1710 / TOR INT 002 — incremented once per <c>SubmitAsync</c> success.
    /// Tagged with <c>op_code</c> (stable enum-name of the targeted Annex-4 op)
    /// so operators can chart per-op upload volume. Cardinality bounded by the
    /// closed Annex-4 op set (≤ 12 in practice).
    /// </summary>
    public static readonly Counter<long> OfflineBatchSubmitted = Meter.CreateCounter<long>(
        name: "cnas.offline_batch.submitted",
        description: "Offline-batch submissions accepted, tagged by op_code (R1710).");

    /// <summary>
    /// R1710 / TOR INT 002 — incremented once per row finalised by the
    /// offline-batch processor. Tagged with <c>op_code</c> (Annex-4 op) and
    /// <c>status</c> (<c>Succeeded</c> | <c>Failed</c>) so operators can
    /// chart per-op success share. Cardinality bounded by 11 × 2 = 22.
    /// </summary>
    public static readonly Counter<long> OfflineBatchRowProcessed = Meter.CreateCounter<long>(
        name: "cnas.offline_batch.row_processed",
        description: "Offline-batch rows finalised, tagged by op_code and status (R1710).");

    /// <summary>
    /// R1710 / TOR INT 002 — incremented once per processor-finalised
    /// submission. Tagged with <c>op_code</c> and <c>terminal_status</c>
    /// (<c>Completed</c> | <c>Failed</c>). Cardinality bounded by 11 × 2 = 22.
    /// </summary>
    public static readonly Counter<long> OfflineBatchCompleted = Meter.CreateCounter<long>(
        name: "cnas.offline_batch.completed",
        description: "Offline-batch submissions finalised, tagged by op_code and terminal_status (R1710).");

    /// <summary>
    /// R1810 / TOR BP 1.2-I — incremented once per Treasury feed import START.
    /// Tagged with <c>trigger_kind</c> (<c>Scheduled</c> | <c>Manual</c>) so
    /// operators chart per-trigger run rates. Cardinality bounded to 2.
    /// </summary>
    public static readonly Counter<long> TreasuryFeedImportStarted = Meter.CreateCounter<long>(
        name: "cnas.treasury_feed.import_started",
        description: "Treasury feed imports started, tagged by trigger_kind (R1810).");

    /// <summary>
    /// R1810 / TOR BP 1.2-I — incremented once per parsed row finalised by
    /// the Treasury feed importer. Tagged with <c>status</c>
    /// (<c>Imported</c> | <c>Updated</c> | <c>Skipped</c> | <c>Failed</c>)
    /// so operators chart per-outcome share. Cardinality bounded to 4.
    /// </summary>
    public static readonly Counter<long> TreasuryFeedRowProcessed = Meter.CreateCounter<long>(
        name: "cnas.treasury_feed.row_processed",
        description: "Treasury feed rows finalised by the importer, tagged by status (R1810).");

    /// <summary>
    /// R1810 / TOR BP 1.2-I — incremented once per Treasury feed import END.
    /// Tagged with <c>terminal_status</c> (<c>Completed</c> | <c>Failed</c>)
    /// and <c>trigger_kind</c>. Cardinality bounded to 2 × 2 = 4.
    /// </summary>
    public static readonly Counter<long> TreasuryFeedImportCompleted = Meter.CreateCounter<long>(
        name: "cnas.treasury_feed.import_completed",
        description: "Treasury feed imports finalised, tagged by terminal_status and trigger_kind (R1810).");

    /// <summary>
    /// R1202 / TOR §3.4-C — incremented once per successful capitalised-payment
    /// request creation. Untagged; the observable rate alone signals that the
    /// registry is being populated at the expected pace.
    /// </summary>
    public static readonly Counter<long> CapitalisedPaymentRequested = Meter.CreateCounter<long>(
        name: "cnas.capitalised_payment.requested",
        description: "Capitalised-payment requests created in the registry (R1202).");

    /// <summary>
    /// R1202 / TOR §3.4-C — incremented once per successful present-value
    /// computation. Tagged with <c>obligation_kind</c> (3 values) so operators
    /// can chart per-obligation volume.
    /// </summary>
    public static readonly Counter<long> CapitalisedPaymentComputed = Meter.CreateCounter<long>(
        name: "cnas.capitalised_payment.computed",
        description: "Capitalised-payment present-value computations completed, tagged by obligation_kind (R1202).");

    /// <summary>
    /// R1202 / TOR §3.4-C — incremented once per terminal decision outcome.
    /// Tagged with <c>obligation_kind</c> (3 values) and <c>outcome</c>
    /// (<c>approved</c> / <c>rejected</c> / <c>cancelled</c> / <c>settled</c>).
    /// Cardinality bounded to 3 × 4 = 12.
    /// </summary>
    public static readonly Counter<long> CapitalisedPaymentDecisionOutcome = Meter.CreateCounter<long>(
        name: "cnas.capitalised_payment.decision_outcome",
        description: "Capitalised-payment terminal outcomes, tagged by obligation_kind and outcome (R1202).");

    /// <summary>
    /// R1403 / TOR §3.6-D — incremented once per athlete-pension award
    /// request creation. Untagged; the observable rate alone signals that
    /// the registry is being populated at the expected pace.
    /// </summary>
    public static readonly Counter<long> AthletePensionRequested = Meter.CreateCounter<long>(
        name: "cnas.athlete_pension.requested",
        description: "Athlete-pension awards created in the registry (R1403).");

    /// <summary>
    /// R1403 / TOR §3.6-D — incremented once per eligibility evaluation
    /// run. Tagged with <c>outcome</c> (<c>eligible</c> / <c>ineligible</c>);
    /// cardinality bounded to 2.
    /// </summary>
    public static readonly Counter<long> AthletePensionEligibilityEvaluated = Meter.CreateCounter<long>(
        name: "cnas.athlete_pension.eligibility_evaluated",
        description: "Athlete-pension eligibility evaluations, tagged by outcome (R1403).");

    /// <summary>
    /// R1403 / TOR §3.6-D — incremented once per terminal lifecycle
    /// transition. Tagged with <c>outcome</c> (<c>approved</c> /
    /// <c>rejected</c> / <c>activated</c> / <c>suspended</c> /
    /// <c>resumed</c> / <c>terminated</c>); cardinality bounded to 6.
    /// </summary>
    public static readonly Counter<long> AthletePensionDecisionOutcome = Meter.CreateCounter<long>(
        name: "cnas.athlete_pension.decision_outcome",
        description: "Athlete-pension lifecycle outcomes, tagged by outcome (R1403).");

    /// <summary>
    /// R1201 / R1402 / TOR §3.4-B / §3.6-C — incremented once per successful
    /// international-agreements routing-case creation. Tagged with
    /// <c>benefit_kind</c>; cardinality bounded by the
    /// <c>IntlAgreementBenefitKind</c> enum (currently 2 values).
    /// </summary>
    public static readonly Counter<long> IntlAgreementCaseCreated = Meter.CreateCounter<long>(
        name: "cnas.intl_agreement.case_created",
        description: "International-agreements routing cases created, tagged by benefit_kind (R1201/R1402).");

    /// <summary>
    /// R1201 / R1402 / TOR §3.4-B / §3.6-C — incremented once per
    /// routing-case submit (Draft → AtLocalReview). Tagged with
    /// <c>benefit_kind</c>.
    /// </summary>
    public static readonly Counter<long> IntlAgreementCaseSubmitted = Meter.CreateCounter<long>(
        name: "cnas.intl_agreement.case_submitted",
        description: "International-agreements routing cases submitted to level-1 review, tagged by benefit_kind (R1201/R1402).");

    /// <summary>
    /// R1201 / R1402 / TOR §3.4-B / §3.6-C — incremented once per
    /// reviewer decision recorded at any level. Tagged with
    /// <c>benefit_kind</c>, <c>level</c> (Local / Regional / National),
    /// and <c>outcome</c> (Approved / Rejected / RevisionRequested).
    /// </summary>
    public static readonly Counter<long> IntlAgreementReviewRecorded = Meter.CreateCounter<long>(
        name: "cnas.intl_agreement.review_recorded",
        description: "International-agreements reviewer decisions recorded, tagged by benefit_kind, level, outcome (R1201/R1402).");

    /// <summary>
    /// R1201 / R1402 / TOR §3.4-B / §3.6-C — incremented once per
    /// terminal lifecycle transition. Tagged with <c>benefit_kind</c> and
    /// <c>terminal_status</c> (Approved / Rejected / Cancelled).
    /// </summary>
    public static readonly Counter<long> IntlAgreementCaseFinalised = Meter.CreateCounter<long>(
        name: "cnas.intl_agreement.case_finalised",
        description: "International-agreements routing cases reached a terminal status, tagged by benefit_kind, terminal_status (R1201/R1402).");

    /// <summary>
    /// R2279 / TOR SEC 033 — incremented once per successful classification-catalog
    /// snapshot capture. Tagged with <c>trigger_kind</c>
    /// (<c>Scheduled</c> / <c>Manual</c>); cardinality bounded to 2.
    /// </summary>
    public static readonly Counter<long> ClassificationSnapshotCaptured = Meter.CreateCounter<long>(
        name: "cnas.classification.snapshot_captured",
        description: "Classification-catalog snapshots captured, tagged by trigger_kind (R2279).");

    /// <summary>
    /// R2279 / TOR SEC 033 — incremented once per detected drift finding.
    /// Tagged with <c>drift_kind</c> (<c>Added</c> / <c>Removed</c> /
    /// <c>LabelChanged</c> / <c>ClassificationLost</c>); cardinality bounded to 4.
    /// </summary>
    public static readonly Counter<long> ClassificationDriftDetected = Meter.CreateCounter<long>(
        name: "cnas.classification.drift_detected",
        description: "Classification drift findings detected, tagged by drift_kind (R2279).");

    /// <summary>
    /// R2279 / TOR SEC 033 — incremented once per drift finding acknowledged by
    /// an operator. Untagged — the observable rate alone signals that operators
    /// are working through the backlog.
    /// </summary>
    public static readonly Counter<long> ClassificationDriftAcknowledged = Meter.CreateCounter<long>(
        name: "cnas.classification.drift_acknowledged",
        description: "Classification drift findings acknowledged by operators (R2279).");

    /// <summary>
    /// R1904 / ARH 025 — incremented once per dataset-materialisation entry
    /// inside <c>ReportingService</c> (stock-five + Annex 6 / 6b / ... / 6j).
    /// Tagged with <c>db_context</c> = <c>read_replica</c> so operators can
    /// confirm that long-running report aggregations are landing on the
    /// Postgres streaming-replication follower and not on the primary backend.
    /// Cardinality is bounded to one (the tag is a constant emitted by the
    /// marker pattern in <c>ReportingService</c>); future hybrid services would
    /// emit <c>db_context=primary</c> as a second value.
    /// </summary>
    public static readonly Counter<long> ReportingServiceQueryExecuted = Meter.CreateCounter<long>(
        name: "cnas.reporting.query_executed",
        description: "ReportingService dataset materialisations, tagged by db_context (R1904).");

    /// <summary>
    /// R2271 / TOR SEC 025 — ABAC rule sets created via the registry service.
    /// Untagged — the observable rate alone signals administrative activity.
    /// </summary>
    public static readonly Counter<long> AbacRuleSetCreated = Meter.CreateCounter<long>(
        name: "cnas.abac.rule_set.created",
        description: "ABAC rule sets created by administrators (R2271 / SEC 025).");

    /// <summary>
    /// R2271 / TOR SEC 025 — ABAC rules appended to a rule set. Tagged with
    /// <c>effect</c> = <c>Allow</c> | <c>Deny</c> so operators can chart the
    /// shape of new policies. Cardinality bounded to 2.
    /// </summary>
    public static readonly Counter<long> AbacRuleAdded = Meter.CreateCounter<long>(
        name: "cnas.abac.rule.added",
        description: "ABAC rules added to a rule set, tagged by effect (R2271 / SEC 025).");

    /// <summary>
    /// R2271 / TOR SEC 025 — ABAC decisions returned to callers. Tagged with
    /// <c>policy_name</c> and <c>effect</c> so operators can confirm which
    /// policies are being exercised and the proportion of Allow / Deny verdicts.
    /// Cardinality bounded by the registered policy set times 2.
    /// </summary>
    public static readonly Counter<long> AbacDecisionEvaluated = Meter.CreateCounter<long>(
        name: "cnas.abac.decision.evaluated",
        description: "ABAC decisions returned to callers, tagged by policy_name + effect (R2271 / SEC 025).");

    /// <summary>
    /// R2271 / TOR SEC 025 — per-rule evaluation errors (parse or runtime
    /// failure). Tagged with <c>policy_name</c> so operators can pin malformed
    /// rules to their owning rule set. The rule itself is treated as
    /// non-matching — a malformed rule MUST NEVER silently grant access.
    /// </summary>
    public static readonly Counter<long> AbacRuleEvalError = Meter.CreateCounter<long>(
        name: "cnas.abac.rule.eval_error",
        description: "ABAC rule evaluation errors (parse / runtime); rule treated as non-matching (R2271 / SEC 025).");

    /// <summary>
    /// R2430 / TOR M4 — incremented once per successful migration-plan
    /// creation. Untagged — the observable rate alone signals operator activity.
    /// </summary>
    public static readonly Counter<long> MigrationPlanCreated = Meter.CreateCounter<long>(
        name: "cnas.migration.plan_created",
        description: "Migration plans created via the admin registry (R2430).");

    /// <summary>
    /// R2430 / R2431 / TOR M4 — incremented once per migration run STARTED.
    /// Tagged with <c>trigger_kind</c> (Scheduled / Manual / DryRun) and
    /// <c>target_entity</c> (plan's TargetEntityName, bounded by the plan
    /// registry).
    /// </summary>
    public static readonly Counter<long> MigrationRunStarted = Meter.CreateCounter<long>(
        name: "cnas.migration.run_started",
        description: "Migration runs started, tagged by trigger_kind + target_entity (R2430 / R2431).");

    /// <summary>
    /// R2431 / TOR M4 — incremented once per source row finalised by the
    /// importer. Tagged with <c>target_entity</c> + <c>outcome</c>
    /// (imported / updated / skipped / failed).
    /// </summary>
    public static readonly Counter<long> MigrationRowProcessed = Meter.CreateCounter<long>(
        name: "cnas.migration.row_processed",
        description: "Migration rows finalised by the importer, tagged by target_entity + outcome (R2431).");

    /// <summary>
    /// R2430 / R2431 / TOR M4 — incremented once per migration run END.
    /// Tagged with <c>target_entity</c> + <c>terminal_status</c>
    /// (Completed / CompletedWithErrors / Failed / Cancelled).
    /// </summary>
    public static readonly Counter<long> MigrationRunCompleted = Meter.CreateCounter<long>(
        name: "cnas.migration.run_completed",
        description: "Migration runs finalised, tagged by target_entity + terminal_status (R2430 / R2431).");

    /// <summary>
    /// R2433 / TOR M4 — incremented once per reconciliation report
    /// computed. Tagged with <c>target_entity</c> + <c>status</c>
    /// (Passed / Discrepancy / Failed).
    /// </summary>
    public static readonly Counter<long> MigrationReconciliationOutcome = Meter.CreateCounter<long>(
        name: "cnas.migration.reconciliation_outcome",
        description: "Migration reconciliation outcomes, tagged by target_entity + status (R2433).");

    /// <summary>
    /// R2307 / TOR SEC 060 — incremented once per backup-policy created. Untagged
    /// because the volume is naturally low; observable rate signals admin activity.
    /// </summary>
    public static readonly Counter<long> BackupPolicyCreated = Meter.CreateCounter<long>(
        name: "cnas.backup.policy_created",
        description: "Backup policies created via the admin registry (R2307).");

    /// <summary>
    /// R2307 / TOR SEC 060 — incremented once per backup run STARTED. Tagged with
    /// <c>policy_code</c> (bounded by the policy registry).
    /// </summary>
    public static readonly Counter<long> BackupRunStarted = Meter.CreateCounter<long>(
        name: "cnas.backup.run_started",
        description: "Backup runs started, tagged by policy_code (R2307).");

    /// <summary>
    /// R2307 / TOR SEC 060 — incremented once per backup run reaching a terminal status.
    /// Tagged with <c>policy_code</c> + <c>terminal_status</c>
    /// (Succeeded / Failed / IntegrityFailed).
    /// </summary>
    public static readonly Counter<long> BackupRunCompleted = Meter.CreateCounter<long>(
        name: "cnas.backup.run_completed",
        description: "Backup runs finalised, tagged by policy_code + terminal_status (R2307).");

    /// <summary>
    /// R2307 / TOR SEC 060 — incremented per row removed by the retention sweep.
    /// Untagged — the observable rate alone signals sweep activity.
    /// </summary>
    public static readonly Counter<long> BackupRetentionPurged = Meter.CreateCounter<long>(
        name: "cnas.backup.retention_purged",
        description: "Backup runs purged by the retention sweep (R2307).");

    /// <summary>
    /// R2307 / TOR SEC 060 — incremented once per integrity-check verdict.
    /// Tagged with <c>status</c> (Passed / Failed / Inconclusive).
    /// </summary>
    public static readonly Counter<long> BackupIntegrityCheckOutcome = Meter.CreateCounter<long>(
        name: "cnas.backup.integrity_check_outcome",
        description: "Backup integrity-check verdicts, tagged by status (R2307).");

    /// <summary>
    /// R2500 / TOR PIR 020-023 — incremented once per ticket submission.
    /// Tagged with <c>category_code</c> (bounded by the helpdesk catalog) and
    /// <c>severity</c>.
    /// </summary>
    public static readonly Counter<long> SupportTicketSubmitted = Meter.CreateCounter<long>(
        name: "cnas.support_ticket.submitted",
        description: "Helpdesk tickets submitted, tagged by category_code + severity (R2500).");

    /// <summary>
    /// R2500 / TOR PIR 020-023 — incremented once per ticket state transition.
    /// Tagged with <c>category_code</c>, <c>from_status</c>, <c>to_status</c>.
    /// </summary>
    public static readonly Counter<long> SupportTicketStateChanged = Meter.CreateCounter<long>(
        name: "cnas.support_ticket.state_changed",
        description: "Helpdesk ticket state transitions, tagged by category_code + from/to status (R2500).");

    /// <summary>
    /// R2500 / TOR PIR 020-023 — incremented once per newly-detected SLA event.
    /// Tagged with <c>category_code</c> + <c>event_kind</c>.
    /// </summary>
    public static readonly Counter<long> SupportTicketSlaBreached = Meter.CreateCounter<long>(
        name: "cnas.support_ticket.sla_breached",
        description: "Helpdesk SLA events recorded, tagged by category_code + event_kind (R2500).");

    /// <summary>
    /// R2500 / TOR PIR 020-023 — incremented once per ticket auto-escalated by
    /// the SLA evaluator. Tagged with <c>category_code</c>.
    /// </summary>
    public static readonly Counter<long> SupportTicketAutoEscalated = Meter.CreateCounter<long>(
        name: "cnas.support_ticket.auto_escalated",
        description: "Helpdesk tickets auto-escalated by the SLA evaluator, tagged by category_code (R2500).");

    /// <summary>
    /// R2501 / TOR PIR 024 — incremented once per business-hours policy
    /// state change (create / modify / activate / deactivate).
    /// </summary>
    public static readonly Counter<long> BusinessHoursPolicyChanged = Meter.CreateCounter<long>(
        name: "cnas.business_hours.policy_changed",
        description: "Business-hours policy state changes (R2501).");

    /// <summary>
    /// R2502 / TOR PIR 025 — incremented once per maintenance window created.
    /// Tagged with <c>kind</c>.
    /// </summary>
    public static readonly Counter<long> MaintenanceWindowCreated = Meter.CreateCounter<long>(
        name: "cnas.maintenance.window_created",
        description: "Maintenance windows created via the admin registry, tagged by kind (R2502).");

    /// <summary>
    /// R2502 / TOR PIR 025 — incremented once per maintenance window whose
    /// public notice was posted. Tagged with <c>kind</c>.
    /// </summary>
    public static readonly Counter<long> MaintenanceWindowNoticePosted = Meter.CreateCounter<long>(
        name: "cnas.maintenance.notice_posted",
        description: "Maintenance windows that transitioned to NoticePeriod, tagged by kind (R2502).");

    /// <summary>
    /// R2504 / TOR PIR 024 — incremented once per system-update event
    /// created. Tagged with <c>cadence</c>.
    /// </summary>
    public static readonly Counter<long> SystemUpdateEventCreated = Meter.CreateCounter<long>(
        name: "cnas.system_update.event_created",
        description: "System-update events created via the admin registry, tagged by cadence (R2504).");

    /// <summary>
    /// R2504 / TOR PIR 024 — incremented once per system-update event whose
    /// public notice was dispatched. Tagged with <c>cadence</c>.
    /// </summary>
    public static readonly Counter<long> SystemUpdateNotificationDispatched = Meter.CreateCounter<long>(
        name: "cnas.system_update.notification_dispatched",
        description: "System-update notifications dispatched, tagged by cadence (R2504).");

    /// <summary>
    /// R2505 / TOR PIR 030-033 — incremented once per change request submitted.
    /// Tagged with <c>kind</c>.
    /// </summary>
    public static readonly Counter<long> ChangeRequestSubmitted = Meter.CreateCounter<long>(
        name: "cnas.change_request.submitted",
        description: "Change requests submitted via the admin registry, tagged by kind (R2505).");

    /// <summary>
    /// R2505 / TOR PIR 030-033 — incremented once per change-request state
    /// transition. Tagged with <c>kind</c>, <c>from_status</c>, <c>to_status</c>.
    /// </summary>
    public static readonly Counter<long> ChangeRequestStateChanged = Meter.CreateCounter<long>(
        name: "cnas.change_request.state_changed",
        description: "Change-request state transitions, tagged by kind + from/to status (R2505).");

    /// <summary>
    /// R2505 / TOR PIR 030-033 — incremented once per change request rolled
    /// back. Tagged with <c>kind</c>.
    /// </summary>
    public static readonly Counter<long> ChangeRequestRollback = Meter.CreateCounter<long>(
        name: "cnas.change_request.rollback",
        description: "Change-request rollbacks, tagged by kind (R2505).");

    /// <summary>
    /// R2506 / TOR PIR 037-040 — incremented once per quality risk created.
    /// Tagged with <c>category</c>.
    /// </summary>
    public static readonly Counter<long> QualityRiskCreated = Meter.CreateCounter<long>(
        name: "cnas.quality_risk.created",
        description: "Quality risks created via the registry, tagged by category (R2506).");

    /// <summary>
    /// R2506 / TOR PIR 037-040 — incremented once per overdue-review risk
    /// detected by the annual-review sweep job.
    /// </summary>
    public static readonly Counter<long> QualityRiskReviewOverdueDetected = Meter.CreateCounter<long>(
        name: "cnas.quality_risk.review_overdue_detected",
        description: "Quality risks detected as overdue for annual review (R2506).");

    /// <summary>
    /// R2506 / TOR PIR 037-040 — incremented once per preventive-action state
    /// transition. Tagged with <c>from_status</c> and <c>to_status</c>.
    /// </summary>
    public static readonly Counter<long> QualityRiskActionStateChanged = Meter.CreateCounter<long>(
        name: "cnas.quality_risk.action_state_changed",
        description: "Quality-risk preventive-action state transitions (R2506).");

    /// <summary>
    /// R0051 / TOR SEC 014 / CLAUDE.md §5.3 — incremented once per local-login
    /// attempt. Tagged with <c>outcome</c> = <c>success</c> | <c>bad_password</c> |
    /// <c>wrong_role</c> | <c>unknown_login</c> | <c>account_locked</c> |
    /// <c>account_not_active</c> | <c>validation_failed</c> so operators can chart
    /// the per-outcome attempt rate without breaching the no-PII invariant
    /// (CLAUDE.md §5.6). Account-enumeration prevention means every failure mode
    /// surfaces the same wire-level error code; the meter is the place to
    /// distinguish them for alerting.
    /// </summary>
    public static readonly Counter<long> LocalLoginAttempted = Meter.CreateCounter<long>(
        name: "cnas.local_login.attempted",
        description: "Local-login attempts tagged by outcome (R0051 / SEC 014).");

    /// <summary>
    /// R0103 / TOR CF 14.02 — incremented once per inbound CloudEvent whose
    /// <c>MessageId</c> was already present in the dedup ledger. Tagged with
    /// <c>source</c> and <c>type</c> so operators can chart per-stream duplicate
    /// volume without breaching the no-PII invariant (the envelope id itself is
    /// NEVER attached as a tag — that would be unbounded cardinality).
    /// </summary>
    public static readonly Counter<long> IntegrationEventDeduped = Meter.CreateCounter<long>(
        name: "cnas.integration_event.deduped",
        description: "Inbound CloudEvents short-circuited by the R0103 dedup ledger.");

    /// <summary>
    /// R0103 / TOR CF 14.02 — incremented once per inbound CloudEvent claimed for
    /// the first time (i.e. the deduper inserted a fresh row). Tagged with
    /// <c>source</c> and <c>type</c>.
    /// </summary>
    public static readonly Counter<long> IntegrationEventAccepted = Meter.CreateCounter<long>(
        name: "cnas.integration_event.accepted",
        description: "Inbound CloudEvents claimed for the first time by the R0103 dedup ledger.");

    /// <summary>
    /// R0103 / TOR CF 14.02 — incremented once per inbound CloudEvent whose
    /// downstream handler chain raised an unhandled exception AFTER the dedup
    /// row was successfully claimed. Tagged with <c>source</c> only — adding
    /// <c>type</c> here would balloon cardinality on a low-volume failure
    /// channel and operators chart this metric by stream rather than by event
    /// shape.
    /// </summary>
    public static readonly Counter<long> IntegrationEventFailed = Meter.CreateCounter<long>(
        name: "cnas.integration_event.failed",
        description: "Inbound CloudEvents whose downstream handler chain raised after the dedup row was claimed (R0103).");

    /// <summary>
    /// R0117 / CF 14.11 — incremented once at the start of every
    /// <c>IPgdPublisher.PublishAsync</c> call, BEFORE the upstream HTTP attempt. Tagged
    /// with <c>dataset_code</c> so operators can chart per-dataset attempt rates without
    /// breaching the no-PII invariant (the code is admin-supplied and bounded).
    /// </summary>
    public static readonly Counter<long> PgdPublishAttempted = Meter.CreateCounter<long>(
        name: "cnas.pgd.publish.attempted",
        description: "PGD dataset publish attempts; tagged with dataset_code.");

    /// <summary>
    /// R0117 / CF 14.11 — incremented once per publish call after the outcome is known.
    /// Tagged with <c>dataset_code</c> and <c>status</c> (<c>accepted</c> | <c>rejected</c>
    /// | <c>skipped</c>) so operators can chart success rate per dataset.
    /// </summary>
    public static readonly Counter<long> PgdPublishOutcome = Meter.CreateCounter<long>(
        name: "cnas.pgd.publish.outcome",
        description: "PGD dataset publish outcomes; tagged with dataset_code and status.");

    /// <summary>
    /// R0125 / CF 16.09 — incremented once per workflow-task history event recorded.
    /// Tagged with <c>event_kind</c> so operators can chart the relative frequency of
    /// each transition kind.
    /// </summary>
    public static readonly Counter<long> WorkflowTaskHistoryEvent = Meter.CreateCounter<long>(
        name: "cnas.workflow_task.history.event",
        description: "Workflow-task history events recorded; tagged with event_kind.");

    /// <summary>
    /// R0132 / CF 17.18 — incremented once per template-version rollback. Tagged with
    /// <c>template_code</c> so operators can spot anomalous rollback patterns
    /// (e.g. repeated rollbacks of the same template) on the open-data dashboard.
    /// </summary>
    public static readonly Counter<long> TemplateVersionRollback = Meter.CreateCounter<long>(
        name: "cnas.template.version.rollback",
        description: "Template-version rollbacks; tagged with template_code.");

    /// <summary>
    /// R0160 / R0161 / TOR CF 03.03 — incremented once per global full-text-search
    /// invocation. Tagged with <c>domain_count</c> (string representation of the
    /// number of domains queried) so operators can chart the distribution of
    /// single-domain vs cross-domain calls and identify domain-fan-out hotspots.
    /// </summary>
    public static readonly Counter<long> FullTextSearchExecuted = Meter.CreateCounter<long>(
        name: "cnas.search.fulltext.executed",
        description: "Global full-text-search invocations; tagged with domain_count.");

    /// <summary>
    /// R0160 / R0161 / TOR CF 03.03 — incremented once per global full-text-search
    /// invocation that returned zero hits. Untagged — operators chart absolute volume
    /// against <see cref="FullTextSearchExecuted"/> to spot empty-result spikes
    /// (a signal that the indexes need re-tuning or the user's query is malformed).
    /// </summary>
    public static readonly Counter<long> FullTextSearchEmptyResult = Meter.CreateCounter<long>(
        name: "cnas.search.fulltext.empty_result",
        description: "Global full-text-search invocations that returned zero hits.");

    /// <summary>
    /// R0211 / TOR UI 003 — incremented once per
    /// <c>IPreferredLanguageResolver.ResolveAsync</c> call. Tagged with
    /// <c>language</c> = <c>ro</c> | <c>en</c> | <c>ru</c> so operators can chart
    /// the per-language adoption rate of the localisation switcher.
    /// </summary>
    public static readonly Counter<long> PreferredLanguageResolved = Meter.CreateCounter<long>(
        name: "cnas.profile.preferred_language.resolved",
        description: "Preferred-language resolutions; tagged with language.");

    /// <summary>
    /// R0302 / TOR §2.1 — incremented once per
    /// <c>IContributorSourceHistoryService.RecordChangeAsync</c> success. Tagged
    /// with <c>new_source</c> (the post-change source attribution) so operators
    /// can chart the per-source ingestion volume.
    /// </summary>
    public static readonly Counter<long> ContributorSourceChangeRecorded = Meter.CreateCounter<long>(
        name: "cnas.contributor.source_change.recorded",
        description: "Contributor source-system changes recorded; tagged with new_source.");

    /// <summary>
    /// R0322 / TOR UI 014 — incremented once per
    /// <c>IApplicationAttachmentService.AttachAsync</c> success. Tagged with
    /// <c>category</c> (the semantic attachment category) so operators can chart
    /// per-category attachment volume.
    /// </summary>
    public static readonly Counter<long> ApplicationAttachmentAttached = Meter.CreateCounter<long>(
        name: "cnas.application_attachment.attached",
        description: "Application attachments created; tagged with category.");

    /// <summary>
    /// R0322 / TOR UI 014 — incremented once per virus-scan-result post. Tagged
    /// with <c>status</c> (<c>Clean</c> | <c>Infected</c> | <c>ScanFailed</c> |
    /// <c>Skipped</c>) so operators can chart the per-status scan-volume.
    /// </summary>
    public static readonly Counter<long> ApplicationAttachmentVirusScanCompleted = Meter.CreateCounter<long>(
        name: "cnas.application_attachment.virus_scan.completed",
        description: "Application-attachment virus-scan results recorded; tagged with status.");

    /// <summary>
    /// R0402 / TOR CF 17.09 — incremented once per
    /// <c>IClassifierService.DeactivateAsync</c> call that the reference-guard
    /// short-circuited because referencing rows still cite the
    /// classifier (Kind, Code). Tagged with <c>scheme</c> = the classifier
    /// kind (e.g. <c>CAEM</c>) so operators can chart which schemes are most
    /// frequently blocked.
    /// </summary>
    public static readonly Counter<long> ClassifierReferenceBlocked = Meter.CreateCounter<long>(
        name: "cnas.classifier.reference.blocked",
        description: "Classifier deactivations blocked by the reference-guard; tagged with scheme.");

    /// <summary>
    /// R0500 / TOR CF 01.02 — incremented once per
    /// <c>IPublicKpiService.GetCurrentAsync</c> call that traversed the
    /// cache window and recomputed the snapshot against the read-replica.
    /// </summary>
    public static readonly Counter<long> PublicKpiSnapshotComputed = Meter.CreateCounter<long>(
        name: "cnas.public_kpi.snapshot.computed",
        description: "Public KPI snapshots recomputed against the read-replica.");

    /// <summary>
    /// R0500 / TOR CF 01.02 — incremented once per
    /// <c>IPublicKpiService.GetCurrentAsync</c> call that returned the
    /// cached snapshot without touching the DB. The ratio
    /// <c>cache_hit / (cache_hit + computed)</c> measures public-KPI
    /// cache effectiveness.
    /// </summary>
    public static readonly Counter<long> PublicKpiSnapshotCacheHit = Meter.CreateCounter<long>(
        name: "cnas.public_kpi.snapshot.cache_hit",
        description: "Public KPI snapshot calls served from the in-process cache.");

    /// <summary>
    /// R0203 / TOR CF 20.06 — incremented once per
    /// <c>ExternalSourceIngestionRun</c> START. Tagged with
    /// <c>source_code</c> + <c>trigger_kind</c> so operators chart per-source
    /// run rates. Cardinality bounded to (sources × triggers) ≈ 10.
    /// </summary>
    public static readonly Counter<long> ExternalSourceIngestionRunStarted = Meter.CreateCounter<long>(
        name: "cnas.external_source.ingestion_run.started",
        description: "External-source ingestion runs started, tagged by source_code and trigger_kind (R0203).");

    /// <summary>
    /// R0203 / TOR CF 20.06 — incremented once per
    /// <c>ExternalSourceIngestionRun</c> END. Tagged with
    /// <c>source_code</c> + <c>terminal_status</c> so operators chart
    /// per-source success rate. Cardinality bounded to (sources × statuses) ≈ 25.
    /// </summary>
    public static readonly Counter<long> ExternalSourceIngestionRunCompleted = Meter.CreateCounter<long>(
        name: "cnas.external_source.ingestion_run.completed",
        description: "External-source ingestion runs finalised, tagged by source_code and terminal_status (R0203).");

    /// <summary>
    /// R0341 / TOR CF 11.06 — incremented once per
    /// <c>IPdfAConversionService.ConvertAsync</c> call. Tagged with
    /// <c>outcome={success|failure|engine_not_available}</c>.
    /// </summary>
    public static readonly Counter<long> PdfAConversionAttempted = Meter.CreateCounter<long>(
        name: "cnas.document.pdfa.conversion_attempted",
        description: "PDF/A conversion attempts, tagged by outcome (R0341).");

    /// <summary>
    /// R0341 / TOR CF 11.06 — incremented once per
    /// <c>IDocumentHashVerifier.VerifyAsync</c> call. Tagged with
    /// <c>outcome={match|mismatch|error}</c>. Operators chart the mismatch
    /// rate as a tamper-detection signal.
    /// </summary>
    public static readonly Counter<long> DocumentHashVerification = Meter.CreateCounter<long>(
        name: "cnas.document.hash_verify",
        description: "Document hash-verification calls, tagged by outcome (R0341).");
}
