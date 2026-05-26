using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Annex 6g named-report extensions for <see cref="ReportingService"/>. The eighth batch of
/// Annex 6 reports lives in its own partial file so that the earlier batches (the original
/// five in <c>ReportingService.Annex6.cs</c>, the second batch in <c>ReportingService.Annex6b.cs</c>,
/// the third batch in <c>ReportingService.Annex6c.cs</c>, the fourth batch in
/// <c>ReportingService.Annex6d.cs</c>, the fifth batch in <c>ReportingService.Annex6e.cs</c>,
/// the sixth batch — wait, by file-naming Annex 6f is the seventh batch — and this file the
/// eighth) remain decoupled. The new codes are recognised by <see cref="IsAnnex6gReportCode"/>
/// and dispatched by <see cref="BuildAnnex6gDatasetAsync"/>; the Annex 6f dispatcher chains
/// into this file via a single <c>_ when</c> arm so new code only requires one minimal
/// upstream edit.
/// </summary>
/// <remarks>
/// <para>
/// Conventions match the earlier batches — external identifiers in every row are Sqid-encoded
/// (CLAUDE.md RULE 3) when they appear, timestamps are UTC formatted with the round-trip
/// <c>"o"</c> format using <see cref="CultureInfo.InvariantCulture"/>, money is rendered using
/// <c>F2</c> on the invariant culture, and date windows are half-open <c>[fromUtc, toUtc)</c>.
/// </para>
/// <para>
/// Of the five reports in this batch only <c>RPT-INSURED-PERSONS-NEW</c> emits per-entity rows
/// and therefore Sqid-encodes the insured person id. The other four are aggregations — they
/// emit summary or histogram rows rather than per-entity rows, so no Sqid encoding appears in
/// their output. The dense-histogram convention is preserved on <c>RPT-WORKFLOW-BACKLOG-AGE</c>
/// (5 fixed buckets) and <c>RPT-NOTIFICATIONS-DELIVERY</c> (one row per channel — Email / SMS /
/// InApp — even when a channel has no traffic in the window).
/// </para>
/// </remarks>
public sealed partial class ReportingService
{
    // ─────────────────────────── Codes ───────────────────────────

    /// <summary>Annex 6g — distribution of document <see cref="Document.Kind"/> values created in window.</summary>
    private const string DocumentTypesUsageCode = "RPT-DOCUMENT-TYPES-USAGE";

    /// <summary>Annex 6g — open workflow-task ages bucketed into a 5-bucket histogram.</summary>
    private const string WorkflowBacklogAgeCode = "RPT-WORKFLOW-BACKLOG-AGE";

    /// <summary>Annex 6g — insured persons newly registered in the supplied UTC window.</summary>
    private const string InsuredPersonsNewCode = "RPT-INSURED-PERSONS-NEW";

    /// <summary>Annex 6g — distinct beneficiary count per <see cref="ServicePassport.Code"/> as of a UTC moment.</summary>
    private const string BeneficiariesByServiceTypeCode = "RPT-BENEFICIARIES-BY-SERVICE-TYPE";

    /// <summary>Annex 6g — per-channel notification delivery counts (Delivered / Failed / Suppressed).</summary>
    private const string NotificationsDeliveryCode = "RPT-NOTIFICATIONS-DELIVERY";

    // ─────────────────────────── Dispatcher hooks ───────────────────────────

    /// <summary>True when the supplied code is one of the Annex 6g report identifiers.</summary>
    /// <param name="code">Caller-supplied report code (case-sensitive — codes are stable contract).</param>
    /// <remarks>
    /// The check chains in <see cref="IsAnnex6hReportCode"/> so the upstream dispatcher
    /// recognises the Annex 6h batch without further edits to <see cref="IsAnnex6fReportCode"/>
    /// or any earlier link in the chain.
    /// </remarks>
    private static bool IsAnnex6gReportCode(string code)
        => code is DocumentTypesUsageCode or WorkflowBacklogAgeCode or InsuredPersonsNewCode
            or BeneficiariesByServiceTypeCode or NotificationsDeliveryCode
            || IsAnnex6hReportCode(code);

