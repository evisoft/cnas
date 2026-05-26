using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Annex 6h named-report extensions for <see cref="ReportingService"/>. The ninth batch of
/// Annex 6 reports lives in its own partial file so that the earlier batches
/// (<c>ReportingService.Annex6.cs</c> through <c>ReportingService.Annex6g.cs</c>) remain
/// decoupled. The new codes are recognised by <see cref="IsAnnex6hReportCode"/> and dispatched
/// by <see cref="BuildAnnex6hDatasetAsync"/>; the Annex 6g dispatcher chains into this file via
/// a single <c>_ when</c> arm so new code only requires one minimal upstream edit.
/// </summary>
/// <remarks>
/// <para>
/// Conventions match the earlier batches — external identifiers in every row are Sqid-encoded
/// (CLAUDE.md RULE 3) when they appear, timestamps are UTC formatted with the round-trip
/// <c>"o"</c> format using <see cref="CultureInfo.InvariantCulture"/>, money is rendered using
/// <c>F2</c> on the invariant culture, and date windows are half-open <c>[fromUtc, toUtc)</c>.
/// </para>
/// <para>
/// None of the five reports in this batch emit per-entity rows — they are all aggregations
/// (severity histogram, daily volume series, daily login counts, distinct-actor counts, and
/// per-service percentile statistics), so no Sqid encoding appears in their output. Two of
/// them apply a dense-output contract: <c>RPT-AUDIT-EVENTS-BY-SEVERITY</c> emits one row per
/// <see cref="AuditSeverity"/> value, and <c>RPT-DOCUMENT-UPLOAD-VOLUMES</c>/<c>RPT-LOGIN-EVENTS-PER-DAY</c>
/// emit one row per calendar day in <c>[fromUtc, toUtc)</c> (gap-filled with zeroes).
/// </para>
/// </remarks>
public sealed partial class ReportingService
{
    // ─────────────────────────── Codes ───────────────────────────

    /// <summary>Annex 6h — distribution of <see cref="AuditLog"/> rows by <see cref="AuditSeverity"/> in window.</summary>
    private const string AuditEventsBySeverityCode = "RPT-AUDIT-EVENTS-BY-SEVERITY";

    /// <summary>Annex 6h — daily count of <see cref="Document"/> rows created in window.</summary>
    private const string DocumentUploadVolumesCode = "RPT-DOCUMENT-UPLOAD-VOLUMES";

    /// <summary>Annex 6h — daily count of login audit events (<c>USER.LOGIN.*</c>) in window.</summary>
    private const string LoginEventsPerDayCode = "RPT-LOGIN-EVENTS-PER-DAY";

    /// <summary>Annex 6h — distinct audit-log actors over the trailing 30 days from <c>asOfUtc</c>.</summary>
    private const string ActiveUsersLast30DaysCode = "RPT-ACTIVE-USERS-LAST-30D";

    /// <summary>Annex 6h — examination-duration percentiles per service code over closed-in-window dossiers.</summary>
    private const string DossierExaminationDurationCode = "RPT-DOSSIER-EXAMINATION-DURATION";

    // ─────────────────────────── Shared header tables ───────────────────────────

    /// <summary>
    /// Canonical column-header row for <c>RPT-DOCUMENT-UPLOAD-VOLUMES</c>. Hoisted to a static
    /// readonly field so the empty-window fast-path and the populated-window branch share the
    /// same literal — satisfies CA1861 (avoid repeated constant-array allocations).
    /// </summary>
    private static readonly string[] DocumentUploadVolumesHeaders = ["Day (UTC)", "Upload Count"];

    /// <summary>
    /// Canonical column-header row for <c>RPT-LOGIN-EVENTS-PER-DAY</c>. Hoisted to a static
    /// readonly field so the empty-window fast-path and the populated-window branch share the
    /// same literal — satisfies CA1861 (avoid repeated constant-array allocations).
    /// </summary>
    private static readonly string[] LoginEventsPerDayHeaders = ["Day (UTC)", "Success Count", "Failure Count"];

