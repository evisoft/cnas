using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.AdminDashboard;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0537 / CF 04.10 — default implementation of <see cref="IAdminDashboardService"/>.
/// Composes the admin dashboard's payload from four read sources: the KPI snapshot
/// store (R0201), the audit log's <c>SECURITY_ALERT.FIRED</c> stream (R0189), the
/// audit log's severity histogram (R0182), and the pending-admin-action backlog
/// (R0058). The optional perf-metrics tile is returned empty with a warning when the
/// meter-snapshot integration is not wired in the current environment — see the
/// service remarks for the deferred integration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Scoped — captures the per-request read-only DbContext + UTC
/// clock; the KPI snapshot service is itself scoped. Wired in
/// <c>InfrastructureServiceCollectionExtensions</c>.
/// </para>
/// <para>
/// <b>Window discipline.</b> Both the audit summary and the recent-alerts list use a
/// rolling 24-hour window anchored at the clock instant captured at the top of the
/// call so the snapshot is internally consistent — a slow query cannot cause one
/// sub-payload to use a different window than another.
/// </para>
/// </remarks>
public sealed class AdminDashboardService : IAdminDashboardService
{
    /// <summary>Stable KPI codes the dashboard surfaces. See R0201 for the catalogue.</summary>
    private static readonly string[] DashboardKpiCodes =
    [
        "Applications.Pending",
        "Tasks.Overdue",
        "Applications.ClosedYesterday",
        "Notifications.DeliveredYesterday",
        "Tasks.AvgHandlingHours",
    ];

    /// <summary>Maximum number of recent alerts returned (newest first).</summary>
    public const int MaxRecentAlerts = 20;

    /// <summary>Window length for the audit summary and recent-alert query, in hours.</summary>
    public const int WindowHours = 24;

    /// <summary>Stable event code identifying a security-alert audit row (mirrors R0189).</summary>
    private const string SecurityAlertFiredCode = "SECURITY_ALERT.FIRED";

    /// <summary>
    /// Warning string surfaced on the payload when perf-metrics are unavailable in this
    /// environment. The Blazor admin dashboard renders the string verbatim — translated
    /// strings stay on the front-end (i18n is the consumer's job, not this service's).
    /// </summary>
    public const string PerfMetricsUnavailableWarning =
        "Perf metrics snapshot is not wired in this environment; the tile is intentionally empty.";

    private readonly IReadOnlyCnasDbContext _readDb;
    private readonly IKpiSnapshotService _kpis;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ILogger<AdminDashboardService> _logger;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="readDb">Read-only DB context routed to the replica (R0026).</param>
    /// <param name="kpis">KPI snapshot service (R0201).</param>
    /// <param name="sqids">Sqid encoder for the surfaced alert / user ids.</param>
    /// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="logger">Structured logger.</param>
    public AdminDashboardService(
        IReadOnlyCnasDbContext readDb,
        IKpiSnapshotService kpis,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ILogger<AdminDashboardService> logger)
    {
        ArgumentNullException.ThrowIfNull(readDb);
        ArgumentNullException.ThrowIfNull(kpis);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _readDb = readDb;
        _kpis = kpis;
        _sqids = sqids;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<AdminDashboardDto>> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var windowStart = now.AddHours(-WindowHours);

        var kpis = await _kpis.GetLatestAsync(DashboardKpiCodes, cancellationToken).ConfigureAwait(false);

        // Recent alerts — audit rows with the canonical SECURITY_ALERT.FIRED code, newest
        // first, capped at MaxRecentAlerts. We parse the rule code out of DetailsJson best-
        // effort; a malformed payload surfaces as a null RuleCode rather than failing the
        // whole call.
        var alertRows = await _readDb.AuditLogs
            .Where(a => a.EventCode == SecurityAlertFiredCode && a.EventAtUtc >= windowStart)
            .OrderByDescending(a => a.EventAtUtc)
            .Take(MaxRecentAlerts)
            .Select(a => new
            {
                a.Id,
                a.EventAtUtc,
                a.DetailsJson,
                a.TargetEntity,
                a.TargetEntityId,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var recentAlerts = new List<DashboardSecurityAlertDto>(alertRows.Count);
        foreach (var row in alertRows)
        {
            var ruleCode = ExtractRuleCode(row.DetailsJson);
            string? affectedUserSqid = null;
            if (string.Equals(row.TargetEntity, nameof(UserProfile), StringComparison.Ordinal)
                && row.TargetEntityId is long uid)
            {
                affectedUserSqid = _sqids.Encode(uid);
            }
            recentAlerts.Add(new DashboardSecurityAlertDto(
                AlertSqid: _sqids.Encode(row.Id),
                RuleCode: ruleCode,
                TriggeredAtUtc: row.EventAtUtc,
                AffectedUserSqid: affectedUserSqid,
                Summary: ruleCode is null
                    ? "Security alert fired"
                    : $"Security alert: {ruleCode}"));
        }

        // Audit summary — group by severity over the rolling 24-hour window.
        var summaryRows = await _readDb.AuditLogs
            .Where(a => a.EventAtUtc >= windowStart)
            .GroupBy(a => a.Severity)
            .Select(g => new { Severity = g.Key, Count = (long)g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var auditSummary = summaryRows
            .OrderBy(r => (int)r.Severity)
            .Select(r => new DashboardAuditSummaryDto(r.Severity.ToString(), r.Count))
            .ToList();

        // Open admin-action backlog — pending + active, no time bound.
        var openAdminActions = await _readDb.PendingAdminActions
            .Where(p => p.Status == PendingAdminActionStatus.Pending && p.IsActive)
            .CountAsync(cancellationToken).ConfigureAwait(false);

        // Perf metrics — meter-snapshot integration deferred to a follow-up batch.
        // We surface an empty list + an explanatory warning so the UI knows the tile
        // is intentionally empty rather than just unsupported by this version.
        var perfMetrics = Array.Empty<DashboardPerfMetricDto>();
        var warning = PerfMetricsUnavailableWarning;

        var dto = new AdminDashboardDto(
            Kpis: kpis,
            RecentAlerts: recentAlerts,
            AuditSummary: auditSummary,
            OpenAdminActionsCount: openAdminActions,
            PerfMetrics: perfMetrics,
            SnapshotAtUtc: now,
            Warning: warning);

        _logger.LogInformation(
            "AdminDashboardService snapshot composed: kpiCount={KpiCount} alertCount={AlertCount} auditBuckets={AuditBuckets} openActions={OpenActions}",
            kpis.Count, recentAlerts.Count, auditSummary.Count, openAdminActions);

        return Result<AdminDashboardDto>.Success(dto);
    }

    /// <summary>
    /// Best-effort extraction of the <c>ruleCode</c> field from a
    /// <c>SECURITY_ALERT.FIRED</c> audit row's <c>DetailsJson</c>. Returns <c>null</c>
    /// when the payload is null/empty, not a JSON object, missing the field, or fails
    /// to parse — the dashboard renders without a rule code rather than failing.
    /// </summary>
    /// <param name="detailsJson">Raw audit-log <c>DetailsJson</c> payload.</param>
    /// <returns>The parsed rule code, or <c>null</c> on any failure.</returns>
    private static string? ExtractRuleCode(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (!doc.RootElement.TryGetProperty("ruleCode", out var ruleCodeEl))
            {
                return null;
            }
            return ruleCodeEl.ValueKind == JsonValueKind.String ? ruleCodeEl.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
