using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Annex 6f named-report extensions for <see cref="ReportingService"/>. The seventh batch of
/// Annex 6 reports lives in its own partial file so that the earlier batches (the original
/// five in <c>ReportingService.Annex6.cs</c>, the second batch in <c>ReportingService.Annex6b.cs</c>,
/// the third batch in <c>ReportingService.Annex6c.cs</c>, the fourth batch in
/// <c>ReportingService.Annex6d.cs</c>, the fifth batch in <c>ReportingService.Annex6e.cs</c>,
/// and this file) remain decoupled. The new codes are recognised by
/// <see cref="IsAnnex6fReportCode"/> and dispatched by <see cref="BuildAnnex6fDatasetAsync"/>;
/// the Annex 6e dispatcher chains into this file via a single <c>_ when</c> arm so new code
/// only requires one minimal upstream edit.
/// </summary>
/// <remarks>
/// <para>
/// Conventions match the earlier batches — external identifiers in every row are Sqid-encoded
/// (CLAUDE.md RULE 3) when they appear, timestamps are UTC formatted with the round-trip
/// <c>"o"</c> format using <see cref="CultureInfo.InvariantCulture"/>, money is rendered using
/// <c>F2</c> on the invariant culture, and date windows are half-open <c>[fromUtc, toUtc)</c>.
/// </para>
/// <para>
/// All five reports in this batch are aggregations — they emit summary or histogram rows
/// rather than per-entity rows. As a consequence, no Sqid encoding appears in their output
/// (no row carries an external identifier). The dense-histogram convention is preserved on
/// <c>RPT-CASES-BY-AGE-GROUP</c> (5 fixed age buckets) and <c>RPT-DAILY-CASH-FLOW</c> (every
/// day in window — zero-amount days still emitted).
/// </para>
/// </remarks>
public sealed partial class ReportingService
{
    // ─────────────────────────── Codes ───────────────────────────

    /// <summary>Annex 6f — dossier counts bucketed by beneficiary age groups (5-bucket histogram).</summary>
    private const string CasesByAgeGroupCode = "RPT-CASES-BY-AGE-GROUP";

    /// <summary>Annex 6f — distribution of active dossiers by beneficiary locality (count desc).</summary>
    private const string CasesByLocalityCode = "RPT-CASES-BY-LOCALITY";

    /// <summary>Annex 6f — per-examiner open caseload and average task age in days.</summary>
    private const string ExaminerAvgCaseloadCode = "RPT-EXAMINER-AVG-CASELOAD";

    /// <summary>Annex 6f — cancellation counts grouped by reason code inside a UTC window.</summary>
    private const string CancellationsByReasonCode = "RPT-CANCELLATIONS-BY-REASON";

    /// <summary>Annex 6f — per-calendar-day total disbursed amount and beneficiary count.</summary>
    private const string DailyCashFlowCode = "RPT-DAILY-CASH-FLOW";

    // ─────────────────────────── Dispatcher hooks ───────────────────────────

    /// <summary>True when the supplied code is one of the Annex 6f report identifiers.</summary>
    /// <param name="code">Caller-supplied report code (case-sensitive — codes are stable contract).</param>
    private static bool IsAnnex6fReportCode(string code)
        => code is CasesByAgeGroupCode or CasesByLocalityCode or ExaminerAvgCaseloadCode
            or CancellationsByReasonCode or DailyCashFlowCode
            || IsAnnex6gReportCode(code);

