using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Annex 6d named-report extensions for <see cref="ReportingService"/>. The fifth batch of
/// Annex 6 reports lives in its own partial file so that the earlier batches (the original
/// five in <c>ReportingService.Annex6.cs</c>, the second batch in <c>ReportingService.Annex6b.cs</c>,
/// the third batch in <c>ReportingService.Annex6c.cs</c>, and this file) remain decoupled.
/// The new codes are recognised by <see cref="IsAnnex6dReportCode"/> and dispatched by
/// <see cref="BuildAnnex6dDatasetAsync"/>; the Annex 6c dispatcher chains into this file via
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
/// All five reports in this batch are aggregations — they emit summary rows rather than
/// per-entity rows. As a consequence, no Sqid encoding appears in the output (no row carries
/// an external identifier). The dense-histogram convention is preserved: zero-count buckets
/// are still emitted so downstream consumers can rely on a stable shape.
/// </para>
/// </remarks>
public sealed partial class ReportingService
{
    // ─────────────────────────── Codes ───────────────────────────

    /// <summary>Annex 6d — average / median dossier processing time per service.</summary>
    private const string DossierLifecycleTimeCode = "RPT-DOSSIER-LIFECYCLE-TIME";

    /// <summary>Annex 6d — per-examiner outcome distribution inside a UTC window.</summary>
    private const string ExaminerOutcomesCode = "RPT-EXAMINER-OUTCOMES";

    /// <summary>Annex 6d — new applications per calendar day in a UTC window (dense histogram).</summary>
    private const string NewApplicationsDailyCode = "RPT-NEW-APPLICATIONS-DAILY";

    /// <summary>Annex 6d — cumulative outstanding amounts (MDL) per service as of a UTC moment.</summary>
    private const string OutstandingAmountsCode = "RPT-OUTSTANDING-AMOUNTS";

    /// <summary>Annex 6d — distribution of dossier decision turnaround times across fixed buckets.</summary>
    private const string DecisionTurnaroundCode = "RPT-DECISION-TURNAROUND";

    // ─────────────────────────── Dispatcher hooks ───────────────────────────

    /// <summary>True when the supplied code is one of the Annex 6d report identifiers.</summary>
    /// <param name="code">Caller-supplied report code (case-sensitive — codes are stable contract).</param>
    /// <remarks>
    /// The check chains in <see cref="IsAnnex6eReportCode"/> so the upstream dispatcher
    /// recognises the Annex 6e batch without further edits to <see cref="IsAnnex6cReportCode"/>.
    /// New Annex 6 batches should follow the same pattern.
    /// </remarks>
    private static bool IsAnnex6dReportCode(string code)
        => code is DossierLifecycleTimeCode or ExaminerOutcomesCode or NewApplicationsDailyCode
            or OutstandingAmountsCode or DecisionTurnaroundCode
            || IsAnnex6eReportCode(code);

