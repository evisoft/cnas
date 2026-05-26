using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Annex 6e named-report extensions for <see cref="ReportingService"/>. The sixth batch of
/// Annex 6 reports lives in its own partial file so that the earlier batches (the original
/// five in <c>ReportingService.Annex6.cs</c>, the second batch in <c>ReportingService.Annex6b.cs</c>,
/// the third batch in <c>ReportingService.Annex6c.cs</c>, the fourth batch in
/// <c>ReportingService.Annex6d.cs</c>, and this file) remain decoupled. The new codes are
/// recognised by <see cref="IsAnnex6eReportCode"/> and dispatched by
/// <see cref="BuildAnnex6eDatasetAsync"/>; the Annex 6d dispatcher chains into this file via
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
/// Of the five reports in this batch, only <c>RPT-SLAS-MISSED</c> emits per-dossier rows (and
/// therefore Sqid-encodes the dossier id). The other four are aggregations — they emit summary
/// or histogram rows rather than per-entity rows, so no Sqid encoding appears in their output.
/// The dense-histogram convention is preserved on <c>RPT-DOCUMENT-AGE-DISTRIBUTION</c> and
/// <c>RPT-TOTAL-PAYMENTS-PER-MONTH</c>: zero-count buckets / months are still emitted so
/// downstream consumers can rely on a stable shape.
/// </para>
/// </remarks>
public sealed partial class ReportingService
{
    // ─────────────────────────── Codes ───────────────────────────

    /// <summary>Annex 6e — distribution of document ages inside active dossiers (5-bucket histogram).</summary>
    private const string DocumentAgeDistributionCode = "RPT-DOCUMENT-AGE-DISTRIBUTION";

    /// <summary>Annex 6e — most-frequent rejection reasons inside a UTC window (sorted desc by count).</summary>
    private const string RejectionReasonsCode = "RPT-REJECTION-REASONS";

    /// <summary>Annex 6e — per-examiner monthly decision counts and approved-amount totals.</summary>
    private const string MonthlyDecisionsByExaminerCode = "RPT-MONTHLY-DECISIONS-BY-EXAMINER";

    /// <summary>Annex 6e — open dossiers whose age strictly exceeds an SLA in days.</summary>
    private const string SlasMissedCode = "RPT-SLAS-MISSED";

    /// <summary>Annex 6e — month-by-month total payments inside a UTC window (dense histogram).</summary>
    private const string TotalPaymentsPerMonthCode = "RPT-TOTAL-PAYMENTS-PER-MONTH";

    // ─────────────────────────── Dispatcher hooks ───────────────────────────

    /// <summary>True when the supplied code is one of the Annex 6e report identifiers.</summary>
    /// <param name="code">Caller-supplied report code (case-sensitive — codes are stable contract).</param>
    private static bool IsAnnex6eReportCode(string code)
        => code is DocumentAgeDistributionCode or RejectionReasonsCode
            or MonthlyDecisionsByExaminerCode or SlasMissedCode
            or TotalPaymentsPerMonthCode
            || IsAnnex6fReportCode(code);

