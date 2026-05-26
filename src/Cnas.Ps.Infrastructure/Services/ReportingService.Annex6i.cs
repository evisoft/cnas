using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Annex 6i named-report extensions for <see cref="ReportingService"/>. The tenth batch of
/// Annex 6 reports lives in its own partial file so that the earlier batches
/// (<c>ReportingService.Annex6.cs</c> through <c>ReportingService.Annex6h.cs</c>) remain
/// decoupled. The new codes are recognised by <see cref="IsAnnex6iReportCode"/> and dispatched
/// by <see cref="BuildAnnex6iDatasetAsync"/>; the Annex 6h dispatcher chains into this file via
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
/// (per-passport application count, per-event-code histogram, per-channel unread queue,
/// per-document-kind unsigned count, per-examiner open caseload), so no Sqid encoding appears
/// in their output. Two of them apply a dense-output contract:
/// <c>RPT-NOTIFICATIONS-UNREAD</c> emits one row per <see cref="NotificationChannel"/> value
/// (Email / SMS / InApp) and <c>RPT-DOSSIERS-OPEN-BY-EXAMINER</c> emits a dense row for the
/// <c>"&lt;unassigned&gt;"</c> bucket whenever there are open dossiers without an assigned
/// examiner.
/// </para>
/// </remarks>
public sealed partial class ReportingService
{
    // ─────────────────────────── Codes ───────────────────────────

    /// <summary>Annex 6i — count of <see cref="ServiceApplication"/> rows per <see cref="ServicePassport.Code"/> in window.</summary>
    private const string PassportUsageCode = "RPT-PASSPORT-USAGE";

    /// <summary>Annex 6i — distribution of <see cref="AuditLog"/> rows by <see cref="AuditLog.EventCode"/> in window.</summary>
    private const string AuditEventsByActionCode = "RPT-AUDIT-EVENTS-BY-ACTION";

    /// <summary>Annex 6i — per-channel unread <see cref="Notification"/> count as of a UTC moment.</summary>
    private const string NotificationsUnreadCode = "RPT-NOTIFICATIONS-UNREAD";

    /// <summary>Annex 6i — count of unsigned <see cref="Document"/> rows by <see cref="Document.Kind"/> in window.</summary>
    private const string DocumentsUnsignedCode = "RPT-DOCUMENTS-UNSIGNED";

    /// <summary>Annex 6i — per-examiner open dossier count (plus unassigned bucket) as of a UTC moment.</summary>
    private const string DossiersOpenByExaminerCode = "RPT-DOSSIERS-OPEN-BY-EXAMINER";

    // ─────────────────────────── Shared header tables ───────────────────────────

    /// <summary>
    /// Canonical column-header row for <c>RPT-NOTIFICATIONS-UNREAD</c>. Hoisted to a static
    /// readonly field so the empty-window fast-path and the populated-window branch share the
    /// same literal — satisfies CA1861 (avoid repeated constant-array allocations).
    /// </summary>
    private static readonly string[] NotificationsUnreadHeaders = ["Channel", "Unread Count"];

    // ─────────────────────────── Dispatcher hooks ───────────────────────────

    /// <summary>True when the supplied code is one of the Annex 6i report identifiers.</summary>
    /// <param name="code">Caller-supplied report code (case-sensitive — codes are stable contract).</param>
    /// <remarks>
    /// The check chains in <see cref="IsAnnex6jReportCode"/> so the upstream dispatcher
    /// recognises the Annex 6j batch without further edits to <see cref="IsAnnex6hReportCode"/>
    /// or any earlier link in the chain.
    /// </remarks>
    private static bool IsAnnex6iReportCode(string code)
        => code is PassportUsageCode or AuditEventsByActionCode or NotificationsUnreadCode
            or DocumentsUnsignedCode or DossiersOpenByExaminerCode
            || IsAnnex6jReportCode(code);

