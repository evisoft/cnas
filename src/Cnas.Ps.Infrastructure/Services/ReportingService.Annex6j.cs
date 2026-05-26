using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Annex 6j named-report extensions for <see cref="ReportingService"/>. The eleventh batch of
/// Annex 6 reports lives in its own partial file so that the earlier batches
/// (<c>ReportingService.Annex6.cs</c> through <c>ReportingService.Annex6i.cs</c>) remain
/// decoupled. The new codes are recognised by <see cref="IsAnnex6jReportCode"/> and dispatched
/// by <see cref="BuildAnnex6jDatasetAsync"/>; the Annex 6i dispatcher chains into this file via
/// a single <c>_ when</c> arm so adding the batch only required one minimal upstream edit.
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
/// (per-outcome closure histogram, per-passport-per-month dense matrix, per-recipient top-N
/// notification count, per-actor top-N audit count, daily verdict productivity series), so no
/// Sqid encoding appears in their output. Two of them apply a dense-output contract:
/// <c>RPT-APPLICATIONS-BY-PASSPORT-MONTHLY</c> emits one row per (passport, month) tuple
/// across the full window even when a particular month carried zero traffic for a passport,
/// and <c>RPT-DOCUMENT-VERDICTS-OVER-TIME</c> emits one row per calendar day in
/// <c>[fromUtc, toUtc)</c> (gap-filled with zeroes).
/// </para>
/// <para>
/// All EF queries follow the provider-seam pattern used by Annex 6h / Annex 6i — Postgres-only
/// helpers (server-side <c>DATE_TRUNC</c>, <c>percentile_cont</c>, etc.) are avoided in favour
/// of pulling the minimum projection to memory and bucketing client-side so the InMemory
/// provider used by the integration tests behaves identically to Postgres.
/// </para>
/// </remarks>
public sealed partial class ReportingService
{
    // ─────────────────────────── Codes ───────────────────────────

    /// <summary>Annex 6j — count of closed <see cref="Dossier"/> rows by mapped outcome in window.</summary>
    private const string DossiersClosedByOutcomeCode = "RPT-DOSSIERS-CLOSED-BY-OUTCOME";

    /// <summary>Annex 6j — dense (<see cref="ServicePassport.Code"/>, month) count of <see cref="ServiceApplication"/> rows in window.</summary>
    private const string ApplicationsByPassportMonthlyCode = "RPT-APPLICATIONS-BY-PASSPORT-MONTHLY";

    /// <summary>Annex 6j — top-N citizens (recipients) by <see cref="Notification"/> count in window.</summary>
    private const string NotificationsByCitizenCode = "RPT-NOTIFICATIONS-BY-CITIZEN";

    /// <summary>Annex 6j — top-N actors by <see cref="AuditLog"/> event count in window.</summary>
    private const string AuditEventsByActorCode = "RPT-AUDIT-EVENTS-BY-ACTOR";

    /// <summary>Annex 6j — daily count of <see cref="Document"/> rows whose <see cref="Document.Verdict"/> was recorded in window.</summary>
    private const string DocumentVerdictsOverTimeCode = "RPT-DOCUMENT-VERDICTS-OVER-TIME";

    // ─────────────────────────── Shared header tables ───────────────────────────

    /// <summary>
    /// Canonical column-header row for <c>RPT-DOSSIERS-CLOSED-BY-OUTCOME</c>. Hoisted to a
    /// static readonly field so the empty-window fast-path and the populated-window branch
    /// share the same literal — satisfies CA1861 (avoid repeated constant-array allocations).
    /// </summary>
    private static readonly string[] DossiersClosedByOutcomeHeaders = ["Outcome", "Count"];

    /// <summary>
    /// Canonical column-header row for <c>RPT-APPLICATIONS-BY-PASSPORT-MONTHLY</c>. Hoisted to
    /// a static readonly field so the empty-window fast-path and the populated-window branch
    /// share the same literal — satisfies CA1861.
    /// </summary>
    private static readonly string[] ApplicationsByPassportMonthlyHeaders =
        ["Service Code", "Month (UTC)", "Application Count"];

    /// <summary>
    /// Canonical column-header row for <c>RPT-DOCUMENT-VERDICTS-OVER-TIME</c>. Hoisted to a
    /// static readonly field so the empty-window fast-path and the populated-window branch
    /// share the same literal — satisfies CA1861.
    /// </summary>
    private static readonly string[] DocumentVerdictsOverTimeHeaders = ["Day (UTC)", "Verdict Count"];