    /// <summary>
    /// Routes an Annex 6e report code to its materialiser. Returns
    /// <see cref="ErrorCodes.NotFound"/> for unknown codes so the failure shape matches the
    /// earlier Annex 6 dispatchers.
    /// </summary>
    /// <param name="reportCode">Stable report code, e.g. <c>RPT-DOCUMENT-AGE-DISTRIBUTION</c>.</param>
    /// <param name="parameters">Parsed JSON parameter document.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAnnex6eDatasetAsync(
        string reportCode,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        return reportCode switch
        {
            DocumentAgeDistributionCode => await BuildDocumentAgeDistributionAsync(parameters, cancellationToken).ConfigureAwait(false),
            RejectionReasonsCode => await BuildRejectionReasonsAsync(parameters, cancellationToken).ConfigureAwait(false),
            MonthlyDecisionsByExaminerCode => await BuildMonthlyDecisionsByExaminerAsync(parameters, cancellationToken).ConfigureAwait(false),
            SlasMissedCode => await BuildSlasMissedAsync(parameters, cancellationToken).ConfigureAwait(false),
            TotalPaymentsPerMonthCode => await BuildTotalPaymentsPerMonthAsync(parameters, cancellationToken).ConfigureAwait(false),
            // Annex 6f batch — appended without disturbing the original five branches above.
            _ when IsAnnex6fReportCode(reportCode) =>
                await BuildAnnex6fDatasetAsync(reportCode, parameters, cancellationToken).ConfigureAwait(false),
            _ => Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown report code"),
        };
    }

    // ─────────────────────────── RPT-DOCUMENT-AGE-DISTRIBUTION ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOCUMENT-AGE-DISTRIBUTION</c> — distribution of document ages (in days)
    /// across the five fixed buckets <c>&lt;7d</c>, <c>7-30d</c>, <c>30-90d</c>, <c>90-180d</c>,
    /// <c>&gt;180d</c>. Source: <see cref="Document"/> rows attached
    /// (<see cref="Document.DossierId"/> non-null) to dossiers whose
    /// <see cref="Dossier.ClosedAtUtc"/> is null (still active). Age is computed as
    /// <c>(asOfUtc − Document.CreatedAtUtc).TotalDays</c>.
    /// </summary>
    /// <remarks>
    /// All five buckets are always emitted — downstream consumers expect a stable histogram
    /// shape. Bucket boundaries are half-open at the upper end: a 7.0-day document lands in
    /// <c>7-30d</c>, not <c>&lt;7d</c>; a 30.0-day document lands in <c>30-90d</c>; and so on.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>asOfUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDocumentAgeDistributionAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var asOfUtc = ReadUtcDate(parameters, "asOfUtc");
        if (asOfUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'asOfUtc' is required (UTC ISO 8601).");
        }
        var asOf = asOfUtc.Value;

        // Pull every document attached to an active (non-closed) dossier; bucketing happens in
        // memory so we get deterministic boundaries regardless of EF provider.
        var query =
            from doc in _db.Documents
            where doc.IsActive && doc.DossierId != null
            join d in _db.Dossiers on doc.DossierId!.Value equals d.Id
            where d.IsActive && d.ClosedAtUtc == null
            select doc.CreatedAtUtc;

        var createdTimes = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        // Initialise all five buckets at zero so the dense-histogram contract holds even with
        // no in-window data.
        var lt7 = 0;
        var b7_30 = 0;
        var b30_90 = 0;
        var b90_180 = 0;
        var gt180 = 0;

        foreach (var createdUtc in createdTimes)
        {
            var ageDays = (asOf - createdUtc).TotalDays;
            if (ageDays < 7.0) lt7++;
            else if (ageDays < 30.0) b7_30++;
            else if (ageDays < 90.0) b30_90++;
            else if (ageDays < 180.0) b90_180++;
            else gt180++;
        }

        var headers = new[] { "Age Bucket", "Count" };
        var data = new List<string[]>
        {
            new[] { "<7d",     lt7.ToString(CultureInfo.InvariantCulture) },
            new[] { "7-30d",   b7_30.ToString(CultureInfo.InvariantCulture) },
            new[] { "30-90d",  b30_90.ToString(CultureInfo.InvariantCulture) },
            new[] { "90-180d", b90_180.ToString(CultureInfo.InvariantCulture) },
            new[] { ">180d",   gt180.ToString(CultureInfo.InvariantCulture) },
        };

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-REJECTION-REASONS ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-REJECTION-REASONS</c> — the most-frequent rejection reasons inside the
    /// UTC window <c>[fromUtc, toUtc)</c>. Source: applications whose
    /// <see cref="ServiceApplication.Status"/> is <see cref="ApplicationStatus.Rejected"/>
    /// and whose <see cref="ServiceApplication.ClosedAtUtc"/> falls in the window. Rows are
    /// sorted descending by Count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No <c>RejectionReasonCode</c> field exists on <see cref="ServiceApplication"/></b> in
    /// the current data model — so this builder buckets every rejected application into the
    /// <c>UNKNOWN</c> reason code per the report specification. When the entity gains a
    /// dedicated reason-code field this builder should group by that field instead. The
    /// Romanian friendly label is resolved through a static lookup map below so consumers
    /// already see human-readable text even before the data model is enriched.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildRejectionReasonsAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Count Rejected applications closed in window. Today the entity carries no reason
        // code field, so every row lands in the UNKNOWN bucket; the count materialises with
        // a server-side aggregation to avoid pulling all rows just to count them.
        var rejectedCount = await _db.Applications
            .Where(a => a.IsActive
                && a.Status == ApplicationStatus.Rejected
                && a.ClosedAtUtc != null
                && a.ClosedAtUtc >= fromInstant
                && a.ClosedAtUtc < toInstant)
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Reason Code", "Reason (RO)", "Count" };

        // Aggregated row layout — there is only one bucket today (UNKNOWN). When real reason
        // codes arrive, this list should be assembled by grouping the in-window rejections.
        var rows = new List<(string Code, long Count)>();
        if (rejectedCount > 0)
        {
            rows.Add(("UNKNOWN", rejectedCount));
        }

        var data = rows
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .Select(r => new[]
            {
                r.Code,
                LookupRejectionReasonRo(r.Code),
                r.Count.ToString(CultureInfo.InvariantCulture),
            })
            .ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    /// <summary>
    /// Static lookup table mapping a stable rejection reason code to its Romanian friendly
    /// label. Unknown codes fall through to the code itself, so the report still renders
    /// something useful when a brand-new reason code arrives in production before this map
    /// is updated.
    /// </summary>
    private static string LookupRejectionReasonRo(string code) => code switch
    {
        "DOC_INCOMPLETE" => "Documente incomplete",
        "NOT_ELIGIBLE" => "Beneficiarul nu îndeplinește criteriile",
        "DUPLICATE" => "Cerere duplicată",
        "FRAUD_SUSPECTED" => "Suspiciune de fraudă",
        "UNKNOWN" => "Motiv nespecificat",
        _ => code,
    };

    // ─────────────────────────── RPT-MONTHLY-DECISIONS-BY-EXAMINER ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-MONTHLY-DECISIONS-BY-EXAMINER</c> — per-examiner aggregate of approved
    /// and rejected decisions inside the supplied calendar month, plus the summed amount
    /// across approved dossiers. The <c>monthUtc</c> parameter is anchored to the first-of-month
    /// UTC moment (day component ignored). Source: applications whose
    /// <see cref="ServiceApplication.ClosedAtUtc"/> falls in the month and whose
    /// <see cref="ServiceApplication.Status"/> is one of Approved / Rejected, joined to the
    /// workflow-task assignee via the owning dossier.
    /// </summary>
    /// <remarks>
    /// Each row carries: <c>ExaminerUsername</c> (<see cref="UserProfile.LocalLogin"/> or
    /// <see cref="UserProfile.DisplayName"/> fallback), <c>ApprovedCount</c>,
    /// <c>RejectedCount</c>, <c>TotalAmountApprovedMdl</c>. The amount sums
    /// <see cref="Dossier.ComputedAmountMdl"/> across Approved dossiers only — Rejected
    /// dossiers contribute zero. When a dossier has multiple workflow tasks the most-recently
    /// completed one wins (typical "who closed it" semantic).
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>monthUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildMonthlyDecisionsByExaminerAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var monthUtc = ReadUtcDate(parameters, "monthUtc");
        if (monthUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'monthUtc' is required (UTC ISO 8601).");
        }
        var startOfMonth = new DateTime(monthUtc.Value.Year, monthUtc.Value.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var startOfNextMonth = startOfMonth.AddMonths(1);

        // Join Application (decided in month) → Dossier → WorkflowTask (assignee) → UserProfile.
        // Pull a per-application "best task" by picking the latest CompletedAtUtc; ties are
        // broken arbitrarily by EF — that is acceptable for a reporting aggregate.
        var query =
            from a in _db.Applications
            where a.IsActive
                  && a.ClosedAtUtc != null
                  && a.ClosedAtUtc >= startOfMonth
                  && a.ClosedAtUtc < startOfNextMonth
                  && (a.Status == ApplicationStatus.Approved || a.Status == ApplicationStatus.Rejected)
            join d in _db.Dossiers on a.Id equals d.ApplicationId
            join t in _db.WorkflowTasks on d.Id equals t.DossierId
            where t.IsActive && t.AssignedUserId != null
            join u in _db.UserProfiles on t.AssignedUserId!.Value equals u.Id
            select new
            {
                ApplicationId = a.Id,
                Status = a.Status,
                Amount = d.ComputedAmountMdl,
                Username = u.LocalLogin ?? u.DisplayName,
                TaskCompletedAtUtc = t.CompletedAtUtc,
            };

        var raw = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        // Reduce in-memory: one application per "best" assignee (latest CompletedAtUtc wins),
        // then group by username. This guarantees we never double-count an application even
        // when multiple workflow tasks reference the same dossier.
        var perApplication = raw
            .GroupBy(r => r.ApplicationId)
            .Select(g => g.OrderByDescending(x => x.TaskCompletedAtUtc ?? DateTime.MinValue).First())
            .ToList();

        var grouped = perApplication
            .GroupBy(r => r.Username, StringComparer.Ordinal)
            .Select(g => new
            {
                Username = g.Key,
                Approved = g.Count(x => x.Status == ApplicationStatus.Approved),
                Rejected = g.Count(x => x.Status == ApplicationStatus.Rejected),
                TotalAmount = g
                    .Where(x => x.Status == ApplicationStatus.Approved)
                    .Sum(x => x.Amount ?? 0m),
            })
            .OrderBy(r => r.Username, StringComparer.Ordinal)
            .ToList();

        var headers = new[]
        {
            "Examiner Username", "Approved Count", "Rejected Count", "Total Amount Approved (MDL)",
        };
        var data = grouped.Select(r => new[]
        {
            r.Username,
            r.Approved.ToString(CultureInfo.InvariantCulture),
            r.Rejected.ToString(CultureInfo.InvariantCulture),
            r.TotalAmount.ToString("F2", CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-SLAS-MISSED ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-SLAS-MISSED</c> — open dossiers whose age strictly exceeds the supplied
    /// SLA in days. Filter: <see cref="Dossier.ClosedAtUtc"/> is null and
    /// <c>(now − <see cref="AuditableEntity.CreatedAtUtc"/>).TotalDays &gt; nDays</c>. Each
    /// row carries the dossier Sqid, beneficiary IDNP, ServicePassport code, received-UTC,
    /// and integer DaysOpen.
    /// </summary>
    /// <param name="parameters">JSON object — must contain a positive integer <c>nDays</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildSlasMissedAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var nDays = ReadInt(parameters, "nDays");
        if (nDays is null || nDays.Value <= 0)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'nDays' is required and must be a positive integer.");
        }

        var now = _clock.UtcNow;
        // Translate "age > nDays" into "CreatedAtUtc < cutoff" so it is server-side computable
        // on every EF provider (including the InMemory used by tests).
        var cutoff = now.AddDays(-nDays.Value);

        var query =
            from d in _db.Dossiers
            where d.IsActive
                  && d.ClosedAtUtc == null
                  && d.CreatedAtUtc < cutoff
            join a in _db.Applications on d.ApplicationId equals a.Id
            join s in _db.Solicitants on a.SolicitantId equals s.Id
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            select new
            {
                DossierId = d.Id,
                Idnp = s.NationalId,
                ServiceCode = p.Code,
                ReceivedUtc = d.CreatedAtUtc,
            };

        var rows = await query
            .OrderBy(r => r.ReceivedUtc)
            .Take(AbsoluteRowCeiling)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[]
        {
            "Dossier Sqid", "Beneficiary IDNP", "Service Code", "Received (UTC)", "Days Open",
        };
        var data = rows.Select(r => new[]
        {
            _sqids.Encode(r.DossierId),
            r.Idnp,
            r.ServiceCode,
            r.ReceivedUtc.ToString("o", CultureInfo.InvariantCulture),
            ((int)Math.Floor((now - r.ReceivedUtc).TotalDays))
                .ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-TOTAL-PAYMENTS-PER-MONTH ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-TOTAL-PAYMENTS-PER-MONTH</c> — month-by-month total monthly payments
    /// inside the UTC window <c>[fromUtc, toUtc)</c>. For each calendar month between
    /// <c>fromUtc</c> (first-of-month inclusive) and <c>toUtc</c> (first-of-month exclusive),
    /// counts distinct active approved dossiers as of the month's anchor and sums
    /// <see cref="Dossier.ComputedAmountMdl"/> across them.
    /// </summary>
    /// <remarks>
    /// "Active at month-anchor" mirrors <c>RPT-PEN-ACTIVE</c>: <c>AcceptedAtUtc ≤ monthAnchor</c>
    /// AND (<c>ClosedAtUtc IS NULL</c> OR <c>ClosedAtUtc &gt; monthAnchor</c>). The histogram
    /// is dense — every month in the window is emitted even when no dossiers are active that
    /// month (BeneficiaryCount=0, TotalAmountMdl=0.00). The Month column is the first-of-month
    /// UTC moment formatted with the round-trip <c>"o"</c> format using
    /// <see cref="CultureInfo.InvariantCulture"/>.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildTotalPaymentsPerMonthAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Materialise the candidate dossiers once — every approved dossier whose Accepted
        // moment falls before the window end. The per-month filter happens in-memory using
        // the anchors so EF doesn't need to translate per-month aggregates.
        var query =
            from d in _db.Dossiers
            where d.IsActive
                  && d.AcceptedAtUtc != null
                  && d.AcceptedAtUtc < toInstant
            join a in _db.Applications on d.ApplicationId equals a.Id
            where a.IsActive && a.Status == ApplicationStatus.Approved
            select new
            {
                DossierId = d.Id,
                AcceptedAtUtc = d.AcceptedAtUtc!.Value,
                ClosedAtUtc = d.ClosedAtUtc,
                Amount = d.ComputedAmountMdl,
            };

        var candidates = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var firstMonth = new DateTime(fromInstant.Year, fromInstant.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonthExclusive = new DateTime(toInstant.Year, toInstant.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var headers = new[] { "Month (UTC)", "Beneficiary Count", "Total Amount (MDL)" };
        var data = new List<string[]>();

        for (var month = firstMonth; month < lastMonthExclusive; month = month.AddMonths(1))
        {
            var anchor = month;
            // Active at the month anchor — same predicate as RPT-PEN-ACTIVE.
            var active = candidates
                .Where(x => x.AcceptedAtUtc <= anchor
                            && (x.ClosedAtUtc == null || x.ClosedAtUtc > anchor))
                .ToList();

            var beneficiaryCount = active
                .Select(x => x.DossierId)
                .Distinct()
                .Count();
            var totalAmount = active.Sum(x => x.Amount ?? 0m);

            data.Add(new[]
            {
                month.ToString("o", CultureInfo.InvariantCulture),
                beneficiaryCount.ToString(CultureInfo.InvariantCulture),
                totalAmount.ToString("F2", CultureInfo.InvariantCulture),
            });
        }

        return Result<Dataset>.Success(new Dataset(headers, data));
    }
}