    /// <summary>
    /// Routes an Annex 6i report code to its materialiser. Returns
    /// <see cref="ErrorCodes.NotFound"/> for unknown codes so the failure shape matches the
    /// earlier Annex 6 dispatchers.
    /// </summary>
    /// <param name="reportCode">Stable report code, e.g. <c>RPT-PASSPORT-USAGE</c>.</param>
    /// <param name="parameters">Parsed JSON parameter document.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAnnex6iDatasetAsync(
        string reportCode,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        return reportCode switch
        {
            PassportUsageCode => await BuildPassportUsageAsync(parameters, cancellationToken).ConfigureAwait(false),
            AuditEventsByActionCode => await BuildAuditEventsByActionAsync(parameters, cancellationToken).ConfigureAwait(false),
            NotificationsUnreadCode => await BuildNotificationsUnreadAsync(parameters, cancellationToken).ConfigureAwait(false),
            DocumentsUnsignedCode => await BuildDocumentsUnsignedAsync(parameters, cancellationToken).ConfigureAwait(false),
            DossiersOpenByExaminerCode => await BuildDossiersOpenByExaminerAsync(parameters, cancellationToken).ConfigureAwait(false),
            // Annex 6j batch — appended without disturbing the original five branches above.
            _ when IsAnnex6jReportCode(reportCode) =>
                await BuildAnnex6jDatasetAsync(reportCode, parameters, cancellationToken).ConfigureAwait(false),
            _ => Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown report code"),
        };
    }

    // ─────────────────────────── RPT-PASSPORT-USAGE ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-PASSPORT-USAGE</c> — count of <see cref="ServiceApplication"/> rows whose
    /// <see cref="AuditableEntity.CreatedAtUtc"/> falls in the UTC window
    /// <c>[fromUtc, toUtc)</c>, grouped by the owning <see cref="ServicePassport.Code"/>. The
    /// Romanian-language friendly title <see cref="ServicePassport.NameRo"/> is included so
    /// consumers can render the row without an additional lookup. Soft-deleted applications and
    /// soft-deleted passports are both excluded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Service passports with zero traffic in the window are not emitted — the KPI focuses on
    /// services that actually carried load. Rows are ordered by Count desc, then ServiceCode
    /// (Ordinal) for stable tie-breaks. The window driver is the application's
    /// <see cref="AuditableEntity.CreatedAtUtc"/> (draft creation) rather than
    /// <see cref="ServiceApplication.SubmittedAtUtc"/> so the report counts every attempt the
    /// passport saw, not only the submitted ones. A future "submitted-only" variant should be
    /// a separate report code rather than a parameter on this one.
    /// </para>
    /// <para>
    /// The aggregation is server-side via EF group-by: in-memory aggregation would needlessly
    /// pull every application row in the window. Both Postgres and the InMemory provider
    /// translate the group-by over <c>p.Code</c> / <c>p.NameRo</c> consistently because both
    /// fields are string columns on the joined entity.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildPassportUsageAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Join Application ⨝ ServicePassport on the passport FK; project the pair (Code, NameRo)
        // and group in memory. The join is filtered to in-window, active rows on both sides so
        // soft-deleted passports drop from the result.
        var query =
            from a in _db.Applications
            where a.IsActive
                  && a.CreatedAtUtc >= fromInstant
                  && a.CreatedAtUtc < toInstant
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            where p.IsActive
            select new
            {
                ServiceCode = p.Code,
                ServiceTitleRo = p.NameRo,
            };

        var raw = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = raw
            .GroupBy(r => (r.ServiceCode, r.ServiceTitleRo))
            .Select(g => new
            {
                ServiceCode = g.Key.ServiceCode,
                ServiceTitleRo = g.Key.ServiceTitleRo,
                Count = g.LongCount(),
            })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.ServiceCode, StringComparer.Ordinal)
            .ToList();

        var headers = new[] { "Service Code", "Service Title (RO)", "Application Count" };
        var data = grouped.Select(r => new[]
        {
            r.ServiceCode,
            r.ServiceTitleRo,
            r.Count.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-AUDIT-EVENTS-BY-ACTION ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-AUDIT-EVENTS-BY-ACTION</c> — count of <see cref="AuditLog"/> rows whose
    /// <see cref="AuditLog.EventAtUtc"/> falls in the UTC window <c>[fromUtc, toUtc)</c>,
    /// grouped by <see cref="AuditLog.EventCode"/>. Soft-deleted rows are excluded. Output is
    /// ordered by Count desc, then EventCode (Ordinal); an optional <c>topN</c> parameter
    /// truncates the list — useful for surfacing the most-used calls (e.g.
    /// <c>MCONNECT.RSP.QUERY</c>) or the most-rejected paths (e.g.
    /// <c>DOCUMENT.UPLOAD.REJECTED</c>) without overwhelming the consumer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window predicate uses <see cref="AuditLog.EventAtUtc"/> (the moment the audited
    /// event occurred), matching the convention in
    /// <see cref="BuildAuditEventsBySeverityAsync"/>. <c>topN</c> is clamped to
    /// <c>[1, 5000]</c>; the lower bound prevents a degenerate empty-projection request, the
    /// upper bound matches <see cref="DefaultMaxRows"/> so a single report still fits in a
    /// modest response budget.
    /// </para>
    /// <para>
    /// Grouping happens in memory because the EF InMemory provider cannot reliably translate
    /// a server-side <c>GROUP BY</c> + <c>LongCount()</c> projection that selects both the
    /// key and an aggregate at once when the source includes a string column. We accept the
    /// in-memory pass — the in-window audit volume is bounded by upstream rate limits and
    /// stays comfortably below the row-cap in normal operation.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>; <c>topN</c> optional.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAuditEventsByActionAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // topN — optional truncation. Defaults to the same DefaultMaxRows cap used by the
        // generic audit-log dump so the report never out-grows that contract.
        var topN = ReadInt(parameters, "topN") ?? DefaultMaxRows;
        topN = Math.Clamp(topN, 1, DefaultMaxRows);

        // Pull just the EventCode column for every in-window active row; counting happens in
        // memory (see XML doc rationale).
        var codes = await _db.AuditLogs
            .Where(a => a.IsActive
                && a.EventAtUtc >= fromInstant
                && a.EventAtUtc < toInstant)
            .Select(a => a.EventCode)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = codes
            .GroupBy(c => c, StringComparer.Ordinal)
            .Select(g => new { EventCode = g.Key, Count = g.LongCount() })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.EventCode, StringComparer.Ordinal)
            .Take(topN)
            .ToList();

        var headers = new[] { "Event Code", "Count" };
        var data = grouped.Select(r => new[]
        {
            r.EventCode,
            r.Count.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-NOTIFICATIONS-UNREAD ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-NOTIFICATIONS-UNREAD</c> — per-channel count of <see cref="Notification"/>
    /// rows still unread as of the supplied <c>asOfUtc</c> moment. "Unread" is defined as
    /// <see cref="Notification.ReadAtUtc"/> being null AND
    /// <see cref="Notification.DispatchedAtUtc"/> being non-null AND
    /// <see cref="Notification.DispatchedAtUtc"/> ≤ <c>asOfUtc</c> — i.e. the notification was
    /// actually delivered to the recipient by the asOf moment but they have not opened it yet.
    /// One row per <see cref="NotificationChannel"/> value is emitted densely so consumers can
    /// rely on a stable shape — channels with zero unread still appear with a zero count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Notifications that were created but never dispatched (
    /// <see cref="Notification.DispatchedAtUtc"/> is null) are excluded because they are not
    /// yet visible to the recipient — counting them as "unread" would conflate
    /// delivery-pipeline backlog with reader-side backlog. The dense per-channel contract
    /// matches <c>RPT-NOTIFICATIONS-DELIVERY</c> so consumers can pair the two reports.
    /// </para>
    /// <para>
    /// Channel labels follow the spec's <c>"Email"</c> / <c>"SMS"</c> / <c>"InApp"</c> casing
    /// rather than the enum's stringified form
    /// (<see cref="NotificationChannel.Sms"/> → <c>"Sms"</c>) — a small mapping table is used,
    /// identical to the one in <see cref="BuildNotificationsDeliveryAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>asOfUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildNotificationsUnreadAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var asOfUtc = ReadUtcDate(parameters, "asOfUtc");
        if (asOfUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'asOfUtc' is required (UTC ISO 8601).");
        }
        var asOf = asOfUtc.Value;

        // Pull just (Channel) for every dispatched-but-unread notification as of the moment.
        // The dense channel projection runs in memory.
        var raw = await _db.Notifications
            .Where(n => n.IsActive
                && n.DispatchedAtUtc != null
                && n.DispatchedAtUtc <= asOf
                && n.ReadAtUtc == null)
            .Select(n => n.Channel)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Dense-row contract: every NotificationChannel value gets a row in the spec's order
        // (Email, SMS, InApp). The label table also handles the enum-Sms → "SMS" recasing.
        var channelOrder = new (NotificationChannel Channel, string Label)[]
        {
            (NotificationChannel.Email, "Email"),
            (NotificationChannel.Sms,   "SMS"),
            (NotificationChannel.InApp, "InApp"),
        };

        var data = channelOrder.Select(co =>
        {
            var count = raw.LongCount(c => c == co.Channel);
            return new[]
            {
                co.Label,
                count.ToString(CultureInfo.InvariantCulture),
            };
        }).ToList();

        return Result<Dataset>.Success(new Dataset(NotificationsUnreadHeaders, data));
    }

    // ─────────────────────────── RPT-DOCUMENTS-UNSIGNED ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOCUMENTS-UNSIGNED</c> — count of <see cref="Document"/> rows whose
    /// <see cref="AuditableEntity.CreatedAtUtc"/> falls in the UTC window
    /// <c>[fromUtc, toUtc)</c> and whose <see cref="Document.IsSigned"/> is <c>false</c>,
    /// grouped by <see cref="Document.Kind"/>. Soft-deleted documents are excluded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The report surfaces document kinds that frequently end up unsigned — useful for
    /// spotting workflow steps that should require MSign but currently do not. Kinds with
    /// zero unsigned documents in the window are not emitted; the report only shows
    /// document kinds that actually have an unsigned backlog. Rows are ordered by Count desc,
    /// then DocumentKind (Ordinal) for stable tie-breaks.
    /// </para>
    /// <para>
    /// The kind enum is projected to its stable string form so the column value matches the
    /// other Annex 6 reports that surface <see cref="Document.Kind"/> (e.g.
    /// <c>RPT-DOCUMENT-TYPES-USAGE</c> in Annex 6g). A future nullable type-discriminator
    /// could route null / whitespace into an UNKNOWN bucket but
    /// <see cref="Document.Kind"/> is non-nullable today so the ternary is effectively a no-op.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDocumentsUnsignedAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Pull only the kind enum for unsigned in-window active documents; in-memory grouping
        // keeps the predicate identical across EF providers.
        var raw = await _db.Documents
            .Where(d => d.IsActive
                && !d.IsSigned
                && d.CreatedAtUtc >= fromInstant
                && d.CreatedAtUtc < toInstant)
            .Select(d => d.Kind)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = raw
            // Project the enum to its stable string form (matches RPT-DOCUMENT-TYPES-USAGE).
            .Select(k => k.ToString() is { Length: > 0 } s ? s : "UNKNOWN")
            .GroupBy(t => t, StringComparer.Ordinal)
            .Select(g => new { DocumentKind = g.Key, Count = g.LongCount() })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.DocumentKind, StringComparer.Ordinal)
            .ToList();

        var headers = new[] { "Document Kind", "Unsigned Count" };
        var data = grouped.Select(r => new[]
        {
            r.DocumentKind,
            r.Count.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DOSSIERS-OPEN-BY-EXAMINER ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOSSIERS-OPEN-BY-EXAMINER</c> — count of open
    /// <see cref="Dossier"/> rows (<see cref="Dossier.ClosedAtUtc"/> is null and the row is
    /// active) attributed to each examiner as of the supplied <c>asOfUtc</c> moment. The
    /// examiner identity is <see cref="UserProfile.LocalLogin"/> (or
    /// <see cref="UserProfile.DisplayName"/> fallback) for resolved
    /// <see cref="Dossier.AssignedExaminerId"/> values. Dossiers without an assignee land in a
    /// single <c>"&lt;unassigned&gt;"</c> bucket so the row always sums to the total open
    /// backlog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "As of" is honoured by clamping the dossier's <see cref="AuditableEntity.CreatedAtUtc"/>
    /// to <c>≤ asOfUtc</c> AND the close-or-still-open predicate to
    /// (<c>ClosedAtUtc IS NULL OR ClosedAtUtc &gt; asOfUtc</c>). Dossiers closed before the
    /// moment are excluded even if their <see cref="AuditableEntity.IsActive"/> is still true.
    /// </para>
    /// <para>
    /// The query left-joins <see cref="UserProfile"/> against
    /// <see cref="Dossier.AssignedExaminerId"/>. An unassigned dossier (null FK) cannot be
    /// joined, so the projection branch keeps it as a sentinel
    /// <c>"&lt;unassigned&gt;"</c> bucket — a single row regardless of how many dossiers are
    /// in that state. The bucket is only emitted when at least one unassigned dossier exists
    /// in the result set; that matches the pattern used by <c>RPT-EXAMINER-AVG-CASELOAD</c>
    /// which simply excludes the bucket entirely.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>asOfUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDossiersOpenByExaminerAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var asOfUtc = ReadUtcDate(parameters, "asOfUtc");
        if (asOfUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'asOfUtc' is required (UTC ISO 8601).");
        }
        var asOf = asOfUtc.Value;

        // Pull (AssignedExaminerId) for every open-as-of-asOf dossier. The username resolution
        // happens in a second pass so the predicate is identical across EF providers.
        var assignments = await _db.Dossiers
            .Where(d => d.IsActive
                && d.CreatedAtUtc <= asOf
                && (d.ClosedAtUtc == null || d.ClosedAtUtc > asOf))
            .Select(d => d.AssignedExaminerId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Resolve the distinct assigned ids to (Id, Username) pairs. The InMemory provider
        // translates Contains() on a long[] consistently with Postgres.
        var assignedIds = assignments
            .Where(id => id != null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var users = assignedIds.Count == 0
            ? new List<(long Id, string Username)>()
            : (await _db.UserProfiles
                .Where(u => assignedIds.Contains(u.Id))
                .Select(u => new { u.Id, u.LocalLogin, u.DisplayName })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(u => (u.Id, Username: u.LocalLogin ?? u.DisplayName))
                .ToList();

        var idToUser = users.ToDictionary(u => u.Id, u => u.Username, EqualityComparer<long>.Default);

        // Bucket the assignments — unassigned dossiers land in the "<unassigned>" sentinel.
        // Sentinel string is intentionally <-bracketed so it sorts above real LocalLogin
        // values (which are alphanumeric) in Ordinal order — see the OrderBy below.
        const string UnassignedBucket = "<unassigned>";
        var grouped = assignments
            .Select(id => id is null
                ? UnassignedBucket
                : idToUser.TryGetValue(id.Value, out var name) ? name : $"user#{id.Value}")
            .GroupBy(n => n, StringComparer.Ordinal)
            .Select(g => new { Username = g.Key, Count = g.LongCount() })
            .OrderBy(r => r.Username, StringComparer.Ordinal)
            .ToList();

        var headers = new[] { "Examiner Username", "Open Dossier Count" };
        var data = grouped.Select(r => new[]
        {
            r.Username,
            r.Count.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }
}