    /// <summary>
    /// Routes an Annex 6d report code to its materialiser. Returns
    /// <see cref="ErrorCodes.NotFound"/> for unknown codes so the failure shape matches the
    /// earlier Annex 6 dispatchers.
    /// </summary>
    /// <param name="reportCode">Stable report code, e.g. <c>RPT-DOSSIER-LIFECYCLE-TIME</c>.</param>
    /// <param name="parameters">Parsed JSON parameter document.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAnnex6dDatasetAsync(
        string reportCode,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        return reportCode switch
        {
            DossierLifecycleTimeCode => await BuildDossierLifecycleTimeAsync(parameters, cancellationToken).ConfigureAwait(false),
            ExaminerOutcomesCode => await BuildExaminerOutcomesAsync(parameters, cancellationToken).ConfigureAwait(false),
            NewApplicationsDailyCode => await BuildNewApplicationsDailyAsync(parameters, cancellationToken).ConfigureAwait(false),
            OutstandingAmountsCode => await BuildOutstandingAmountsAsync(parameters, cancellationToken).ConfigureAwait(false),
            DecisionTurnaroundCode => await BuildDecisionTurnaroundAsync(parameters, cancellationToken).ConfigureAwait(false),
            // Annex 6e batch — appended without disturbing the original five branches above.
            _ when IsAnnex6eReportCode(reportCode) =>
                await BuildAnnex6eDatasetAsync(reportCode, parameters, cancellationToken).ConfigureAwait(false),
            _ => Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown report code"),
        };
    }

    // ─────────────────────────── RPT-DOSSIER-LIFECYCLE-TIME ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOSSIER-LIFECYCLE-TIME</c> — average and median dossier processing time
    /// (in days) per <see cref="ServicePassport.Code"/>. Aggregation source: dossiers whose
    /// <see cref="Dossier.ClosedAtUtc"/> falls in the half-open UTC window
    /// <c>[fromUtc, toUtc)</c>. Lifecycle = <c>ClosedAtUtc − CreatedAtUtc</c> in days.
    /// </summary>
    /// <remarks>
    /// Avg is the arithmetic mean. Median is the middle value when the count is odd, or the
    /// arithmetic mean of the two middle values when even. Both are rounded to two decimal
    /// places using <see cref="CultureInfo.InvariantCulture"/>'s <c>F2</c> format so locale
    /// never injects thousand separators or decimal-comma alternatives.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDossierLifecycleTimeAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Pull per-dossier lifecycle days + service code; reduce in-memory because the median
        // calculation requires sorting and indexing — operations that the InMemory provider
        // does not translate uniformly to LINQ-to-SQL.
        var query =
            from d in _db.Dossiers
            where d.IsActive
                  && d.ClosedAtUtc != null
                  && d.ClosedAtUtc >= fromInstant
                  && d.ClosedAtUtc < toInstant
            join a in _db.Applications on d.ApplicationId equals a.Id
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            select new
            {
                ServiceCode = p.Code,
                CreatedAtUtc = d.CreatedAtUtc,
                ClosedAtUtc = d.ClosedAtUtc!.Value,
            };

        var raw = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = raw
            .GroupBy(r => r.ServiceCode, StringComparer.Ordinal)
            .Select(g =>
            {
                var lifetimes = g
                    .Select(x => (x.ClosedAtUtc - x.CreatedAtUtc).TotalDays)
                    .OrderBy(x => x)
                    .ToList();
                var count = lifetimes.Count;
                var avg = lifetimes.Average();
                // Median: for odd count → middle; for even → mean of the two middles.
                double median;
                if (count % 2 == 1)
                {
                    median = lifetimes[count / 2];
                }
                else
                {
                    var lower = lifetimes[(count / 2) - 1];
                    var upper = lifetimes[count / 2];
                    median = (lower + upper) / 2.0;
                }
                return new
                {
                    ServiceCode = g.Key,
                    Avg = avg,
                    Median = median,
                    Count = count,
                };
            })
            .OrderBy(r => r.ServiceCode, StringComparer.Ordinal)
            .ToList();

        var headers = new[] { "Service Code", "Avg Lifecycle Days", "Median Lifecycle Days", "Closed Count" };
        var data = grouped.Select(r => new[]
        {
            r.ServiceCode,
            r.Avg.ToString("F2", CultureInfo.InvariantCulture),
            r.Median.ToString("F2", CultureInfo.InvariantCulture),
            r.Count.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-EXAMINER-OUTCOMES ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-EXAMINER-OUTCOMES</c> — per-examiner distribution of dossier outcomes.
    /// Source: <see cref="WorkflowTask"/> rows completed in the UTC window
    /// <c>[fromUtc, toUtc)</c>, grouped by the assignee's <see cref="UserProfile.LocalLogin"/>
    /// (or <see cref="UserProfile.DisplayName"/> fallback). The outcome attached to each task
    /// is the owning application's <see cref="ServiceApplication.Status"/> at task closure.
    /// </summary>
    /// <remarks>
    /// The three buckets mirror <see cref="ApplicationStatus.Approved"/>,
    /// <see cref="ApplicationStatus.Rejected"/>, and everything else (Cancelled — covers
    /// <see cref="ApplicationStatus.Withdrawn"/>, plain <see cref="ApplicationStatus.Closed"/>,
    /// and any non-final state). Tasks without a <see cref="WorkflowTask.CompletedAtUtc"/> are
    /// excluded (no outcome moment yet).
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildExaminerOutcomesAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Join WorkflowTasks (completed in window) → Dossier → Application → UserProfile.
        // The outcome is taken from the *owning application's* status — that's the dossier's
        // final state at task closure.
        var query =
            from t in _db.WorkflowTasks
            where t.IsActive
                  && t.AssignedUserId != null
                  && t.CompletedAtUtc != null
                  && t.CompletedAtUtc >= fromInstant
                  && t.CompletedAtUtc < toInstant
            join d in _db.Dossiers on t.DossierId equals d.Id
            join a in _db.Applications on d.ApplicationId equals a.Id
            join u in _db.UserProfiles on t.AssignedUserId!.Value equals u.Id
            select new
            {
                Username = u.LocalLogin ?? u.DisplayName,
                Status = a.Status,
            };

        var raw = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = raw
            .GroupBy(r => r.Username, StringComparer.Ordinal)
            .Select(g => new
            {
                Username = g.Key,
                Approved = g.Count(x => x.Status == ApplicationStatus.Approved),
                Rejected = g.Count(x => x.Status == ApplicationStatus.Rejected),
                Cancelled = g.Count(x =>
                    x.Status != ApplicationStatus.Approved && x.Status != ApplicationStatus.Rejected),
            })
            .OrderBy(r => r.Username, StringComparer.Ordinal)
            .ToList();

        var headers = new[] { "Examiner Username", "Approved", "Rejected", "Cancelled" };
        var data = grouped.Select(r => new[]
        {
            r.Username,
            r.Approved.ToString(CultureInfo.InvariantCulture),
            r.Rejected.ToString(CultureInfo.InvariantCulture),
            r.Cancelled.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-NEW-APPLICATIONS-DAILY ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-NEW-APPLICATIONS-DAILY</c> — count of newly-created applications per
    /// calendar day in the UTC window <c>[fromUtc, toUtc)</c>. The histogram is dense: every
    /// day from <c>fromUtc.Date</c> (inclusive) to <c>toUtc.Date</c> (exclusive) is emitted,
    /// even when the count is zero. The day column is formatted <c>yyyy-MM-dd</c> on the
    /// invariant culture.
    /// </summary>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildNewApplicationsDailyAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Pull only in-window applications; date-bucketing happens client-side so the InMemory
        // provider doesn't need to translate DateTime.Date.
        var raw = await _db.Applications
            .Where(a => a.IsActive
                && a.CreatedAtUtc >= fromInstant
                && a.CreatedAtUtc < toInstant)
            .Select(a => new { a.CreatedAtUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Date", "Count" };
        var data = new List<string[]>();

        // Iterate day-by-day so zero-count days still appear.
        var firstDay = new DateTime(
            fromInstant.Year, fromInstant.Month, fromInstant.Day, 0, 0, 0, DateTimeKind.Utc);
        var lastDayExclusive = new DateTime(
            toInstant.Year, toInstant.Month, toInstant.Day, 0, 0, 0, DateTimeKind.Utc);

        for (var day = firstDay; day < lastDayExclusive; day = day.AddDays(1))
        {
            var dayLocal = day;
            var count = raw.Count(r => r.CreatedAtUtc.Date == dayLocal);
            data.Add(new[]
            {
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                count.ToString(CultureInfo.InvariantCulture),
            });
        }

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-OUTSTANDING-AMOUNTS ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-OUTSTANDING-AMOUNTS</c> — cumulative outstanding amounts (MDL) per
    /// <see cref="ServicePassport.Code"/>, summed across active approved dossiers as of the
    /// supplied UTC moment. Filter mirrors <c>RPT-PEN-ACTIVE</c>:
    /// <c>AcceptedAtUtc ≤ asOfUtc</c> AND (<c>ClosedAtUtc IS NULL</c> OR <c>ClosedAtUtc &gt; asOfUtc</c>).
    /// </summary>
    /// <remarks>
    /// "Outstanding" here aggregates <see cref="Dossier.ComputedAmountMdl"/> — the amount the
    /// decision engine awarded for the dossier. In the absence of a Payment / Disbursement
    /// entity (CLAUDE.md cross-cutting — immutable snapshots), this is the best available
    /// estimate of money owed but not yet dispatched. When such an entity arrives this
    /// builder should subtract actual dispatches from the awarded amount.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>asOfUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildOutstandingAmountsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var asOfUtc = ReadUtcDate(parameters, "asOfUtc");
        if (asOfUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'asOfUtc' is required (UTC ISO 8601).");
        }
        var asOf = asOfUtc.Value;

        var query =
            from d in _db.Dossiers
            where d.IsActive
                  && d.AcceptedAtUtc != null
                  && d.AcceptedAtUtc <= asOf
                  && (d.ClosedAtUtc == null || d.ClosedAtUtc > asOf)
            join a in _db.Applications on d.ApplicationId equals a.Id
            where a.IsActive && a.Status == ApplicationStatus.Approved
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            group new { d.ComputedAmountMdl } by p.Code into g
            select new
            {
                ServiceCode = g.Key,
                BeneficiaryCount = g.LongCount(),
                TotalOutstanding = g.Sum(x => x.ComputedAmountMdl ?? 0m),
            };

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Service Code", "Beneficiary Count", "Total Outstanding (MDL)" };
        var data = rows
            .OrderBy(r => r.ServiceCode, StringComparer.Ordinal)
            .Select(r => new[]
            {
                r.ServiceCode,
                r.BeneficiaryCount.ToString(CultureInfo.InvariantCulture),
                r.TotalOutstanding.ToString("F2", CultureInfo.InvariantCulture),
            })
            .ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DECISION-TURNAROUND ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DECISION-TURNAROUND</c> — distribution of dossier decision turnaround
    /// times (<see cref="Dossier.AcceptedAtUtc"/> → <see cref="Dossier.ClosedAtUtc"/>) into
    /// five fixed buckets: <c>&lt;3d</c>, <c>3-7d</c>, <c>7-14d</c>, <c>14-30d</c>, <c>&gt;30d</c>.
    /// Source: dossiers whose <see cref="Dossier.ClosedAtUtc"/> falls in
    /// <c>[fromUtc, toUtc)</c> and whose <see cref="Dossier.AcceptedAtUtc"/> is non-null.
    /// </summary>
    /// <remarks>
    /// All five buckets are always emitted, even when their count is zero — downstream
    /// consumers expect a stable histogram shape. Bucket boundaries are half-open at the
    /// upper end: a 3.0-day turnaround lands in <c>3-7d</c>, not <c>&lt;3d</c>; a 7.0-day
    /// turnaround lands in <c>7-14d</c>, not <c>3-7d</c>; and so on.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDecisionTurnaroundAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Pull the (accepted, closed) pairs of every in-window decision. Bucketing happens in
        // memory so we get deterministic boundaries regardless of EF provider.
        var raw = await _db.Dossiers
            .Where(d => d.IsActive
                && d.AcceptedAtUtc != null
                && d.ClosedAtUtc != null
                && d.ClosedAtUtc >= fromInstant
                && d.ClosedAtUtc < toInstant)
            .Select(d => new
            {
                AcceptedAtUtc = d.AcceptedAtUtc!.Value,
                ClosedAtUtc = d.ClosedAtUtc!.Value,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Initialise all five buckets at zero so the dense-histogram contract holds even with
        // no in-window data.
        var lt3 = 0;
        var b3_7 = 0;
        var b7_14 = 0;
        var b14_30 = 0;
        var gt30 = 0;

        foreach (var r in raw)
        {
            var turnaround = (r.ClosedAtUtc - r.AcceptedAtUtc).TotalDays;
            if (turnaround < 3.0) lt3++;
            else if (turnaround < 7.0) b3_7++;
            else if (turnaround < 14.0) b7_14++;
            else if (turnaround < 30.0) b14_30++;
            else gt30++;
        }

        var headers = new[] { "Turnaround Bucket", "Count" };
        var data = new List<string[]>
        {
            new[] { "<3d",    lt3.ToString(CultureInfo.InvariantCulture) },
            new[] { "3-7d",   b3_7.ToString(CultureInfo.InvariantCulture) },
            new[] { "7-14d",  b7_14.ToString(CultureInfo.InvariantCulture) },
            new[] { "14-30d", b14_30.ToString(CultureInfo.InvariantCulture) },
            new[] { ">30d",   gt30.ToString(CultureInfo.InvariantCulture) },
        };

        return Result<Dataset>.Success(new Dataset(headers, data));
    }
}
