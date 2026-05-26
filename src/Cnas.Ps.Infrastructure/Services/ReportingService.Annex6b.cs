using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Annex 6b named-report extensions for <see cref="ReportingService"/>. The second batch
/// of Annex 6 reports lives in its own partial file so that the original five Annex 6
/// builders (in <c>ReportingService.Annex6.cs</c>) remain unmodified. The new codes are
/// recognised by <see cref="IsAnnex6bReportCode"/> and dispatched by
/// <see cref="BuildAnnex6bDatasetAsync"/>; the parent Annex 6 dispatcher delegates to this
/// file's switch via a chained-default arm.
/// </summary>
/// <remarks>
/// <para>
/// Same conventions as the first batch — external identifiers in every row are Sqid-encoded
/// (CLAUDE.md RULE 3), timestamps are UTC formatted with the round-trip <c>"o"</c> format
/// using <see cref="CultureInfo.InvariantCulture"/>, and money is rendered using
/// <c>F2</c> on the invariant culture. Date windows are half-open: <c>[fromUtc, toUtc)</c>.
/// </para>
/// </remarks>
public sealed partial class ReportingService
{
    // ─────────────────────────── Codes ───────────────────────────

    /// <summary>Annex 6b — dossiers closed in a UTC window.</summary>
    private const string DosClosedPeriodCode = "RPT-DOS-CLOSED-PERIOD";

    /// <summary>Annex 6b — workload per examiner (aggregated, by username).</summary>
    private const string WorkloadExaminerCode = "RPT-WORKLOAD-EXAMINER";

    /// <summary>Annex 6b — per-service beneficiary count + total monthly amount for a month.</summary>
    private const string PaymentBatchSummaryCode = "RPT-PAYMENT-BATCH-SUMMARY";

    /// <summary>Annex 6b — distribution of open dossiers across fixed age buckets.</summary>
    private const string AgingDossiersCode = "RPT-AGING-DOSSIERS";

    /// <summary>Annex 6b — distribution of examiner verdicts on documents in a UTC window.</summary>
    private const string DocVerdictMixCode = "RPT-DOC-VERDICT-MIX";

    // ─────────────────────────── Dispatcher hooks ───────────────────────────

    /// <summary>True when the supplied code is one of the Annex 6b report identifiers.</summary>
    /// <param name="code">Caller-supplied report code (case-sensitive — codes are stable contract).</param>
    /// <remarks>
    /// The check chains in <see cref="IsAnnex6cReportCode"/> so the upstream dispatcher
    /// recognises the Annex 6c batch without further edits to <see cref="IsAnnex6ReportCode"/>.
    /// New Annex 6 batches should follow the same pattern.
    /// </remarks>
    private static bool IsAnnex6bReportCode(string code)
        => code is DosClosedPeriodCode or WorkloadExaminerCode or PaymentBatchSummaryCode
            or AgingDossiersCode or DocVerdictMixCode
            || IsAnnex6cReportCode(code);

