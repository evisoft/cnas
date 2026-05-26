using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2461 / R2462 — Monthly operational reports DTOs (Deliverable 7.1 & 7.2).
//
// R2461 — Monthly Support Report: aggregates SupportTicket counts + SLA
// breach rates + avg resolution time per category + per severity.
// R2462 — Monthly Error-Fix + Doc-Update Report: aggregates
// IntegrityCheckFinding counts + ChangeRequest rollback/deploy counts +
// TemplateVariant update counts.
//
// All fields are Internal-sensitivity operational data (NOT PII). The
// reports identify failures by code / aggregate-name / severity rather
// than by row id, so no Sqid encoding is required on the surface.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R2461 / Deliverable 7.1 — input envelope for the monthly support report.
/// <paramref name="Month"/> selects the calendar month to aggregate (first
/// day of the month, UTC); optional <paramref name="CategoryCodes"/> filter
/// limits results to a specific subset of category codes.
/// </summary>
/// <param name="Month">
/// First-of-month UTC date selecting the report window. The validator
/// rejects any value whose <c>Day</c> is not 1 or which sits in the future
/// relative to the configured clock.
/// </param>
/// <param name="CategoryCodes">
/// Optional filter — when non-null, only tickets whose
/// <c>SupportTicketCategory.Code</c> appears in the list are aggregated.
/// Codes are matched case-sensitively against the stable
/// SCREAMING_SNAKE_CASE category codes.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MonthlySupportReportInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly Month,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    IReadOnlyList<string>? CategoryCodes);

/// <summary>
/// R2461 / Deliverable 7.1 — one severity-keyed breakdown row in the
/// monthly support report. Tickets are bucketed by their current
/// <c>SupportTicketSeverity</c> at the time the snapshot is computed.
/// </summary>
/// <param name="Severity">Stable enum-name string of <c>SupportTicketSeverity</c>.</param>
/// <param name="TotalSubmitted">Tickets submitted in the month with this severity.</param>
/// <param name="TotalResolved">Tickets resolved in the month with this severity.</param>
/// <param name="AvgResolutionMinutes">Average resolution time in minutes (null when no resolved tickets).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MonthlySupportSeverityBreakdownRow(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Severity,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalSubmitted,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalResolved,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal? AvgResolutionMinutes);

/// <summary>
/// R2461 / Deliverable 7.1 — one category-keyed breakdown row in the
/// monthly support report. Tickets are bucketed by their
/// <c>SupportTicketCategory.Code</c> resolved at the snapshot instant.
/// </summary>
/// <param name="CategoryCode">Stable SCREAMING_SNAKE_CASE category code.</param>
/// <param name="TotalSubmitted">Tickets submitted in the month under this category.</param>
/// <param name="TotalResolved">Tickets resolved in the month under this category.</param>
/// <param name="AvgFirstResponseMinutes">Average first-response time in minutes (null when no acknowledged tickets).</param>
/// <param name="AvgResolutionMinutes">Average resolution time in minutes (null when no resolved tickets).</param>
/// <param name="FirstResponseBreachRate">First-response breach rate as a decimal 0..1 (4 decimals).</param>
/// <param name="ResolutionBreachRate">Resolution breach rate as a decimal 0..1 (4 decimals).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MonthlySupportCategoryBreakdownRow(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string CategoryCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalSubmitted,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalResolved,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal? AvgFirstResponseMinutes,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal? AvgResolutionMinutes,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal FirstResponseBreachRate,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal ResolutionBreachRate);