    /// <summary>
    /// Routes an Annex 6f report code to its materialiser. Returns
    /// <see cref="ErrorCodes.NotFound"/> for unknown codes so the failure shape matches the
    /// earlier Annex 6 dispatchers.
    /// </summary>
    /// <param name="reportCode">Stable report code, e.g. <c>RPT-CASES-BY-AGE-GROUP</c>.</param>
    /// <param name="parameters">Parsed JSON parameter document.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAnnex6fDatasetAsync(
        string reportCode,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        return reportCode switch
        {
            CasesByAgeGroupCode => await BuildCasesByAgeGroupAsync(parameters, cancellationToken).ConfigureAwait(false),
            CasesByLocalityCode => await BuildCasesByLocalityAsync(parameters, cancellationToken).ConfigureAwait(false),
            ExaminerAvgCaseloadCode => await BuildExaminerAvgCaseloadAsync(parameters, cancellationToken).ConfigureAwait(false),
            CancellationsByReasonCode => await BuildCancellationsByReasonAsync(parameters, cancellationToken).ConfigureAwait(false),
            DailyCashFlowCode => await BuildDailyCashFlowAsync(parameters, cancellationToken).ConfigureAwait(false),
            // Annex 6g batch — appended without disturbing the original five branches above.
            _ when IsAnnex6gReportCode(reportCode) =>
                await BuildAnnex6gDatasetAsync(reportCode, parameters, cancellationToken).ConfigureAwait(false),
            _ => Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown report code"),
        };
    }

    // ─────────────────────────── RPT-CASES-BY-AGE-GROUP ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-CASES-BY-AGE-GROUP</c> — count of active dossiers (whose
    /// <see cref="Dossier.ClosedAtUtc"/> is null) bucketed by the beneficiary's age at the
    /// supplied <c>asOfUtc</c> moment. The five buckets <c>0-18</c>, <c>19-35</c>, <c>36-55</c>,
    /// <c>56-65</c>, <c>66+</c> are always emitted (dense histogram). Age is computed as
    /// <c>(asOfUtc − InsuredPerson.BirthDate).TotalDays / 365.25</c>; the join key is the
    /// dossier's <see cref="Solicitant.NationalId"/> equal to <see cref="InsuredPerson.Idnp"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Solicitant"/> carries no birth date today — the date of birth is stored on
    /// the linked <see cref="InsuredPerson"/> row sourced from RSP. Dossiers whose
    /// beneficiary cannot be resolved to an <see cref="InsuredPerson"/> are silently dropped
    /// (they would otherwise land in an unknown bucket — a future model enrichment can add
    /// the birth date directly to <see cref="Solicitant"/> at which point this builder can
    /// switch its source). Bucket boundaries are inclusive at the upper end:
    /// 18 → <c>0-18</c>, 19 → <c>19-35</c>, 35 → <c>19-35</c>, 36 → <c>36-55</c>, etc.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>asOfUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildCasesByAgeGroupAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var asOfUtc = ReadUtcDate(parameters, "asOfUtc");
        if (asOfUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'asOfUtc' is required (UTC ISO 8601).");
        }
        var asOf = asOfUtc.Value;

        // Join active dossiers → Application → Solicitant → InsuredPerson (by IDNP). Bucketing
        // happens in memory so we get deterministic boundaries regardless of EF provider.
        //
        // The join MUST use the *Hash shadow columns rather than the encrypted plaintext
        // columns — encrypted columns yield different ciphertext per row, so equality joins
        // on them silently return zero rows in production. The hash columns are deterministic
        // (HMAC-SHA256 with a stable salt), so the same IDNP hashes to the same value on both
        // sides and the join resolves. See Solicitant.NationalIdHash / InsuredPerson.IdnpHash
        // XML doc for the synchronization contract — the application service layer is
        // responsible for keeping the two hash columns in sync with the underlying plaintext.
        var query =
            from d in _db.Dossiers
            where d.IsActive && d.ClosedAtUtc == null
            join a in _db.Applications on d.ApplicationId equals a.Id
            join s in _db.Solicitants on a.SolicitantId equals s.Id
            join ip in _db.InsuredPersons on s.NationalIdHash equals ip.IdnpHash
            where ip.IsActive
            select ip.BirthDate;

        var birthDates = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        // Initialise all five buckets at zero so the dense-histogram contract holds even with
        // no in-window data.
        var b0_18 = 0;
        var b19_35 = 0;
        var b36_55 = 0;
        var b56_65 = 0;
        var gt66 = 0;

        foreach (var birthDate in birthDates)
        {
            // Convert DateOnly → DateTime at midnight UTC for arithmetic.
            var birthInstant = birthDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var ageYears = (asOf - birthInstant).TotalDays / 365.25;
            if (ageYears <= 18.0) b0_18++;
            else if (ageYears <= 35.0) b19_35++;
            else if (ageYears <= 55.0) b36_55++;
            else if (ageYears <= 65.0) b56_65++;
            else gt66++;
        }

        var headers = new[] { "Age Group", "Count" };
        var data = new List<string[]>
        {
            new[] { "0-18",  b0_18.ToString(CultureInfo.InvariantCulture) },
            new[] { "19-35", b19_35.ToString(CultureInfo.InvariantCulture) },
            new[] { "36-55", b36_55.ToString(CultureInfo.InvariantCulture) },
            new[] { "56-65", b56_65.ToString(CultureInfo.InvariantCulture) },
            new[] { "66+",   gt66.ToString(CultureInfo.InvariantCulture) },
        };

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-CASES-BY-LOCALITY ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-CASES-BY-LOCALITY</c> — count of active dossiers grouped by the
    /// beneficiary's locality. The locality is derived from <see cref="Solicitant.PostalAddress"/>:
    /// the first comma-separated token, trimmed. Rows where the postal address is null, empty,
    /// or whitespace fall into the <c>UNKNOWN</c> bucket. Rows are ordered by Count desc, then
    /// locality (Ordinal) for tie-breaking.
    /// </summary>
    /// <remarks>
    /// <see cref="Solicitant"/> has no dedicated locality field today — the report parses the
    /// free-form <c>PostalAddress</c> using the convention that the locality is the first
    /// comma-separated token (e.g. <c>"Chișinău, str. Pacii 12"</c> → <c>Chișinău</c>). When
    /// the data model gains a structured address with a dedicated locality field this builder
    /// should switch to that field instead.
    /// </remarks>
    /// <param name="parameters">Unused — the report carries no parameters.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildCasesByLocalityAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        _ = parameters; // No parameters defined for this report.

        // Pull addresses of active dossiers' beneficiaries; bucket in memory because the
        // first-comma-token extraction is awkward to express server-side and the input volume
        // is small relative to the candidate set.
        var query =
            from d in _db.Dossiers
            where d.IsActive && d.ClosedAtUtc == null
            join a in _db.Applications on d.ApplicationId equals a.Id
            join s in _db.Solicitants on a.SolicitantId equals s.Id
            select s.PostalAddress;

        var addresses = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = addresses
            .Select(ExtractLocality)
            .GroupBy(loc => loc, StringComparer.Ordinal)
            .Select(g => new { Locality = g.Key, Count = g.LongCount() })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Locality, StringComparer.Ordinal)
            .ToList();

        var headers = new[] { "Locality", "Count" };
        var data = grouped.Select(r => new[]
        {
            r.Locality,
            r.Count.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    /// <summary>
    /// Extracts a locality token from a free-form postal address: the first comma-separated
    /// piece, trimmed. Null / empty / whitespace-only input yields <c>UNKNOWN</c>.
    /// </summary>
    private static string ExtractLocality(string? postalAddress)
    {
        if (string.IsNullOrWhiteSpace(postalAddress)) return "UNKNOWN";
        var commaIdx = postalAddress.IndexOf(',', StringComparison.Ordinal);
        var token = commaIdx >= 0 ? postalAddress[..commaIdx] : postalAddress;
        token = token.Trim();
        return token.Length == 0 ? "UNKNOWN" : token;
    }

    // ─────────────────────────── RPT-EXAMINER-AVG-CASELOAD ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-EXAMINER-AVG-CASELOAD</c> — per-examiner open caseload and average task
    /// age in days as of the supplied <c>asOfUtc</c> moment. Source: <see cref="WorkflowTask"/>
    /// rows whose <see cref="WorkflowTask.CompletedAtUtc"/> is null and which carry an
    /// <see cref="WorkflowTask.AssignedUserId"/>, grouped by the assignee's
    /// <see cref="UserProfile.LocalLogin"/> (or <see cref="UserProfile.DisplayName"/> fallback).
    /// Average age uses <c>(asOfUtc − WorkflowTask.CreatedAtUtc).TotalDays</c> averaged across
    /// the examiner's open tasks. Unassigned tasks (group inboxes — null
    /// <see cref="WorkflowTask.AssignedUserId"/>) are excluded by design.
    /// </summary>
    /// <param name="parameters">JSON object — must contain <c>asOfUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildExaminerAvgCaseloadAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var asOfUtc = ReadUtcDate(parameters, "asOfUtc");
        if (asOfUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'asOfUtc' is required (UTC ISO 8601).");
        }
        var asOf = asOfUtc.Value;

        // Join open tasks → UserProfile. The unassigned tasks are filtered out at the source
        // — they have no examiner to attribute the load to.
        var query =
            from t in _db.WorkflowTasks
            where t.IsActive
                  && t.CompletedAtUtc == null
                  && t.AssignedUserId != null
            join u in _db.UserProfiles on t.AssignedUserId!.Value equals u.Id
            select new
            {
                Username = u.LocalLogin ?? u.DisplayName,
                CreatedAtUtc = t.CreatedAtUtc,
            };

        var raw = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = raw
            .GroupBy(r => r.Username, StringComparer.Ordinal)
            .Select(g => new
            {
                Username = g.Key,
                OpenCases = g.LongCount(),
                AvgAgeDays = g.Average(x => (asOf - x.CreatedAtUtc).TotalDays),
            })
            .OrderBy(r => r.Username, StringComparer.Ordinal)
            .ToList();

        var headers = new[] { "Examiner Username", "Open Cases", "Avg Age (Days)" };
        var data = grouped.Select(r => new[]
        {
            r.Username,
            r.OpenCases.ToString(CultureInfo.InvariantCulture),
            r.AvgAgeDays.ToString("F2", CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-CANCELLATIONS-BY-REASON ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-CANCELLATIONS-BY-REASON</c> — count of cancelled applications grouped by
    /// reason code inside the UTC window <c>[fromUtc, toUtc)</c>. Rows are ordered by Count desc,
    /// then ReasonCode (Ordinal).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ApplicationStatus"/> has no dedicated <c>Cancelled</c> value at the present
    /// data-model iteration; the closest semantic is <see cref="ApplicationStatus.Withdrawn"/>
    /// (application withdrawn before final decision). The builder therefore filters on
    /// <c>Status == Withdrawn</c> and counts only applications whose
    /// <see cref="ServiceApplication.ClosedAtUtc"/> falls in the window.
    /// </para>
    /// <para>
    /// No <c>CancellationReasonCode</c> field exists on <see cref="ServiceApplication"/> — so
    /// the builder buckets every cancellation into the <c>UNKNOWN</c> reason code, mirroring
    /// the pattern that <c>RPT-REJECTION-REASONS</c> uses. The Romanian friendly label is
    /// resolved through a static lookup map below. When the entity gains a dedicated reason-code
    /// field this builder should group by that field instead.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildCancellationsByReasonAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Count Withdrawn applications closed in window. Today the entity carries no reason
        // code field, so every row lands in the UNKNOWN bucket; the count materialises with
        // a server-side aggregation to avoid pulling all rows just to count them.
        var cancelledCount = await _db.Applications
            .Where(a => a.IsActive
                && a.Status == ApplicationStatus.Withdrawn
                && a.ClosedAtUtc != null
                && a.ClosedAtUtc >= fromInstant
                && a.ClosedAtUtc < toInstant)
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Reason Code", "Reason (RO)", "Count" };

        // Aggregated row layout — there is only one bucket today (UNKNOWN). When real reason
        // codes arrive, this list should be assembled by grouping the in-window cancellations.
        var rows = new List<(string Code, long Count)>();
        if (cancelledCount > 0)
        {
            rows.Add(("UNKNOWN", cancelledCount));
        }

        var data = rows
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .Select(r => new[]
            {
                r.Code,
                LookupCancellationReasonRo(r.Code),
                r.Count.ToString(CultureInfo.InvariantCulture),
            })
            .ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    /// <summary>
    /// Static lookup table mapping a stable cancellation reason code to its Romanian friendly
    /// label. Unknown codes fall through to the code itself, so the report still renders
    /// something useful when a brand-new reason code arrives in production before this map is
    /// updated.
    /// </summary>
    private static string LookupCancellationReasonRo(string code) => code switch
    {
        "APPLICANT_REQUEST" => "La cererea solicitantului",
        "DOC_INCOMPLETE" => "Documente incomplete",
        "DUPLICATE" => "Cerere duplicată",
        "ELIGIBILITY_LOST" => "Beneficiarul nu mai îndeplinește criteriile",
        "UNKNOWN" => "Motiv nespecificat",
        _ => code,
    };

    // ─────────────────────────── RPT-DAILY-CASH-FLOW ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DAILY-CASH-FLOW</c> — per-calendar-day total disbursed amount (MDL) and
    /// distinct beneficiary count inside the UTC window <c>[fromUtc, toUtc)</c>. For each day
    /// in the half-open window (anchored at midnight UTC), the builder sums
    /// <see cref="Dossier.ComputedAmountMdl"/> across approved dossiers active that day, using
    /// the same predicate as <c>RPT-PEN-ACTIVE</c> evaluated at the day boundary:
    /// <c>AcceptedAtUtc ≤ dayAnchor</c> AND (<c>ClosedAtUtc IS NULL</c> OR <c>ClosedAtUtc &gt; dayAnchor</c>).
    /// </summary>
    /// <remarks>
    /// The histogram is dense — every day in the window is emitted, even when no dossiers are
    /// active that day (TotalDisbursedMdl=0.00, BeneficiaryCount=0). The Date column is the
    /// first-of-day UTC moment formatted <c>yyyy-MM-dd</c> on the invariant culture. The
    /// window must span at least one full day (<c>toUtc &gt; fromUtc</c>) — a zero-day window
    /// is rejected with <see cref="ErrorCodes.ValidationFailed"/>.
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c> spanning ≥1 day.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDailyCashFlowAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Window must span at least one full day so the dense histogram has at least one row.
        var firstDay = new DateTime(
            fromInstant.Year, fromInstant.Month, fromInstant.Day, 0, 0, 0, DateTimeKind.Utc);
        var lastDayExclusive = new DateTime(
            toInstant.Year, toInstant.Month, toInstant.Day, 0, 0, 0, DateTimeKind.Utc);
        if (lastDayExclusive <= firstDay)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter window must span at least one calendar day.");
        }

        // Materialise the candidate dossiers once — every approved dossier whose Accepted
        // moment falls before the window end. The per-day filter happens in-memory using the
        // day anchors so EF doesn't need to translate per-day aggregates.
        var query =
            from d in _db.Dossiers
            where d.IsActive
                  && d.AcceptedAtUtc != null
                  && d.AcceptedAtUtc < lastDayExclusive
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

        var headers = new[] { "Date (UTC)", "Total Disbursed (MDL)", "Beneficiary Count" };
        var data = new List<string[]>();

        for (var day = firstDay; day < lastDayExclusive; day = day.AddDays(1))
        {
            var anchor = day;
            // Active at the day anchor — same predicate as RPT-PEN-ACTIVE.
            var active = candidates
                .Where(x => x.AcceptedAtUtc <= anchor
                            && (x.ClosedAtUtc == null || x.ClosedAtUtc > anchor))
                .ToList();

            var beneficiaryCount = active
                .Select(x => x.DossierId)
                .Distinct()
                .Count();
            var totalDisbursed = active.Sum(x => x.Amount ?? 0m);

            data.Add(new[]
            {
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                totalDisbursed.ToString("F2", CultureInfo.InvariantCulture),
                beneficiaryCount.ToString(CultureInfo.InvariantCulture),
            });
        }

        return Result<Dataset>.Success(new Dataset(headers, data));
    }
}