    /// <summary>
    /// Routes an Annex 6b report code to its materialiser. Returns
    /// <see cref="ErrorCodes.NotFound"/> for unknown codes so the failure shape matches the
    /// parent Annex 6 dispatcher.
    /// </summary>
    /// <param name="reportCode">Stable report code, e.g. <c>RPT-DOS-CLOSED-PERIOD</c>.</param>
    /// <param name="parameters">Parsed JSON parameter document.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAnnex6bDatasetAsync(
        string reportCode,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        return reportCode switch
        {
            DosClosedPeriodCode => await BuildDosClosedPeriodAsync(parameters, cancellationToken).ConfigureAwait(false),
            WorkloadExaminerCode => await BuildWorkloadExaminerAsync(parameters, cancellationToken).ConfigureAwait(false),
            PaymentBatchSummaryCode => await BuildPaymentBatchSummaryAsync(parameters, cancellationToken).ConfigureAwait(false),
            AgingDossiersCode => await BuildAgingDossiersAsync(parameters, cancellationToken).ConfigureAwait(false),
            DocVerdictMixCode => await BuildDocVerdictMixAsync(parameters, cancellationToken).ConfigureAwait(false),
            // Annex 6c batch — appended without disturbing the original five branches above.
            _ when IsAnnex6cReportCode(reportCode) =>
                await BuildAnnex6cDatasetAsync(reportCode, parameters, cancellationToken).ConfigureAwait(false),
            _ => Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown report code"),
        };
    }

    // ─────────────────────────── RPT-DOS-CLOSED-PERIOD ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOS-CLOSED-PERIOD</c> — dossiers whose <see cref="Dossier.ClosedAtUtc"/>
    /// falls in <c>[fromUtc, toUtc)</c>. The "final outcome" column maps the underlying
    /// <see cref="ServiceApplication.Status"/> at closure to one of <c>Approved</c>,
    /// <c>Rejected</c>, or <c>Cancelled</c> (the latter covers <see cref="ApplicationStatus.Withdrawn"/>
    /// and any non-decision Closed status).
    /// </summary>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDosClosedPeriodAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        var query =
            from d in _db.Dossiers
            where d.IsActive
                  && d.ClosedAtUtc != null
                  && d.ClosedAtUtc >= fromInstant
                  && d.ClosedAtUtc < toInstant
            join a in _db.Applications on d.ApplicationId equals a.Id
            join s in _db.Solicitants on a.SolicitantId equals s.Id
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            select new
            {
                DossierId = d.Id,
                Idnp = s.NationalId,
                ServiceCode = p.Code,
                ClosedAtUtc = d.ClosedAtUtc!.Value,
                Status = a.Status,
            };

        var rows = await query
            .OrderBy(r => r.ClosedAtUtc)
            .Take(AbsoluteRowCeiling)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[]
        {
            "Dossier Sqid", "Beneficiary IDNP", "Service Code", "Closed (UTC)", "Final Outcome",
        };
        var data = rows.Select(r => new[]
        {
            _sqids.Encode(r.DossierId),
            r.Idnp,
            r.ServiceCode,
            r.ClosedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            MapFinalOutcome(r.Status),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    /// <summary>
    /// Maps an <see cref="ApplicationStatus"/> at closure time to one of the three labels
    /// surfaced in the <c>RPT-DOS-CLOSED-PERIOD</c> report: <c>Approved</c>, <c>Rejected</c>,
    /// or <c>Cancelled</c> (everything else, including <see cref="ApplicationStatus.Withdrawn"/>
    /// and a plain <see cref="ApplicationStatus.Closed"/>).
    /// </summary>
    private static string MapFinalOutcome(ApplicationStatus status) => status switch
    {
        ApplicationStatus.Approved => "Approved",
        ApplicationStatus.Rejected => "Rejected",
        _ => "Cancelled",
    };

    // ─────────────────────────── RPT-WORKLOAD-EXAMINER ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-WORKLOAD-EXAMINER</c> — counts of open <see cref="WorkflowTask"/> rows
    /// grouped by the assignee's <see cref="UserProfile.LocalLogin"/> (or
    /// <see cref="UserProfile.DisplayName"/> when no local login is set). Only tasks attached
    /// to an open dossier (<see cref="Dossier.ClosedAtUtc"/> is null) and an active application
    /// are counted. The <c>asOfUtc</c> parameter anchors the report; tasks created strictly
    /// after the anchor are excluded.
    /// </summary>
    /// <param name="parameters">JSON object — must contain <c>asOfUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildWorkloadExaminerAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var asOfUtc = ReadUtcDate(parameters, "asOfUtc");
        if (asOfUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'asOfUtc' is required (UTC ISO 8601).");
        }
        var asOf = asOfUtc.Value;
        const string waitingDocsGroup = "WAITING-DOCS";

        // Join open WorkflowTasks → Dossier (open) → UserProfile. Pull all relevant fields
        // and pivot client-side so per-examiner bucket counts are deterministic and
        // EF-provider-agnostic.
        var query =
            from t in _db.WorkflowTasks
            where t.IsActive
                  && t.CompletedAtUtc == null
                  && t.AssignedUserId != null
                  && t.CreatedAtUtc <= asOf
                  && (t.Status == WorkflowTaskStatus.Pending
                      || t.Status == WorkflowTaskStatus.InProgress
                      || t.Status == WorkflowTaskStatus.Overdue)
            join d in _db.Dossiers on t.DossierId equals d.Id
            where d.IsActive && d.ClosedAtUtc == null
            join u in _db.UserProfiles on t.AssignedUserId!.Value equals u.Id
            select new
            {
                Username = u.LocalLogin ?? u.DisplayName,
                Status = t.Status,
                GroupCode = t.GroupCode,
            };

        var raw = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        // Pivot in-memory: one row per examiner, columns are the three buckets + total.
        var buckets = raw
            .GroupBy(r => r.Username, StringComparer.Ordinal)
            .Select(g =>
            {
                var inExam = g.Count(x => x.Status == WorkflowTaskStatus.InProgress);
                var waitingDocs = g.Count(x =>
                    string.Equals(x.GroupCode, waitingDocsGroup, StringComparison.Ordinal));
                var total = g.Count();
                // "Open Dossiers" = open tasks not already counted in the more-specific buckets.
                var openDossiers = total - inExam - waitingDocs;
                if (openDossiers < 0) openDossiers = 0;
                return new
                {
                    Username = g.Key,
                    OpenDossiers = openDossiers,
                    InExamination = inExam,
                    WaitingDocs = waitingDocs,
                    Total = total,
                };
            })
            .OrderBy(r => r.Username, StringComparer.Ordinal)
            .ToList();

        var headers = new[]
        {
            "Examiner Username", "Open Dossiers", "In Examination", "Waiting Docs", "Total",
        };
        var data = buckets.Select(r => new[]
        {
            r.Username,
            r.OpenDossiers.ToString(CultureInfo.InvariantCulture),
            r.InExamination.ToString(CultureInfo.InvariantCulture),
            r.WaitingDocs.ToString(CultureInfo.InvariantCulture),
            r.Total.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-PAYMENT-BATCH-SUMMARY ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-PAYMENT-BATCH-SUMMARY</c> — per-service beneficiary count and summed
    /// monthly payable amount (MDL) for active approved dossiers as of the supplied calendar
    /// month. The <c>monthUtc</c> parameter is anchored to the first-of-month UTC moment
    /// (day component ignored). Filter is the same as <c>RPT-PEN-ACTIVE</c> evaluated at the
    /// month anchor: <c>AcceptedAtUtc ≤ monthAnchor</c> AND
    /// (<c>ClosedAtUtc IS NULL</c> OR <c>ClosedAtUtc &gt; monthAnchor</c>).
    /// </summary>
    /// <param name="parameters">JSON object — must contain <c>monthUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildPaymentBatchSummaryAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var monthUtc = ReadUtcDate(parameters, "monthUtc");
        if (monthUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'monthUtc' is required (UTC ISO 8601).");
        }
        var anchor = new DateTime(monthUtc.Value.Year, monthUtc.Value.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var query =
            from d in _db.Dossiers
            where d.IsActive
                  && d.AcceptedAtUtc != null
                  && d.AcceptedAtUtc <= anchor
                  && (d.ClosedAtUtc == null || d.ClosedAtUtc > anchor)
            join a in _db.Applications on d.ApplicationId equals a.Id
            where a.IsActive && a.Status == ApplicationStatus.Approved
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            group new { d.ComputedAmountMdl } by p.Code into g
            select new
            {
                ServiceCode = g.Key,
                BeneficiaryCount = g.LongCount(),
                TotalAmount = g.Sum(x => x.ComputedAmountMdl ?? 0m),
            };

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Service Code", "Beneficiary Count", "Total Amount (MDL)" };
        var data = rows
            .OrderBy(r => r.ServiceCode, StringComparer.Ordinal)
            .Select(r => new[]
            {
                r.ServiceCode,
                r.BeneficiaryCount.ToString(CultureInfo.InvariantCulture),
                r.TotalAmount.ToString("F2", CultureInfo.InvariantCulture),
            })
            .ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-AGING-DOSSIERS ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-AGING-DOSSIERS</c> — distribution of currently-open dossiers
    /// (<see cref="Dossier.ClosedAtUtc"/> is null) into five fixed age buckets relative to
    /// <see cref="ICnasTimeProvider.UtcNow"/>. All five buckets are always emitted, even
    /// when the count is zero — downstream consumers expect a consistent histogram.
    /// </summary>
    /// <param name="parameters">Ignored (no parameters defined for this report).</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAgingDossiersAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        _ = parameters; // No parameters required.
        var now = _clock.UtcNow;

        // Pre-compute the four cutoff moments in UTC so EF translates the bucket predicates
        // into simple range comparisons on CreatedAtUtc rather than calling out to date-diff
        // functions (which the InMemory provider doesn't support uniformly).
        var cutoff30 = now.AddDays(-30);
        var cutoff60 = now.AddDays(-60);
        var cutoff90 = now.AddDays(-90);
        var cutoff180 = now.AddDays(-180);

        var openDossiers = _db.Dossiers.Where(d => d.IsActive && d.ClosedAtUtc == null);

        // Compute each bucket count individually so the result is provider-agnostic and stable.
        var lt30 = await openDossiers.CountAsync(d => d.CreatedAtUtc > cutoff30, cancellationToken).ConfigureAwait(false);
        var b30_60 = await openDossiers.CountAsync(d => d.CreatedAtUtc <= cutoff30 && d.CreatedAtUtc > cutoff60, cancellationToken).ConfigureAwait(false);
        var b60_90 = await openDossiers.CountAsync(d => d.CreatedAtUtc <= cutoff60 && d.CreatedAtUtc > cutoff90, cancellationToken).ConfigureAwait(false);
        var b90_180 = await openDossiers.CountAsync(d => d.CreatedAtUtc <= cutoff90 && d.CreatedAtUtc > cutoff180, cancellationToken).ConfigureAwait(false);
        var gt180 = await openDossiers.CountAsync(d => d.CreatedAtUtc <= cutoff180, cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Age Bucket", "Count" };
        var data = new List<string[]>
        {
            new[] { "<30 days", lt30.ToString(CultureInfo.InvariantCulture) },
            new[] { "30-60",     b30_60.ToString(CultureInfo.InvariantCulture) },
            new[] { "60-90",     b60_90.ToString(CultureInfo.InvariantCulture) },
            new[] { "90-180",    b90_180.ToString(CultureInfo.InvariantCulture) },
            new[] { ">180",      gt180.ToString(CultureInfo.InvariantCulture) },
        };

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DOC-VERDICT-MIX ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOC-VERDICT-MIX</c> — counts of <see cref="Document"/> rows whose
    /// <see cref="Document.VerdictAtUtc"/> falls in <c>[fromUtc, toUtc)</c>, grouped by
    /// <see cref="Document.Verdict"/>. The integer verdict values correspond to
    /// <c>ExaminationVerdict</c> (Application layer): <c>0 = Accepted</c>, <c>1 = Rejected</c>,
    /// <c>2 = Held</c>. All three labels are always emitted, even when their count is zero.
    /// </summary>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDocVerdictMixAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var fromUtc = ReadUtcDate(parameters, "fromUtc");
        var toUtc = ReadUtcDate(parameters, "toUtc");
        if (fromUtc is null || toUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameters 'fromUtc' and 'toUtc' are required (UTC ISO 8601).");
        }
        var from = fromUtc.Value;
        var to = toUtc.Value;

        // Verdict integers mirror the Application-layer ExaminationVerdict enum. Stored as
        // raw int to keep Core unaware of Application types; here we count the three values
        // explicitly so we can emit zero-count rows for verdicts that didn't occur in the window.
        var inWindow = _db.Documents.Where(d => d.IsActive
            && d.Verdict != null
            && d.VerdictAtUtc != null
            && d.VerdictAtUtc >= from
            && d.VerdictAtUtc < to);

        const int acceptedValue = 0;
        const int rejectedValue = 1;
        const int heldValue = 2;

        var accepted = await inWindow.CountAsync(d => d.Verdict == acceptedValue, cancellationToken).ConfigureAwait(false);
        var rejected = await inWindow.CountAsync(d => d.Verdict == rejectedValue, cancellationToken).ConfigureAwait(false);
        var held = await inWindow.CountAsync(d => d.Verdict == heldValue, cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Verdict", "Count" };
        var data = new List<string[]>
        {
            new[] { "Accepted", accepted.ToString(CultureInfo.InvariantCulture) },
            new[] { "Rejected", rejected.ToString(CultureInfo.InvariantCulture) },
            new[] { "Held",     held.ToString(CultureInfo.InvariantCulture) },
        };

        return Result<Dataset>.Success(new Dataset(headers, data));
    }
}