    // ─────────────────────────── Dispatcher hooks ───────────────────────────

    /// <summary>True when the supplied code is one of the Annex 6j report identifiers.</summary>
    /// <param name="code">Caller-supplied report code (case-sensitive — codes are stable contract).</param>
    /// <remarks>
    /// This file is currently the terminal link in the Annex 6 chain — no further batches are
    /// chained from here. A future Annex 6k batch should chain in <see cref="IsAnnex6jReportCode"/>
    /// via a trailing <c>|| IsAnnex6kReportCode(code)</c> alternation, mirroring the pattern
    /// used by every earlier link.
    /// </remarks>
    private static bool IsAnnex6jReportCode(string code)
        => code is DossiersClosedByOutcomeCode or ApplicationsByPassportMonthlyCode
            or NotificationsByCitizenCode or AuditEventsByActorCode or DocumentVerdictsOverTimeCode;

    /// <summary>
    /// Routes an Annex 6j report code to its materialiser. Returns
    /// <see cref="ErrorCodes.NotFound"/> for unknown codes so the failure shape matches the
    /// earlier Annex 6 dispatchers.
    /// </summary>
    /// <param name="reportCode">Stable report code, e.g. <c>RPT-DOSSIERS-CLOSED-BY-OUTCOME</c>.</param>
    /// <param name="parameters">Parsed JSON parameter document.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAnnex6jDatasetAsync(
        string reportCode,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        return reportCode switch
        {
            DossiersClosedByOutcomeCode => await BuildDossiersClosedByOutcomeAsync(parameters, cancellationToken).ConfigureAwait(false),
            ApplicationsByPassportMonthlyCode => await BuildApplicationsByPassportMonthlyAsync(parameters, cancellationToken).ConfigureAwait(false),
            NotificationsByCitizenCode => await BuildNotificationsByCitizenAsync(parameters, cancellationToken).ConfigureAwait(false),
            AuditEventsByActorCode => await BuildAuditEventsByActorAsync(parameters, cancellationToken).ConfigureAwait(false),
            DocumentVerdictsOverTimeCode => await BuildDocumentVerdictsOverTimeAsync(parameters, cancellationToken).ConfigureAwait(false),
            _ => Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown report code"),
        };
    }

    // ─────────────────────────── RPT-DOSSIERS-CLOSED-BY-OUTCOME ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOSSIERS-CLOSED-BY-OUTCOME</c> — count of <see cref="Dossier"/> rows whose
    /// <see cref="Dossier.ClosedAtUtc"/> falls in the half-open UTC window
    /// <c>[fromUtc, toUtc)</c>, grouped by the closure outcome derived from the joined
    /// <see cref="ServiceApplication.Status"/>. Outcomes are mapped using the same three-bucket
    /// scheme as <c>RPT-DOS-CLOSED-PERIOD</c> (<c>Approved</c> / <c>Rejected</c> / <c>Cancelled</c>)
    /// so the two reports can be cross-referenced. Soft-deleted dossiers and applications are
    /// both excluded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This report is distinct from <c>RPT-DOSSIER-LIFECYCLE-TIME</c> (which is durations per
    /// service) and <c>RPT-EXAMINER-OUTCOMES</c> (which is per-examiner verdicts). The KPI
    /// here is the headline closed-by-outcome distribution across the entire system, useful
    /// for a single-glance dashboard tile.
    /// </para>
    /// <para>
    /// The three outcome rows are emitted densely so consumers can rely on a stable shape —
    /// a window with zero <c>Cancelled</c> closures still emits a <c>Cancelled,0</c> row. The
    /// in-memory bucketing keeps the query identical across EF providers — the InMemory
    /// provider does not reliably translate a server-side <c>GROUP BY</c> over a CASE-style
    /// outcome expression.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDossiersClosedByOutcomeAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Join Dossier ⨝ Application on the application FK; pull only the Status column for
        // every in-window closed dossier. Outcome mapping happens in memory via the shared
        // MapFinalOutcome helper (defined in Annex 6b).
        var query =
            from d in _db.Dossiers
            where d.IsActive
                  && d.ClosedAtUtc != null
                  && d.ClosedAtUtc >= fromInstant
                  && d.ClosedAtUtc < toInstant
            join a in _db.Applications on d.ApplicationId equals a.Id
            where a.IsActive
            select a.Status;

        var statuses = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        // Dense-row contract: every outcome label gets a row in a stable order. The label
        // strings match RPT-DOS-CLOSED-PERIOD's MapFinalOutcome output so the two reports
        // can be paired in a dashboard.
        var outcomeOrder = new[] { "Approved", "Rejected", "Cancelled" };
        var bucketed = statuses
            .GroupBy(s => MapFinalOutcome(s), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.LongCount(), StringComparer.Ordinal);

        var data = outcomeOrder.Select(label =>
        {
            var count = bucketed.TryGetValue(label, out var c) ? c : 0L;
            return new[]
            {
                label,
                count.ToString(CultureInfo.InvariantCulture),
            };
        }).ToList();

        return Result<Dataset>.Success(new Dataset(DossiersClosedByOutcomeHeaders, data));
    }

    // ─────────────────────────── RPT-APPLICATIONS-BY-PASSPORT-MONTHLY ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-APPLICATIONS-BY-PASSPORT-MONTHLY</c> — count of <see cref="ServiceApplication"/>
    /// rows whose <see cref="AuditableEntity.CreatedAtUtc"/> falls in the half-open UTC window
    /// <c>[fromUtc, toUtc)</c>, grouped by the owning <see cref="ServicePassport.Code"/> AND the
    /// calendar month (UTC). The output is a dense (passport × month) matrix — every passport
    /// that carried at least one application anywhere in the window emits a row for every month
    /// in the window, with a zero count when that particular cell was empty. Soft-deleted
    /// applications and passports are both excluded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <c>RPT-PASSPORT-USAGE</c> (which is a single per-passport total) and
    /// <c>RPT-NEW-APPLICATIONS-DAILY</c> (which is a per-day series across all passports). The
    /// monthly-by-passport breakdown lets product owners spot seasonal traffic patterns per
    /// service without a full cross-tabulation in the UI.
    /// </para>
    /// <para>
    /// Months are identified by the first day of the month at UTC midnight. The dense range
    /// spans <c>fromUtc</c>'s month (inclusive) through <c>toUtc</c>'s month (exclusive when
    /// <c>toUtc</c> is exactly on a month boundary, inclusive otherwise) — matching the
    /// half-open window semantics used by every other Annex 6 report. Passports that produced
    /// zero applications anywhere in the window are not emitted at all; the report focuses on
    /// active services so a long-tail of disabled passports does not bloat the output.
    /// </para>
    /// <para>
    /// Rows are ordered by ServiceCode (Ordinal), then Month ascending — a deterministic shape
    /// the CSV consumer can rely on. The aggregation runs in memory after pulling
    /// (CreatedAtUtc, Code) pairs because <c>DateTime.Date</c> / month arithmetic is not
    /// translatable on the InMemory provider.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildApplicationsByPassportMonthlyAsync(JsonElement parameters, CancellationToken cancellationToken)
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
                ApplicationsByPassportMonthlyHeaders,
                new List<string[]>()));
        }

        // Pull (CreatedAtUtc, passport Code) for every in-window active application joined to
        // an active passport. Bucketing into (passport, month) happens in memory.
        var query =
            from a in _db.Applications
            where a.IsActive
                  && a.CreatedAtUtc >= fromInstant
                  && a.CreatedAtUtc < toInstant
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            where p.IsActive
            select new
            {
                CreatedAtUtc = a.CreatedAtUtc,
                ServiceCode = p.Code,
            };

        var raw = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        // Build the dense month range. A month is identified by its first-of-month UTC midnight.
        var firstMonth = new DateTime(fromInstant.Year, fromInstant.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var months = new List<DateTime>();
        for (var m = firstMonth; m < toInstant; m = m.AddMonths(1))
        {
            months.Add(m);
        }

        // Bucket counts per (passport, month). Months are normalised to the first-of-month.
        var byCell = raw
            .GroupBy(r => (r.ServiceCode, Month: new DateTime(r.CreatedAtUtc.Year, r.CreatedAtUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc)))
            .ToDictionary(g => g.Key, g => g.LongCount());

        // Distinct passport codes that carried at least one application anywhere in the window.
        // Ordering ensures a stable Ordinal alphabetic shape.
        var passports = raw
            .Select(r => r.ServiceCode)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var data = new List<string[]>();
        foreach (var code in passports)
        {
            foreach (var month in months)
            {
                var count = byCell.TryGetValue((code, month), out var c) ? c : 0L;
                data.Add(new[]
                {
                    code,
                    month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    count.ToString(CultureInfo.InvariantCulture),
                });
            }
        }

        return Result<Dataset>.Success(new Dataset(ApplicationsByPassportMonthlyHeaders, data));
    }

    // ─────────────────────────── RPT-NOTIFICATIONS-BY-CITIZEN ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-NOTIFICATIONS-BY-CITIZEN</c> — top-N citizens (recipients) by
    /// <see cref="Notification"/> count whose <see cref="AuditableEntity.CreatedAtUtc"/> falls
    /// in the half-open UTC window <c>[fromUtc, toUtc)</c>. The recipient is resolved through
    /// <see cref="Notification.RecipientUserId"/> → <see cref="UserProfile"/>; the column value
    /// is <see cref="UserProfile.LocalLogin"/> (or <see cref="UserProfile.DisplayName"/>
    /// fallback). Soft-deleted notifications are excluded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The KPI highlights workflows that hammer a single recipient — useful for spotting
    /// runaway loops, over-eager dispatchers, or spam-like patterns. The default
    /// <c>topN</c> truncates to <see cref="DefaultMaxRows"/>; callers tuning a dashboard tile
    /// typically pass <c>topN=10</c> or <c>topN=25</c>. <c>topN</c> is clamped to
    /// <c>[1, 5000]</c> — the lower bound prevents a degenerate empty projection, the upper
    /// bound matches <see cref="DefaultMaxRows"/> so a single report stays within the standard
    /// response budget.
    /// </para>
    /// <para>
    /// Recipients whose user profile is soft-deleted or otherwise unresolved fall back to a
    /// <c>user#{id}</c> sentinel so the row count still sums to the total in-window volume —
    /// matching the unassigned-bucket pattern in <c>RPT-DOSSIERS-OPEN-BY-EXAMINER</c>. Rows
    /// are ordered by Count desc, then Username (Ordinal) for stable tie-breaks.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>; <c>topN</c> optional.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildNotificationsByCitizenAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        var topN = ReadInt(parameters, "topN") ?? DefaultMaxRows;
        topN = Math.Clamp(topN, 1, DefaultMaxRows);

        // Pull RecipientUserId for every in-window active notification. Username resolution
        // runs in a second pass so the predicate is identical across EF providers.
        var recipientIds = await _db.Notifications
            .Where(n => n.IsActive
                && n.CreatedAtUtc >= fromInstant
                && n.CreatedAtUtc < toInstant)
            .Select(n => n.RecipientUserId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Resolve distinct ids to (Id, Username) pairs. Soft-deleted user profiles are filtered
        // OUT of the join — a notification to a deleted user still surfaces via the user#{id}
        // sentinel branch below.
        var distinctIds = recipientIds.Distinct().ToList();
        var users = distinctIds.Count == 0
            ? new List<(long Id, string Username)>()
            : (await _db.UserProfiles
                .Where(u => u.IsActive && distinctIds.Contains(u.Id))
                .Select(u => new { u.Id, u.LocalLogin, u.DisplayName })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(u => (u.Id, Username: u.LocalLogin ?? u.DisplayName))
                .ToList();

        var idToUser = users.ToDictionary(u => u.Id, u => u.Username, EqualityComparer<long>.Default);

        // Bucket counts per username. Unknown / soft-deleted recipients fall back to user#{id}.
        var grouped = recipientIds
            .Select(id => idToUser.TryGetValue(id, out var name) ? name : $"user#{id}")
            .GroupBy(n => n, StringComparer.Ordinal)
            .Select(g => new { Username = g.Key, Count = g.LongCount() })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Username, StringComparer.Ordinal)
            .Take(topN)
            .ToList();

        var headers = new[] { "Recipient Username", "Notification Count" };
        var data = grouped.Select(r => new[]
        {
            r.Username,
            r.Count.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-AUDIT-EVENTS-BY-ACTOR ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-AUDIT-EVENTS-BY-ACTOR</c> — top-N actors by <see cref="AuditLog"/> event
    /// count whose <see cref="AuditLog.EventAtUtc"/> falls in the half-open UTC window
    /// <c>[fromUtc, toUtc)</c>. Soft-deleted rows are excluded. The <c>"system"</c> sentinel
    /// actor used by background jobs (and excluded from <c>RPT-ACTIVE-USERS-LAST-30D</c>) is
    /// retained here so the report can surface runaway background workers; callers who only
    /// care about human actors can post-filter the row by inspecting the <c>Actor</c> column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The KPI surfaces actors with anomalously high audit volume — useful for spotting
    /// compromised accounts, bot-driven scraping, or misbehaving service accounts. Distinct
    /// from <c>RPT-AUDIT-EVENTS-BY-ACTION</c> (which is keyed on event code) — both reports
    /// share the same window-driver column (<see cref="AuditLog.EventAtUtc"/>) so a dashboard
    /// can pair them on the same time slice.
    /// </para>
    /// <para>
    /// The default <c>topN</c> falls back to <see cref="DefaultMaxRows"/> so an
    /// unconstrained call still respects the standard row cap; typical callers pass
    /// <c>topN=10</c> for a leaderboard tile. The clamp is <c>[1, 5000]</c>, matching the rest
    /// of Annex 6.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>; <c>topN</c> optional.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAuditEventsByActorAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        var topN = ReadInt(parameters, "topN") ?? DefaultMaxRows;
        topN = Math.Clamp(topN, 1, DefaultMaxRows);

        // Pull just the ActorId column for every in-window active audit row; counting happens
        // in memory (consistent with RPT-AUDIT-EVENTS-BY-ACTION's rationale).
        var actorIds = await _db.AuditLogs
            .Where(a => a.IsActive
                && a.EventAtUtc >= fromInstant
                && a.EventAtUtc < toInstant)
            .Select(a => a.ActorId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = actorIds
            .GroupBy(c => c, StringComparer.Ordinal)
            .Select(g => new { Actor = g.Key, Count = g.LongCount() })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Actor, StringComparer.Ordinal)
            .Take(topN)
            .ToList();

        var headers = new[] { "Actor", "Count" };
        var data = grouped.Select(r => new[]
        {
            r.Actor,
            r.Count.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DOCUMENT-VERDICTS-OVER-TIME ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOCUMENT-VERDICTS-OVER-TIME</c> — daily count of <see cref="Document"/>
    /// rows whose <see cref="Document.VerdictAtUtc"/> falls in the half-open UTC window
    /// <c>[fromUtc, toUtc)</c> (i.e. documents on which an examiner recorded a verdict during
    /// that day). Soft-deleted documents are excluded. The output is a dense daily series:
    /// days with zero verdicts are emitted with a zero count so consumers can chart without
    /// gap-filling on their side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The report is a productivity proxy — surfacing examiner verdict throughput across the
    /// window. A row's <see cref="Document.Verdict"/> integer value is not interrogated; any
    /// non-null verdict counts equally. The <c>VerdictAtUtc</c> filter (rather than
    /// <see cref="AuditableEntity.CreatedAtUtc"/>) ensures backlog documents that finally
    /// received a verdict during the window land in the correct day bucket — the metric is
    /// about when the verdict was recorded, not when the document was uploaded.
    /// </para>
    /// <para>
    /// Calendar bucketing uses <see cref="DateTime.Date"/> on the UTC timestamp. The dense
    /// range spans <c>fromUtc.Date</c> (inclusive) to <c>toUtc.Date</c> (exclusive on a clean
    /// midnight boundary, inclusive otherwise) — matching <c>RPT-DOCUMENT-UPLOAD-VOLUMES</c>
    /// and <c>RPT-LOGIN-EVENTS-PER-DAY</c> so the three daily series can be charted on a
    /// shared x-axis without reconciliation.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDocumentVerdictsOverTimeAsync(JsonElement parameters, CancellationToken cancellationToken)
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
                DocumentVerdictsOverTimeHeaders,
                new List<string[]>()));
        }

        // Pull just the verdict timestamps for every in-window active document that has been
        // verdict-stamped. Bucketing happens in memory because DateTime.Date is not provider-
        // translatable on the InMemory provider.
        var verdictTimes = await _db.Documents
            .Where(d => d.IsActive
                && d.Verdict != null
                && d.VerdictAtUtc != null
                && d.VerdictAtUtc >= fromInstant
                && d.VerdictAtUtc < toInstant)
            .Select(d => d.VerdictAtUtc!.Value)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Bucket-by-day, then gap-fill across the full half-open day range so consumers can
        // rely on the dense daily series contract.
        var byDay = verdictTimes
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

        return Result<Dataset>.Success(new Dataset(DocumentVerdictsOverTimeHeaders, data));
    }
}
