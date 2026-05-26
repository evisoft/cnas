using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.AdminDashboard;

/// <summary>
/// R0537 / CF 04.10 — orchestrator that composes the admin dashboard's superset
/// payload (KPIs + recent security alerts + audit summary + admin-action backlog +
/// optional perf metrics) into a single read. The Blazor admin dashboard's
/// composition root invokes this once per render rather than firing N parallel REST
/// calls so the UI's latency budget stays predictable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition only.</b> The service does NOT compute KPIs or evaluate alerts
/// itself; it pulls aggregated data from the dependent subsystems:
/// <list type="bullet">
///   <item>KPIs: <c>IKpiSnapshotService.GetLatestAsync</c> (R0201).</item>
///   <item>Alerts: <c>AuditLog</c> rows where <c>EventCode=SECURITY_ALERT.FIRED</c>
///         (R0189).</item>
///   <item>Audit summary: <c>AuditLog GROUP BY Severity WHERE CreatedAtUtc &gt;=
///         now - 24h</c>.</item>
///   <item>Open admin actions: <c>PendingAdminAction</c> count where
///         <c>Status=Pending</c> AND <c>IsActive</c> (R0058).</item>
///   <item>Perf metrics: optional — empty list with a warning when meter snapshot
///         isn't wired in this environment.</item>
/// </list>
/// </para>
/// <para>
/// <b>Failure semantics.</b> The service degrades gracefully — a missing sub-payload
/// (e.g. perf-metric snapshot unavailable) returns an empty list and surfaces a
/// human-readable diagnostic on <see cref="AdminDashboardDto.Warning"/> rather than
/// failing the whole call. The only hard failures are infrastructure-level
/// (database unavailable, cancellation).
/// </para>
/// </remarks>
public interface IAdminDashboardService
{
    /// <summary>
    /// Composes the latest dashboard snapshot from every dependent subsystem and
    /// returns a single DTO. Always returns <see cref="Result{T}.Success(T)"/> when
    /// the underlying DB is reachable — sub-payload gaps are reported via the
    /// payload's <see cref="AdminDashboardDto.Warning"/> field rather than as a
    /// Result failure.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>
    /// A success Result carrying the composed snapshot. A failure Result fires only
    /// on infrastructure-level errors (cancellation, DB unavailable).
    /// </returns>
    Task<Result<AdminDashboardDto>> GetSnapshotAsync(
        CancellationToken cancellationToken = default);
}