    // ─────────────────────────── Dispatcher hooks ───────────────────────────

    /// <summary>True when the supplied code is one of the Annex 6h report identifiers.</summary>
    /// <param name="code">Caller-supplied report code (case-sensitive — codes are stable contract).</param>
    /// <remarks>
    /// The check chains in <see cref="IsAnnex6iReportCode"/> so the upstream dispatcher
    /// recognises the Annex 6i batch without further edits to <see cref="IsAnnex6gReportCode"/>
    /// or any earlier link in the chain.
    /// </remarks>
    private static bool IsAnnex6hReportCode(string code)
        => code is AuditEventsBySeverityCode or DocumentUploadVolumesCode or LoginEventsPerDayCode
            or ActiveUsersLast30DaysCode or DossierExaminationDurationCode
            || IsAnnex6iReportCode(code);

    /// <summary>
    /// Routes an Annex 6h report code to its materialiser. Returns
    /// <see cref="ErrorCodes.NotFound"/> for unknown codes so the failure shape matches the
    /// earlier Annex 6 dispatchers.
    /// </summary>
    /// <param name="reportCode">Stable report code, e.g. <c>RPT-AUDIT-EVENTS-BY-SEVERITY</c>.</param>
    /// <param name="parameters">Parsed JSON parameter document.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAnnex6hDatasetAsync(
        string reportCode,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        return reportCode switch
        {
            AuditEventsBySeverityCode => await BuildAuditEventsBySeverityAsync(parameters, cancellationToken).ConfigureAwait(false),
            DocumentUploadVolumesCode => await BuildDocumentUploadVolumesAsync(parameters, cancellationToken).ConfigureAwait(false),
            LoginEventsPerDayCode => await BuildLoginEventsPerDayAsync(parameters, cancellationToken).ConfigureAwait(false),
            ActiveUsersLast30DaysCode => await BuildActiveUsersLast30DaysAsync(parameters, cancellationToken).ConfigureAwait(false),
            DossierExaminationDurationCode => await BuildDossierExaminationDurationAsync(parameters, cancellationToken).ConfigureAwait(false),
            // Annex 6i batch — appended without disturbing the original five branches above.
            _ when IsAnnex6iReportCode(reportCode) =>
                await BuildAnnex6iDatasetAsync(reportCode, parameters, cancellationToken).ConfigureAwait(false),
            _ => Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown report code"),
        };
    }

    // ─────────────────────────── RPT-AUDIT-EVENTS-BY-SEVERITY ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-AUDIT-EVENTS-BY-SEVERITY</c> — count of <see cref="AuditLog"/> rows whose
    /// <see cref="AuditLog.EventAtUtc"/> falls in the UTC window <c>[fromUtc, toUtc)</c>, grouped
    /// by <see cref="AuditLog.Severity"/>. Soft-deleted rows are excluded. The four
    /// <see cref="AuditSeverity"/> values (Information, Notice, Sensitive, Critical) are emitted
    /// densely — buckets with zero traffic still appear so downstream consumers can rely on a
    /// stable row shape.
    /// </summary>
    /// <remarks>
    /// The window predicate uses <see cref="AuditLog.EventAtUtc"/> (the moment the audited event
    /// occurred) rather than <see cref="AuditableEntity.CreatedAtUtc"/> (the moment the row was
    /// written) — the two coincide for synchronous writes but can diverge when audit rows are
    /// back-loaded from <c>MLog</c> mirroring (TOR SEC 056). The report owners care about when
    /// the event happened, so <c>EventAtUtc</c> drives the filter.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAuditEventsBySeverityAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var fromUtc = ReadUtcDate(parameters, "fromUtc");
        var toUtc = ReadUtcDate(parameters, "toUtc");
        if (fromUtc is null || toUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameters 'fromUtc' and 'toUtc' are required (UTC ISO 8601).");
        }
        var fromInstant = fromUtc.Value;
        var toInstant = toUtc.Value;

        // Pull the severity column only; grouping in memory keeps the predicate identical
        // across EF providers and lets us assemble the dense-histogram output trivially.
        var severities = await _db.AuditLogs
            .Where(a => a.IsActive
                && a.EventAtUtc >= fromInstant
                && a.EventAtUtc < toInstant)
            .Select(a => a.Severity)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Dense-row contract: every AuditSeverity value gets a row, in enum-declaration order
        // (Information, Notice, Sensitive, Critical).
        var severityOrder = new[]
        {
            AuditSeverity.Information,
            AuditSeverity.Notice,
            AuditSeverity.Sensitive,
            AuditSeverity.Critical,
        };

        var headers = new[] { "Severity", "Count" };
        var data = severityOrder.Select(s =>
        {
            var count = severities.LongCount(v => v == s);
            return new[]
            {
                s.ToString(),
                count.ToString(CultureInfo.InvariantCulture),
            };
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DOCUMENT-UPLOAD-VOLUMES ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOCUMENT-UPLOAD-VOLUMES</c> — count of <see cref="Document"/> rows whose
    /// <see cref="AuditableEntity.CreatedAtUtc"/> falls in the UTC window
    /// <c>[fromUtc, toUtc)</c>, grouped by calendar day (UTC). Soft-deleted rows are excluded.
    /// Days with zero uploads are emitted with a zero count so the output forms a dense daily
    /// series that downstream consumers can chart without gap-filling on their side.
    /// </summary>
    /// <remarks>
    /// Calendar bucketing uses <see cref="DateTime.Date"/> on the UTC timestamp, so a row with
    /// <c>CreatedAtUtc</c> = <c>2026-05-20T23:59:59Z</c> lands in the <c>2026-05-20</c> bucket,
    /// not the next day. The dense range spans <c>fromUtc.Date</c> (inclusive) to
    /// <c>toUtc.Date</c> (exclusive) — a half-open window so a caller using midnight boundaries
    /// (e.g. <c>[2026-05-01, 2026-06-01)</c>) gets exactly the month's 31 rows.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDocumentUploadVolumesAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var fromUtc = ReadUtcDate(parameters, "fromUtc");
        var toUtc = ReadUtcDate(parameters, "toUtc");
        if (fromUtc is null || toUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameters 'fromUtc' and 'toUtc' are required (UTC ISO 8601).");
        }
        var fromInstant = fromUtc.Value;
        var toInstant = toUtc.Value;
        if (fromInstant >= toInstant)
        {
            // Defensive — caller supplied an empty/inverted window. Return an empty result set
            // with the canonical header shape so consumers don't crash on the missing rows.
            return Result<Dataset>.Success(new Dataset(
                DocumentUploadVolumesHeaders,
                new List<string[]>()));
        }

        // Pull the creation timestamps; calendar bucketing happens in memory because
        // DateTime.Date is not provider-translatable on the InMemory provider.
        var createdTimes = await _db.Documents
            .Where(d => d.IsActive
                && d.CreatedAtUtc >= fromInstant
                && d.CreatedAtUtc < toInstant)
            .Select(d => d.CreatedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Bucket-by-day, then gap-fill across the full half-open day range so consumers can rely
        // on the dense daily series contract.
        var byDay = createdTimes
            .GroupBy(t => t.Date)
            .ToDictionary(g => g.Key, g => g.LongCount());

        var data = new List<string[]>();
        for (var day = fromInstant.Date; day < toInstant.Date || (day == toInstant.Date && toInstant.TimeOfDay > TimeSpan.Zero); day = day.AddDays(1))
        {
            var count = byDay.TryGetValue(day, out var c) ? c : 0L;
            data.Add(new[]
            {
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                count.ToString(CultureInfo.InvariantCulture),
            });
        }

        return Result<Dataset>.Success(new Dataset(DocumentUploadVolumesHeaders, data));
    }

    // ─────────────────────────── RPT-LOGIN-EVENTS-PER-DAY ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-LOGIN-EVENTS-PER-DAY</c> — daily count of login-related audit events inside
    /// the UTC window <c>[fromUtc, toUtc)</c>. A "login event" is any <see cref="AuditLog"/> row
    /// whose <see cref="AuditLog.EventCode"/> starts with <c>USER.LOGIN.</c> (e.g.
    /// <c>USER.LOGIN.SUCCESS</c>, <c>USER.LOGIN.FAILURE</c>). The output is a dense daily series
    /// with three columns: day, success count, failure count. Days with no traffic are emitted
    /// with zeroes so the series can be charted without client-side gap-filling.
    /// </summary>
    /// <remarks>
    /// The success / failure split keys off the event-code suffix: codes ending in
    /// <c>.SUCCESS</c> count as successful, everything else under <c>USER.LOGIN.</c> counts as a
    /// failure (covers <c>FAILURE</c>, <c>LOCKED</c>, <c>EXPIRED</c>, and any future failure
    /// sub-codes added without requiring a report change). The bucketing uses the
    /// <see cref="AuditLog.EventAtUtc"/> timestamp.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildLoginEventsPerDayAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var fromUtc = ReadUtcDate(parameters, "fromUtc");
        var toUtc = ReadUtcDate(parameters, "toUtc");
        if (fromUtc is null || toUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameters 'fromUtc' and 'toUtc' are required (UTC ISO 8601).");
        }
        var fromInstant = fromUtc.Value;
        var toInstant = toUtc.Value;
        if (fromInstant >= toInstant)
        {
            return Result<Dataset>.Success(new Dataset(
                LoginEventsPerDayHeaders,
                new List<string[]>()));
        }

        // Pull (EventAtUtc, EventCode) pairs for any in-window USER.LOGIN.* event. EF.Functions.Like
        // gives us a provider-portable startswith filter that translates to SQL LIKE on Postgres
        // and to a case-insensitive substring match on the InMemory provider.
        var raw = await _db.AuditLogs
            .Where(a => a.IsActive
                && a.EventAtUtc >= fromInstant
                && a.EventAtUtc < toInstant
                && EF.Functions.Like(a.EventCode, "USER.LOGIN.%"))
            .Select(a => new { a.EventAtUtc, a.EventCode })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Bucket per day with split between success / failure. Anything that isn't a SUCCESS
        // under USER.LOGIN.* counts as a failure variant.
        var byDay = raw
            .GroupBy(r => r.EventAtUtc.Date)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Success = g.LongCount(r => r.EventCode.EndsWith(".SUCCESS", StringComparison.Ordinal)),
                    Failure = g.LongCount(r => !r.EventCode.EndsWith(".SUCCESS", StringComparison.Ordinal)),
                });

        var data = new List<string[]>();
        for (var day = fromInstant.Date; day < toInstant.Date || (day == toInstant.Date && toInstant.TimeOfDay > TimeSpan.Zero); day = day.AddDays(1))
        {
            if (byDay.TryGetValue(day, out var counts))
            {
                data.Add(new[]
                {
                    day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    counts.Success.ToString(CultureInfo.InvariantCulture),
                    counts.Failure.ToString(CultureInfo.InvariantCulture),
                });
            }
            else
            {
                data.Add(new[]
                {
                    day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "0",
                    "0",
                });
            }
        }

        return Result<Dataset>.Success(new Dataset(LoginEventsPerDayHeaders, data));
    }

    // ─────────────────────────── RPT-ACTIVE-USERS-LAST-30D ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-ACTIVE-USERS-LAST-30D</c> — distinct active-user count over the trailing
    /// 30-day window ending at the supplied <c>asOfUtc</c> moment. "Active" is defined as an
    /// authenticated principal who produced at least one <see cref="AuditLog"/> row in that
    /// window — the report uses the audit log as the activity proxy because it is the system
    /// of record for every authenticated action (TOR SEC 042). Distinctness keys off
    /// <see cref="AuditLog.ActorId"/>; the literal sentinel <c>"system"</c> is excluded so
    /// machine-driven background jobs do not inflate the count.
    /// </summary>
    /// <remarks>
    /// The report carries two parameters: <c>asOfUtc</c> (window end, exclusive) and an
    /// optional <c>windowDays</c> override (defaults to 30). The output is a single summary row
    /// — Window From, Window To, Active User Count — rather than a per-user row, because the
    /// downstream KPI is the headline number and per-user PII would multiply the audit cost.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>asOfUtc</c>; <c>windowDays</c> optional.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildActiveUsersLast30DaysAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var asOfUtc = ReadUtcDate(parameters, "asOfUtc");
        if (asOfUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'asOfUtc' is required (UTC ISO 8601).");
        }
        var asOf = asOfUtc.Value;

        // windowDays — optional override; defaults to 30 to match the report code's contract.
        // Clamp to [1, 366] so a caller cannot turn the report into an all-time scan, and so
        // a negative value cannot invert the window.
        var windowDays = ReadInt(parameters, "windowDays") ?? 30;
        windowDays = Math.Clamp(windowDays, 1, 366);

        var fromInstant = asOf.AddDays(-windowDays);

        // Pull every distinct non-system actor in the window. The "system" sentinel is the
        // ActorId used by background jobs / migrations / health probes (see ReportingService
        // and AuditLogger usages); it is excluded so the KPI reflects human users only.
        var distinctActors = await _db.AuditLogs
            .Where(a => a.IsActive
                && a.EventAtUtc >= fromInstant
                && a.EventAtUtc < asOf
                && a.ActorId != "system")
            .Select(a => a.ActorId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Window From (UTC)", "Window To (UTC)", "Active User Count" };
        var data = new List<string[]>
        {
            new[]
            {
                fromInstant.ToString("o", CultureInfo.InvariantCulture),
                asOf.ToString("o", CultureInfo.InvariantCulture),
                distinctActors.Count.ToString(CultureInfo.InvariantCulture),
            },
        };

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DOSSIER-EXAMINATION-DURATION ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOSSIER-EXAMINATION-DURATION</c> — average and p50/p90/p95 examination
    /// duration (in days) per <see cref="ServicePassport.Code"/>. The examination duration is
    /// <c>ClosedAtUtc − AcceptedAtUtc</c>, capturing only the in-examination phase of the dossier
    /// lifecycle (in contrast to <c>RPT-DOSSIER-LIFECYCLE-TIME</c>, which measures the full
    /// <c>ClosedAtUtc − CreatedAtUtc</c> span). Source: <see cref="Dossier"/> rows whose
    /// <see cref="Dossier.ClosedAtUtc"/> falls in the half-open UTC window
    /// <c>[fromUtc, toUtc)</c> and whose <see cref="Dossier.AcceptedAtUtc"/> is populated
    /// (dossiers that never entered examination are excluded).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Percentiles are computed in memory via the nearest-rank method
    /// (<c>ceil(p × n) − 1</c> index after sorting ascending). The nearest-rank definition is
    /// chosen over linear interpolation so the result is always a member of the underlying
    /// sample — easier to reason about for SLA reporting. Avg / p50 / p90 / p95 are rounded to
    /// two decimal places using <see cref="CultureInfo.InvariantCulture"/>'s <c>F2</c> format
    /// so locale never injects thousand separators or decimal-comma alternatives.
    /// </para>
    /// <para>
    /// In-memory percentile calculation (rather than a server-side
    /// <c>percentile_cont</c> / <c>percentile_disc</c>) keeps the predicate identical across EF
    /// providers — the InMemory provider used in integration tests does not translate those
    /// SQL constructs uniformly. The volume of in-window closed dossiers per service is
    /// expected to stay in the thousands at most, well within memory budgets.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDossierExaminationDurationAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var fromUtc = ReadUtcDate(parameters, "fromUtc");
        var toUtc = ReadUtcDate(parameters, "toUtc");
        if (fromUtc is null || toUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameters 'fromUtc' and 'toUtc' are required (UTC ISO 8601).");
        }
        var fromInstant = fromUtc.Value;
        var toInstant = toUtc.Value;

        // Pull per-dossier (service code, accepted, closed) for every in-window closed dossier
        // that actually entered the examination phase. The percentile math runs in memory —
        // see the XML doc for the rationale (nearest-rank, provider-portable, low volume).
        var query =
            from d in _db.Dossiers
            where d.IsActive
                  && d.AcceptedAtUtc != null
                  && d.ClosedAtUtc != null
                  && d.ClosedAtUtc >= fromInstant
                  && d.ClosedAtUtc < toInstant
            join a in _db.Applications on d.ApplicationId equals a.Id
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            select new
            {
                ServiceCode = p.Code,
                AcceptedAtUtc = d.AcceptedAtUtc!.Value,
                ClosedAtUtc = d.ClosedAtUtc!.Value,
            };

        var raw = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = raw
            .GroupBy(r => r.ServiceCode, StringComparer.Ordinal)
            .Select(g =>
            {
                var durations = g
                    .Select(x => (x.ClosedAtUtc - x.AcceptedAtUtc).TotalDays)
                    .OrderBy(x => x)
                    .ToList();
                return new
                {
                    ServiceCode = g.Key,
                    Count = durations.Count,
                    Avg = durations.Average(),
                    P50 = NearestRank(durations, 0.50),
                    P90 = NearestRank(durations, 0.90),
                    P95 = NearestRank(durations, 0.95),
                };
            })
            .OrderBy(r => r.ServiceCode, StringComparer.Ordinal)
            .ToList();

        var headers = new[]
        {
            "Service Code", "Closed Count", "Avg Days", "P50 Days", "P90 Days", "P95 Days",
        };
        var data = grouped.Select(r => new[]
        {
            r.ServiceCode,
            r.Count.ToString(CultureInfo.InvariantCulture),
            r.Avg.ToString("F2", CultureInfo.InvariantCulture),
            r.P50.ToString("F2", CultureInfo.InvariantCulture),
            r.P90.ToString("F2", CultureInfo.InvariantCulture),
            r.P95.ToString("F2", CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    /// <summary>
    /// Nearest-rank percentile over an <em>already sorted ascending</em> list of values. The
    /// rank is <c>ceil(p × n)</c>; the returned value sits at index <c>rank − 1</c> after
    /// clamping into <c>[0, n − 1]</c>. Returns <c>0</c> for an empty sample so callers never
    /// face a divide-by-zero.
    /// </summary>
    /// <param name="sortedAscending">Sample values, sorted ascending. Modifying after the call is safe.</param>
    /// <param name="percentile">Percentile in the inclusive range <c>[0, 1]</c>.</param>
    /// <returns>The nearest-rank percentile value, or zero for an empty sample.</returns>
    private static double NearestRank(IReadOnlyList<double> sortedAscending, double percentile)
    {
        if (sortedAscending.Count == 0) return 0.0;
        // ceil(p × n) − 1, clamped into the valid index range. For p = 0 we still want the
        // smallest value (index 0), which Math.Ceiling(0) = 0 followed by clamp(-1, 0, n-1)
        // = 0 produces correctly.
        var rank = (int)Math.Ceiling(percentile * sortedAscending.Count);
        var index = Math.Clamp(rank - 1, 0, sortedAscending.Count - 1);
        return sortedAscending[index];
    }
}