/// <summary>
/// R2461 / Deliverable 7.1 — monthly support report payload. Aggregates
/// every <c>SupportTicket</c> whose <c>SubmittedAt</c> falls inside the
/// requested month (UTC). The breach-rate fields are decimals in <c>0..1</c>
/// with four-decimal precision; multiply by 100 to render as a percent at
/// the UI.
/// </summary>
/// <param name="Month">The first-of-month UTC date that was requested.</param>
/// <param name="GeneratedAtUtc">UTC instant the report was computed (clock-supplied).</param>
/// <param name="TotalSubmitted">Tickets submitted in the month.</param>
/// <param name="TotalResolved">Tickets that reached Resolved at any point during the month.</param>
/// <param name="TotalClosed">Tickets closed in the month.</param>
/// <param name="TotalEscalated">Tickets escalated in the month.</param>
/// <param name="TotalCancelled">Tickets cancelled in the month.</param>
/// <param name="AvgFirstResponseMinutes">Average first-response time in minutes (null when no acknowledged tickets).</param>
/// <param name="AvgResolutionMinutes">Average resolution time in minutes (null when no resolved tickets).</param>
/// <param name="FirstResponseBreachRate">First-response breach rate as decimal 0..1, 4 decimals.</param>
/// <param name="ResolutionBreachRate">Resolution breach rate as decimal 0..1, 4 decimals.</param>
/// <param name="SeverityBreakdown">Per-severity breakdown rows (sorted by severity enum order).</param>
/// <param name="CategoryBreakdown">Per-category breakdown rows (sorted by category code).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MonthlySupportReportDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly Month,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime GeneratedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalSubmitted,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalResolved,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalClosed,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalEscalated,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalCancelled,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal? AvgFirstResponseMinutes,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal? AvgResolutionMinutes,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal FirstResponseBreachRate,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    decimal ResolutionBreachRate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<MonthlySupportSeverityBreakdownRow> SeverityBreakdown,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<MonthlySupportCategoryBreakdownRow> CategoryBreakdown);

/// <summary>
/// R2462 / Deliverable 7.2 — input envelope for the monthly error-fix /
/// documentation-update report. Same calendar-month semantics as
/// <see cref="MonthlySupportReportInputDto"/>.
/// </summary>
/// <param name="Month">
/// First-of-month UTC date selecting the report window. The validator
/// rejects any value whose <c>Day</c> is not 1 or which sits in the future
/// relative to the configured clock.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MonthlyErrorFixReportInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly Month);

/// <summary>
/// R2462 / Deliverable 7.2 — one (aggregate-name × severity) row in the
/// error-fix report. Aggregate names mirror the <c>IntegrityCheckFinding</c>
/// AggregateName values (e.g. <c>Claim</c>, <c>ExecutoryDocument</c>,
/// <c>UserProfile</c>).
/// </summary>
/// <param name="AggregateName">Display name of the offending aggregate.</param>
/// <param name="Severity">Stable enum-name string of <c>IntegrityFindingSeverity</c>.</param>
/// <param name="Count">Number of findings of this (aggregate, severity) bucket in the month.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MonthlyErrorFixCategoryBreakdownRow(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string AggregateName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Severity,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Count);

/// <summary>
/// R2462 / Deliverable 7.2 — monthly error-fix + documentation-update
/// report payload. Aggregates integrity findings (by severity), change
/// requests (rolled-back / deployed) and documentation-template updates
/// over the requested calendar month (UTC).
/// </summary>
/// <param name="Month">The first-of-month UTC date that was requested.</param>
/// <param name="GeneratedAtUtc">UTC instant the report was computed (clock-supplied).</param>
/// <param name="TotalIntegrityFindings">Total integrity findings detected in the month.</param>
/// <param name="IntegrityFindingsByCriticalSeverity">Findings with severity = Critical.</param>
/// <param name="IntegrityFindingsByHighSeverity">Findings with severity = High.</param>
/// <param name="IntegrityFindingsByMediumSeverity">Findings with severity = Medium.</param>
/// <param name="IntegrityFindingsByLowSeverity">Findings with severity = Low.</param>
/// <param name="TotalChangeRequestsRolledBack">Change requests that reached the RolledBack state in the month.</param>
/// <param name="TotalChangeRequestsDeployed">Change requests that reached the Deployed state in the month.</param>
/// <param name="TotalDocumentationTemplatesUpdated">
/// <c>TemplateVariant</c> rows whose <c>UpdatedAtUtc</c> falls inside the month
/// — counts every translated/edited variant per template/language pair.
/// </param>
/// <param name="CategoryBreakdown">Per-(aggregate-name × severity) breakdown of integrity findings.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MonthlyErrorFixReportDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly Month,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime GeneratedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalIntegrityFindings,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int IntegrityFindingsByCriticalSeverity,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int IntegrityFindingsByHighSeverity,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int IntegrityFindingsByMediumSeverity,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int IntegrityFindingsByLowSeverity,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalChangeRequestsRolledBack,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalChangeRequestsDeployed,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalDocumentationTemplatesUpdated,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<MonthlyErrorFixCategoryBreakdownRow> CategoryBreakdown);
