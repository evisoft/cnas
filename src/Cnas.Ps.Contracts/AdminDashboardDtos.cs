using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0537 / CF 04.10 — single-payload admin-dashboard superset that aggregates KPIs,
/// recent security alerts, audit summary, the open admin-action backlog, and (optional)
/// performance metrics into one read. The Blazor admin dashboard renders this snapshot
/// without firing N parallel REST calls — the server pre-aggregates the dependent
/// subsystems so the UI's response budget stays predictable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sensitivity floor.</b> The DTO carries
/// <see cref="DashboardSecurityAlertDto"/> rows whose contents can reveal user activity
/// (failed-login bursts, refresh-token reuse) — those are
/// <see cref="SensitivityLabel.Confidential"/>. KPIs themselves are
/// <see cref="SensitivityLabel.Internal"/>; the type-level floor is therefore
/// <see cref="SensitivityLabel.Internal"/>, with property-level overrides on the
/// alert list.
/// </para>
/// <para>
/// <b>Time discipline.</b> Every timestamp on the payload is UTC per CLAUDE.md
/// cross-cutting; the snapshot itself stamps <see cref="SnapshotAtUtc"/> so the UI
/// can render "Last updated 30s ago" without re-issuing the call.
/// </para>
/// </remarks>
/// <param name="Kpis">
/// Dictionary keyed by stable KPI code carrying the latest snapshot value. Pulled from
/// <c>IKpiSnapshotService.GetLatestAsync</c> over the set
/// (<c>Applications.Pending</c>, <c>Tasks.Overdue</c>, <c>Applications.ClosedYesterday</c>,
/// <c>Notifications.DeliveredYesterday</c>, <c>Tasks.AvgHandlingHours</c>). KPI codes the
/// store has never seen are omitted from the dictionary rather than zero-filled.
/// </param>
/// <param name="RecentAlerts">
/// Top 20 security alerts from the last 24 hours (newest first). Pulled from
/// <c>AuditLog</c> rows whose <c>EventCode</c> is <c>SECURITY_ALERT.FIRED</c>
/// (the canonical event written by <c>SecurityAlertEvaluatorJob</c>). Carries
/// confidentiality-class data so the property is marked
/// <see cref="SensitivityLabel.Confidential"/>.
/// </param>
/// <param name="AuditSummary">
/// Count of audit-log rows in the last 24 hours grouped by
/// <c>AuditSeverity</c>. Drives the "X criticals, Y notices, …" tile on the dashboard
/// without exposing per-row details.
/// </param>
/// <param name="OpenAdminActionsCount">
/// Number of <c>PendingAdminAction</c> rows whose <c>Status</c> is
/// <c>PendingAdminActionStatus.Pending</c> AND <c>IsActive=true</c>. Surfaces the
/// maker-checker backlog (R0058) on the dashboard.
/// </param>
/// <param name="PerfMetrics">
/// Optional collection of perf metric snapshots. Empty when the meter snapshot is not
/// available in this environment (the service documents the gap via
/// <see cref="Warning"/>). The full meter-listener integration is deferred.
/// </param>
/// <param name="SnapshotAtUtc">
/// UTC instant the service composed this snapshot. The UI can render "as of …" without
/// re-issuing the call.
/// </param>
/// <param name="Warning">
/// Optional human-readable diagnostic explaining a missing sub-payload (e.g. perf
/// metrics not wired in this environment). <c>null</c> when every sub-payload was
/// successfully composed.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record AdminDashboardDto(
    IReadOnlyDictionary<string, decimal> Kpis,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    IReadOnlyList<DashboardSecurityAlertDto> RecentAlerts,
    IReadOnlyList<DashboardAuditSummaryDto> AuditSummary,
    int OpenAdminActionsCount,
    IReadOnlyList<DashboardPerfMetricDto> PerfMetrics,
    DateTime SnapshotAtUtc,
    string? Warning);

/// <summary>
/// R0537 / CF 04.10 — one row in the admin dashboard's recent-security-alerts list.
/// Sourced from <c>AuditLog</c> rows whose event code is
/// <c>SECURITY_ALERT.FIRED</c> (R0189 / SEC 048).
/// </summary>
/// <remarks>
/// <para>
/// <b>Confidential by default.</b> An alert's mere existence can reveal sensitive
/// information (a failed-login burst implies an attempted compromise on a specific
/// account). The type-level floor is <see cref="SensitivityLabel.Confidential"/> so
/// every property inherits the label, no exceptions.
/// </para>
/// </remarks>
/// <param name="AlertSqid">Sqid-encoded id of the originating <c>AuditLog</c> row.</param>
/// <param name="RuleCode">Stable rule code (e.g. <c>FAILED_LOGIN_BURST</c>) read out of
/// the audit row's <c>DetailsJson</c>. <c>null</c> when the payload did not carry one.</param>
/// <param name="TriggeredAtUtc">UTC instant the alert fired.</param>
/// <param name="AffectedUserSqid">
/// Sqid-encoded id of the affected user when the alert's <c>TargetEntity</c> is a
/// <c>UserProfile</c>. <c>null</c> when the alert is not user-scoped.
/// </param>
/// <param name="Summary">
/// Short human-readable summary of the alert (rule code + actor id by default). The UI
/// renders this as the alert's display label; it MUST NOT carry PII.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record DashboardSecurityAlertDto(
    string AlertSqid,
    string? RuleCode,
    DateTime TriggeredAtUtc,
    string? AffectedUserSqid,
    string Summary);

/// <summary>
/// R0537 / CF 04.10 — single row in the admin dashboard's audit-summary tile. One row
/// per distinct <c>AuditSeverity</c> level observed in the last 24-hour window.
/// </summary>
/// <param name="Severity">
/// Audit severity bucket (Information / Notice / Sensitive / Critical) rendered as the
/// enum's string name so the wire shape is stable across protobuf-like consumers.
/// </param>
/// <param name="Count">Number of audit rows in this bucket in the rolling 24-hour window.</param>
public sealed record DashboardAuditSummaryDto(
    string Severity,
    long Count);

/// <summary>
/// R0537 / CF 04.10 — single performance-metric tile. Populated when the meter snapshot
/// is available in the deployment; empty list otherwise (see
/// <see cref="AdminDashboardDto.Warning"/>).
/// </summary>
/// <param name="Name">Stable instrument name (e.g. <c>cnas.request.duration_ms</c>).</param>
/// <param name="Value">Last observed value (units depend on <see cref="Unit"/>).</param>
/// <param name="Unit">
/// Unit string (e.g. <c>ms</c>, <c>count</c>). Drives the UI's value-formatting choice
/// without the front-end having to hard-code per-metric formatting rules.
/// </param>
public sealed record DashboardPerfMetricDto(
    string Name,
    decimal Value,
    string Unit);