    /// <summary>
    /// Routes an Annex 6g report code to its materialiser. Returns
    /// <see cref="ErrorCodes.NotFound"/> for unknown codes so the failure shape matches the
    /// earlier Annex 6 dispatchers.
    /// </summary>
    /// <param name="reportCode">Stable report code, e.g. <c>RPT-DOCUMENT-TYPES-USAGE</c>.</param>
    /// <param name="parameters">Parsed JSON parameter document.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAnnex6gDatasetAsync(
        string reportCode,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        return reportCode switch
        {
            DocumentTypesUsageCode => await BuildDocumentTypesUsageAsync(parameters, cancellationToken).ConfigureAwait(false),
            WorkflowBacklogAgeCode => await BuildWorkflowBacklogAgeAsync(parameters, cancellationToken).ConfigureAwait(false),
            InsuredPersonsNewCode => await BuildInsuredPersonsNewAsync(parameters, cancellationToken).ConfigureAwait(false),
            BeneficiariesByServiceTypeCode => await BuildBeneficiariesByServiceTypeAsync(parameters, cancellationToken).ConfigureAwait(false),
            NotificationsDeliveryCode => await BuildNotificationsDeliveryAsync(parameters, cancellationToken).ConfigureAwait(false),
            // Annex 6h batch — appended without disturbing the original five branches above.
            _ when IsAnnex6hReportCode(reportCode) =>
                await BuildAnnex6hDatasetAsync(reportCode, parameters, cancellationToken).ConfigureAwait(false),
            _ => Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown report code"),
        };
    }

    // ─────────────────────────── RPT-DOCUMENT-TYPES-USAGE ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOCUMENT-TYPES-USAGE</c> — count of <see cref="Document"/> rows created in
    /// the UTC window <c>[fromUtc, toUtc)</c>, grouped by document type. In the present data
    /// model the document's type is represented by the <see cref="Document.Kind"/> enum (a
    /// dedicated free-form <c>DocumentType</c> field does not exist), so the report buckets
    /// rows by the string form of <see cref="Document.Kind"/>. A defensive <c>UNKNOWN</c> bucket
    /// is reserved for null / empty type strings — currently unreachable because
    /// <see cref="Document.Kind"/> is a non-nullable enum, but kept so the contract holds if a
    /// future model adds a nullable type discriminator.
    /// </summary>
    /// <remarks>
    /// Rows are ordered by Count desc, then DocumentType (Ordinal) for stable tie-breaks. The
    /// builder only counts rows whose <see cref="AuditableEntity.IsActive"/> is true so the
    /// soft-delete contract is honoured everywhere in the reporting surface.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDocumentTypesUsageAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Pull the kind enum directly — server-side grouping by enum value would also work but
        // doing it in memory keeps the predicate identical across EF providers and yields the
        // deterministic UNKNOWN-fallback semantic the report specification calls for.
        var raw = await _db.Documents
            .Where(d => d.IsActive
                && d.CreatedAtUtc >= fromInstant
                && d.CreatedAtUtc < toInstant)
            .Select(d => d.Kind)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = raw
            // Project the enum to its stable string form. A future nullable type discriminator
            // could route null / whitespace into the UNKNOWN bucket — Kind is non-nullable today
            // so the ternary is effectively a no-op now but documents the intended contract.
            .Select(k => k.ToString() is { Length: > 0 } s ? s : "UNKNOWN")
            .GroupBy(t => t, StringComparer.Ordinal)
            .Select(g => new { DocumentType = g.Key, Count = g.LongCount() })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.DocumentType, StringComparer.Ordinal)
            .ToList();

        var headers = new[] { "Document Type", "Count" };
        var data = grouped.Select(r => new[]
        {
            r.DocumentType,
            r.Count.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-WORKFLOW-BACKLOG-AGE ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-WORKFLOW-BACKLOG-AGE</c> — open workflow-task ages bucketed into the five
    /// fixed buckets <c>0-1d</c>, <c>1-3d</c>, <c>3-7d</c>, <c>7-14d</c>, <c>&gt;14d</c>.
    /// Source: <see cref="WorkflowTask"/> rows whose <see cref="WorkflowTask.CompletedAtUtc"/>
    /// is null (still open). Age is computed as
    /// <c>(<see cref="ICnasTimeProvider.UtcNow"/> − <see cref="AuditableEntity.CreatedAtUtc"/>).TotalDays</c>.
    /// All five buckets are emitted densely so downstream consumers can rely on a stable shape.
    /// </summary>
    /// <remarks>
    /// Bucket boundaries are half-open at the upper end: a 1.0-day task lands in <c>1-3d</c>,
    /// not <c>0-1d</c>; a 3.0-day task lands in <c>3-7d</c>; a 7.0-day task lands in <c>7-14d</c>;
    /// a 14.0-day task lands in <c>&gt;14d</c>. The report carries no parameters — it always
    /// describes the present backlog as of <see cref="_clock"/>.
    /// </remarks>
    /// <param name="parameters">Unused — the report carries no parameters.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildWorkflowBacklogAgeAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        _ = parameters; // No parameters defined for this report.

        var now = _clock.UtcNow;

        // Pull the creation timestamps of every open task; bucketing happens in-memory so
        // boundaries are deterministic regardless of EF provider.
        var createdTimes = await _db.WorkflowTasks
            .Where(t => t.IsActive && t.CompletedAtUtc == null)
            .Select(t => t.CreatedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Initialise all five buckets at zero so the dense-histogram contract holds even with
        // no open tasks.
        var b0_1 = 0;
        var b1_3 = 0;
        var b3_7 = 0;
        var b7_14 = 0;
        var gt14 = 0;

        foreach (var createdUtc in createdTimes)
        {
            var ageDays = (now - createdUtc).TotalDays;
            if (ageDays < 1.0) b0_1++;
            else if (ageDays < 3.0) b1_3++;
            else if (ageDays < 7.0) b3_7++;
            else if (ageDays < 14.0) b7_14++;
            else gt14++;
        }

        var headers = new[] { "Age Bucket", "Count" };
        var data = new List<string[]>
        {
            new[] { "0-1d",  b0_1.ToString(CultureInfo.InvariantCulture) },
            new[] { "1-3d",  b1_3.ToString(CultureInfo.InvariantCulture) },
            new[] { "3-7d",  b3_7.ToString(CultureInfo.InvariantCulture) },
            new[] { "7-14d", b7_14.ToString(CultureInfo.InvariantCulture) },
            new[] { ">14d",  gt14.ToString(CultureInfo.InvariantCulture) },
        };

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-INSURED-PERSONS-NEW ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-INSURED-PERSONS-NEW</c> — insured persons (<see cref="InsuredPerson"/>)
    /// whose <see cref="AuditableEntity.CreatedAtUtc"/> falls in the UTC window
    /// <c>[fromUtc, toUtc)</c>. Filters out soft-deleted rows. One row per insured person —
    /// the insured-person id is Sqid-encoded (CLAUDE.md RULE 3).
    /// </summary>
    /// <remarks>
    /// <see cref="InsuredPerson.RegisteredAtUtc"/> is the field captured in the report row
    /// (when the person was first registered in CNAS records — the date sourced from RSP),
    /// while <see cref="AuditableEntity.CreatedAtUtc"/> drives the window predicate (when the
    /// row was first written into the local store). The two values diverge when historic
    /// records are back-loaded; tests assert on the local-creation predicate because that is
    /// the dimension the report owners care about.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildInsuredPersonsNewAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        var rows = await _db.InsuredPersons
            .Where(p => p.IsActive
                && p.CreatedAtUtc >= fromInstant
                && p.CreatedAtUtc < toInstant)
            .OrderBy(p => p.RegisteredAtUtc)
            .Take(AbsoluteRowCeiling)
            .Select(p => new
            {
                p.Id, p.Idnp, p.LastName, p.FirstName, p.Patronymic, p.RegisteredAtUtc,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[]
        {
            "Insured Person Sqid", "IDNP", "Full Name", "Registered (UTC)",
        };
        var data = rows.Select(r => new[]
        {
            _sqids.Encode(r.Id),
            r.Idnp,
            string.IsNullOrWhiteSpace(r.Patronymic)
                ? $"{r.LastName} {r.FirstName}"
                : $"{r.LastName} {r.FirstName} {r.Patronymic}",
            r.RegisteredAtUtc.ToString("o", CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-BENEFICIARIES-BY-SERVICE-TYPE ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-BENEFICIARIES-BY-SERVICE-TYPE</c> — distinct beneficiary count per
    /// <see cref="ServicePassport.Code"/> across active dossiers as of the supplied
    /// <c>asOfUtc</c> moment. "Active" mirrors <c>RPT-PEN-ACTIVE</c>:
    /// <c><see cref="AuditableEntity.IsActive"/></c> AND <c><see cref="Dossier.AcceptedAtUtc"/> ≤ asOfUtc</c>
    /// AND (<c><see cref="Dossier.ClosedAtUtc"/> IS NULL</c> OR
    /// <c><see cref="Dossier.ClosedAtUtc"/> &gt; asOfUtc</c>) AND
    /// <c><see cref="ServiceApplication.Status"/> == <see cref="ApplicationStatus.Approved"/></c>.
    /// "Unique beneficiary" = distinct <see cref="ServiceApplication.SolicitantId"/>.
    /// </summary>
    /// <remarks>
    /// Rows are ordered by ServiceCode (Ordinal). Service passports with zero active beneficiaries
    /// as of the moment are not emitted — the report only surfaces services that actually carry
    /// load. <see cref="ServicePassport.NameRo"/> is the Romanian-language friendly title; tests
    /// assert on the column shape and content so a future rename of the field must be reflected
    /// here too.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>asOfUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildBeneficiariesByServiceTypeAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var asOfUtc = ReadUtcDate(parameters, "asOfUtc");
        if (asOfUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'asOfUtc' is required (UTC ISO 8601).");
        }
        var asOf = asOfUtc.Value;

        // Join Dossiers ⨝ Applications (Approved) ⨝ ServicePassports. Pull the projection then
        // count distinct solicitants per service code in memory; doing the distinct-count in
        // EF would require GROUP BY across joined columns which the InMemory provider does not
        // translate consistently.
        var query =
            from d in _db.Dossiers
            where d.IsActive
                  && d.AcceptedAtUtc != null
                  && d.AcceptedAtUtc <= asOf
                  && (d.ClosedAtUtc == null || d.ClosedAtUtc > asOf)
            join a in _db.Applications on d.ApplicationId equals a.Id
            where a.IsActive && a.Status == ApplicationStatus.Approved
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            where p.IsActive
            select new
            {
                ServiceCode = p.Code,
                ServiceTitleRo = p.NameRo,
                SolicitantId = a.SolicitantId,
            };

        var raw = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = raw
            .GroupBy(r => (r.ServiceCode, r.ServiceTitleRo))
            .Select(g => new
            {
                ServiceCode = g.Key.ServiceCode,
                ServiceTitleRo = g.Key.ServiceTitleRo,
                UniqueBeneficiaryCount = g.Select(x => x.SolicitantId).Distinct().LongCount(),
            })
            .OrderBy(r => r.ServiceCode, StringComparer.Ordinal)
            .ToList();

        var headers = new[] { "Service Code", "Service Title (RO)", "Unique Beneficiary Count" };
        var data = grouped.Select(r => new[]
        {
            r.ServiceCode,
            r.ServiceTitleRo,
            r.UniqueBeneficiaryCount.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-NOTIFICATIONS-DELIVERY ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-NOTIFICATIONS-DELIVERY</c> — per-channel notification delivery counts
    /// (DeliveredCount, FailedCount, SuppressedCount) inside the UTC window
    /// <c>[fromUtc, toUtc)</c>. Source: <see cref="Notification"/> rows created in the window,
    /// grouped by <see cref="Notification.DeliveryStatus"/>. One row per
    /// <see cref="NotificationChannel"/> value is emitted densely so consumers can rely on a
    /// stable shape — channels with zero traffic still appear with zeroes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Notification.DeliveryStatus"/> is the authoritative source of the delivery
    /// outcome: <see cref="NotificationDeliveryStatus.Delivered"/>,
    /// <see cref="NotificationDeliveryStatus.Failed"/>, and
    /// <see cref="NotificationDeliveryStatus.Suppressed"/> populate the three count columns
    /// respectively. <see cref="NotificationDeliveryStatus.Pending"/> rows — created but not
    /// yet attempted by the dispatcher — are deliberately excluded from every count column
    /// because they have not yet reached an outcome.
    /// </para>
    /// <para>
    /// Channel labels follow the report specification's <c>"Email"</c> / <c>"SMS"</c> /
    /// <c>"InApp"</c> casing rather than the enum's stringified form
    /// (<see cref="NotificationChannel.Sms"/> → <c>"Sms"</c>) — a small mapping table is used.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildNotificationsDeliveryAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Pull (channel, delivery-status) for every in-window notification. The aggregation
        // runs in memory so the dense-channel contract is straightforward to assemble.
        var raw = await _db.Notifications
            .Where(n => n.IsActive
                && n.CreatedAtUtc >= fromInstant
                && n.CreatedAtUtc < toInstant)
            .Select(n => new { n.Channel, n.DeliveryStatus })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Dense-row contract: every NotificationChannel value gets a row, in the spec's order
        // (Email, SMS, InApp). The label table also handles the enum-Sms → "SMS" recasing.
        var channelOrder = new (NotificationChannel Channel, string Label)[]
        {
            (NotificationChannel.Email, "Email"),
            (NotificationChannel.Sms,   "SMS"),
            (NotificationChannel.InApp, "InApp"),
        };

        var headers = new[]
        {
            "Channel", "Delivered Count", "Failed Count", "Suppressed Count",
        };
        var data = channelOrder.Select(co =>
        {
            var inChannel = raw.Where(r => r.Channel == co.Channel).ToList();
            // Pending rows have no outcome yet and are excluded from every count column.
            var delivered = inChannel.LongCount(r => r.DeliveryStatus == NotificationDeliveryStatus.Delivered);
            var failed = inChannel.LongCount(r => r.DeliveryStatus == NotificationDeliveryStatus.Failed);
            var suppressed = inChannel.LongCount(r => r.DeliveryStatus == NotificationDeliveryStatus.Suppressed);
            return new[]
            {
                co.Label,
                delivered.ToString(CultureInfo.InvariantCulture),
                failed.ToString(CultureInfo.InvariantCulture),
                suppressed.ToString(CultureInfo.InvariantCulture),
            };
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }
}
