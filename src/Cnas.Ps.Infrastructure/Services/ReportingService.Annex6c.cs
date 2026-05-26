using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Annex 6c named-report extensions for <see cref="ReportingService"/>. The fourth batch of
/// Annex 6 reports lives in its own partial file so that the earlier batches (the original
/// five in <c>ReportingService.Annex6.cs</c> and the second batch in
/// <c>ReportingService.Annex6b.cs</c>) remain untouched. The new codes are recognised by
/// <see cref="IsAnnex6cReportCode"/> and dispatched by <see cref="BuildAnnex6cDatasetAsync"/>;
/// the Annex 6b dispatcher chains into this file via a single <c>_ when</c> arm so new code
/// only requires one minimal upstream edit.
/// </summary>
/// <remarks>
/// <para>
/// Conventions match the earlier batches — external identifiers in every row are Sqid-encoded
/// (CLAUDE.md RULE 3), timestamps are UTC formatted with the round-trip <c>"o"</c> format
/// using <see cref="CultureInfo.InvariantCulture"/>, money is rendered using <c>F2</c> on the
/// invariant culture, and date windows are half-open <c>[fromUtc, toUtc)</c>.
/// </para>
/// </remarks>
public sealed partial class ReportingService
{
    // ─────────────────────────── Codes ───────────────────────────

    /// <summary>Annex 6c — inbox of currently open contestații (appeals).</summary>
    private const string AppealInboxCode = "RPT-APPEAL-INBOX";

    /// <summary>Annex 6c — document requests resolved in the last <c>nDays</c> days.</summary>
    private const string DocRequestsClosedRecentCode = "RPT-DOC-REQUESTS-CLOSED-RECENT";

    /// <summary>Annex 6c — per-examiner dossier-assignment counts inside a UTC window.</summary>
    private const string DossierAssignmentsPerExaminerCode = "RPT-DOSSIER-ASSIGNMENTS-PER-EXAMINER";

    /// <summary>Annex 6c — month-by-month synthesised payment history for a dossier.</summary>
    private const string PaymentHistoryCode = "RPT-PAYMENT-HISTORY";

    /// <summary>Annex 6c — distribution of dossiers across services as of a UTC moment.</summary>
    private const string DossiersByServiceCode = "RPT-DOSSIERS-BY-SERVICE";

    // ─────────────────────────── Dispatcher hooks ───────────────────────────

    /// <summary>True when the supplied code is one of the Annex 6c report identifiers.</summary>
    /// <param name="code">Caller-supplied report code (case-sensitive — codes are stable contract).</param>
    private static bool IsAnnex6cReportCode(string code)
        => code is AppealInboxCode or DocRequestsClosedRecentCode
            or DossierAssignmentsPerExaminerCode or PaymentHistoryCode
            or DossiersByServiceCode
            || IsAnnex6dReportCode(code);

    /// <summary>
    /// Routes an Annex 6c report code to its materialiser. Returns
    /// <see cref="ErrorCodes.NotFound"/> for unknown codes so the failure shape matches the
    /// earlier Annex 6 dispatchers.
    /// </summary>
    /// <param name="reportCode">Stable report code, e.g. <c>RPT-APPEAL-INBOX</c>.</param>
    /// <param name="parameters">Parsed JSON parameter document.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAnnex6cDatasetAsync(
        string reportCode,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        return reportCode switch
        {
            AppealInboxCode => await BuildAppealInboxAsync(parameters, cancellationToken).ConfigureAwait(false),
            DocRequestsClosedRecentCode => await BuildDocRequestsClosedRecentAsync(parameters, cancellationToken).ConfigureAwait(false),
            DossierAssignmentsPerExaminerCode => await BuildDossierAssignmentsPerExaminerAsync(parameters, cancellationToken).ConfigureAwait(false),
            PaymentHistoryCode => await BuildPaymentHistoryAsync(parameters, cancellationToken).ConfigureAwait(false),
            DossiersByServiceCode => await BuildDossiersByServiceAsync(parameters, cancellationToken).ConfigureAwait(false),
            // Annex 6d batch — appended without disturbing the original five branches above.
            _ when IsAnnex6dReportCode(reportCode) =>
                await BuildAnnex6dDatasetAsync(reportCode, parameters, cancellationToken).ConfigureAwait(false),
            _ => Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown report code"),
        };
    }

    // ─────────────────────────── RPT-APPEAL-INBOX ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-APPEAL-INBOX</c> — the inbox of currently open contestații. In the
    /// absence of a dedicated Appeal entity, appeals are sourced from
    /// <see cref="WorkflowTask"/> rows whose <see cref="WorkflowTask.Title"/> begins with
    /// the prefix <c>APPEAL:</c>. An appeal is "open" when
    /// <see cref="WorkflowTask.CompletedAtUtc"/> is <see langword="null"/>. Each row carries
    /// the appeal Sqid, parent dossier Sqid, beneficiary IDNP, the moment the appeal was
    /// filed (UTC), and its age in whole days computed against <see cref="ICnasTimeProvider.UtcNow"/>.
    /// </summary>
    /// <param name="parameters">Ignored — this report takes no parameters.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAppealInboxAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        _ = parameters; // No parameters required.
        const string appealPrefix = "APPEAL:";
        var now = _clock.UtcNow;

        var query =
            from t in _db.WorkflowTasks
            where t.IsActive
                  && t.CompletedAtUtc == null
                  && t.Title.StartsWith(appealPrefix)
            join d in _db.Dossiers on t.DossierId equals d.Id
            join a in _db.Applications on d.ApplicationId equals a.Id
            join s in _db.Solicitants on a.SolicitantId equals s.Id
            select new
            {
                AppealId = t.Id,
                DossierId = d.Id,
                Idnp = s.NationalId,
                FiledUtc = t.CreatedAtUtc,
            };

        var rows = await query
            .OrderBy(r => r.FiledUtc)
            .Take(AbsoluteRowCeiling)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[]
        {
            "Appeal Sqid", "Dossier Sqid", "Beneficiary IDNP", "Filed (UTC)", "Age Days",
        };
        var data = rows.Select(r => new[]
        {
            _sqids.Encode(r.AppealId),
            _sqids.Encode(r.DossierId),
            r.Idnp,
            r.FiledUtc.ToString("o", CultureInfo.InvariantCulture),
            ((int)Math.Floor((now - r.FiledUtc).TotalDays))
                .ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DOC-REQUESTS-CLOSED-RECENT ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOC-REQUESTS-CLOSED-RECENT</c> — external document requests resolved
    /// in the last <c>nDays</c> days. Sourced from <see cref="WorkflowTask"/> rows whose
    /// <see cref="WorkflowTask.Title"/> follows the <c>DOC-REQ:&lt;TargetRegistry&gt;</c>
    /// convention and whose <see cref="WorkflowTask.CompletedAtUtc"/> is non-null and ≥
    /// <c>now - nDays</c>. <c>TurnaroundDays</c> is the elapsed days between
    /// <see cref="AuditableEntity.CreatedAtUtc"/> (sent) and <see cref="WorkflowTask.CompletedAtUtc"/>
    /// (resolved), rounded to a single decimal place using <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    /// <param name="parameters">JSON object — must contain a positive integer <c>nDays</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDocRequestsClosedRecentAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var nDays = ReadInt(parameters, "nDays");
        if (nDays is null || nDays.Value <= 0)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'nDays' is required and must be a positive integer.");
        }

        const string docReqPrefix = "DOC-REQ:";
        var now = _clock.UtcNow;
        var cutoff = now.AddDays(-nDays.Value);

        var query = _db.WorkflowTasks
            .Where(t => t.IsActive
                && t.CompletedAtUtc != null
                && t.CompletedAtUtc >= cutoff
                && t.Title.StartsWith(docReqPrefix))
            .Select(t => new
            {
                RequestId = t.Id,
                DossierId = t.DossierId,
                Title = t.Title,
                SentUtc = t.CreatedAtUtc,
                ResolvedUtc = t.CompletedAtUtc!.Value,
            });

        var rows = await query
            .OrderBy(r => r.ResolvedUtc)
            .Take(AbsoluteRowCeiling)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[]
        {
            "Request Sqid", "Dossier Sqid", "Target Registry",
            "Sent (UTC)", "Resolved (UTC)", "Turnaround Days",
        };
        var data = rows.Select(r => new[]
        {
            _sqids.Encode(r.RequestId),
            _sqids.Encode(r.DossierId),
            // Strip the "DOC-REQ:" prefix to surface only the target registry.
            r.Title.Length > docReqPrefix.Length ? r.Title[docReqPrefix.Length..] : string.Empty,
            r.SentUtc.ToString("o", CultureInfo.InvariantCulture),
            r.ResolvedUtc.ToString("o", CultureInfo.InvariantCulture),
            Math.Round((r.ResolvedUtc - r.SentUtc).TotalDays, 1)
                .ToString("F1", CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DOSSIER-ASSIGNMENTS-PER-EXAMINER ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOSSIER-ASSIGNMENTS-PER-EXAMINER</c> — per-examiner aggregate of
    /// dossier assignments inside the UTC window <c>[fromUtc, toUtc)</c>. Each row carries
    /// the examiner's local login (or <see cref="UserProfile.DisplayName"/> fallback) and
    /// three counts derived from <see cref="WorkflowTask"/> rows with
    /// <see cref="AuditableEntity.CreatedAtUtc"/> inside the window:
    /// <list type="bullet">
    ///   <item><c>Assigned</c> — total tasks created in window for that examiner.</item>
    ///   <item><c>Reassigned</c> — tasks whose dossier had more than one distinct assignee
    ///         across all in-window tasks (i.e. evidence of hand-off).</item>
    ///   <item><c>Closed</c> — tasks whose <see cref="WorkflowTask.CompletedAtUtc"/> is also
    ///         inside the window (work finished within the period).</item>
    /// </list>
    /// </summary>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDossierAssignmentsPerExaminerAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // Pull every in-window task with its assignee. The "Reassigned" detection requires
        // knowing the set of distinct assignees per dossier — we compute it in memory after
        // materialising because the InMemory EF provider does not handle some Distinct/
        // GroupBy/Count combinations identically to Postgres.
        var query =
            from t in _db.WorkflowTasks
            where t.IsActive
                  && t.AssignedUserId != null
                  && t.CreatedAtUtc >= fromInstant
                  && t.CreatedAtUtc < toInstant
            join u in _db.UserProfiles on t.AssignedUserId!.Value equals u.Id
            select new
            {
                DossierId = t.DossierId,
                Username = u.LocalLogin ?? u.DisplayName,
                CreatedAtUtc = t.CreatedAtUtc,
                CompletedAtUtc = t.CompletedAtUtc,
            };

        var raw = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        // Per-dossier set of distinct assignees: a task lives in the "Reassigned" bucket
        // when its dossier saw >1 distinct username across the in-window tasks.
        var assigneesPerDossier = raw
            .GroupBy(r => r.DossierId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Username).Distinct(StringComparer.Ordinal).Count());

        var grouped = raw
            .GroupBy(r => r.Username, StringComparer.Ordinal)
            .Select(g => new
            {
                Username = g.Key,
                Assigned = g.Count(),
                Reassigned = g.Count(r => assigneesPerDossier[r.DossierId] > 1),
                Closed = g.Count(r => r.CompletedAtUtc != null
                                       && r.CompletedAtUtc >= fromInstant
                                       && r.CompletedAtUtc < toInstant),
            })
            .OrderBy(r => r.Username, StringComparer.Ordinal)
            .ToList();

        var headers = new[] { "Examiner Username", "Assigned", "Reassigned", "Closed" };
        var data = grouped.Select(r => new[]
        {
            r.Username,
            r.Assigned.ToString(CultureInfo.InvariantCulture),
            r.Reassigned.ToString(CultureInfo.InvariantCulture),
            r.Closed.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-PAYMENT-HISTORY ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-PAYMENT-HISTORY</c> — synthesised month-by-month payment history for
    /// the beneficiary identified by <paramref name="parameters"/>.<c>dossierSqid</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deterministic synthetic data — no Payment entity exists.</b> Because the current
    /// data model does not yet carry a Payment / PaymentHistory entity, this report derives
    /// its rows by counting the calendar months elapsed between the dossier's
    /// <see cref="Dossier.AcceptedAtUtc"/> (granted-from) and <see cref="ICnasTimeProvider.UtcNow"/>,
    /// emitting one row per elapsed month at <see cref="Dossier.ComputedAmountMdl"/> (or
    /// zero when null) and marking every row as <c>Paid</c>. When a real Payment entity
    /// arrives this builder should be rewritten to project from the actual payment table.
    /// </para>
    /// <para>
    /// "Months elapsed" is calculated against the first-of-month anchors of granted and
    /// now: a row is emitted for each first-of-month strictly between
    /// <see cref="Dossier.AcceptedAtUtc"/>'s month-anchor (exclusive) and now's
    /// month-anchor (inclusive). A dossier granted in the current month or in the future
    /// produces zero rows.
    /// </para>
    /// </remarks>
    /// <param name="parameters">JSON object — must contain a non-empty <c>dossierSqid</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildPaymentHistoryAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var dossierSqid = ReadString(parameters, "dossierSqid");
        if (string.IsNullOrWhiteSpace(dossierSqid))
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'dossierSqid' is required.");
        }

        var decode = _sqids.TryDecode(dossierSqid);
        if (decode.IsFailure)
        {
            return Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown dossier.");
        }
        var dossierId = decode.Value;

        var dossier = await _db.Dossiers
            .Where(d => d.IsActive && d.Id == dossierId)
            .Select(d => new
            {
                d.AcceptedAtUtc,
                d.ComputedAmountMdl,
            })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (dossier is null)
        {
            return Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown dossier.");
        }

        var headers = new[] { "Month (UTC)", "Amount (MDL)", "Status" };
        var data = new List<string[]>();

        // No granted moment → no synthesised history. Same when the dossier is granted in
        // the future or in the current calendar month (nothing has "ended" yet).
        if (dossier.AcceptedAtUtc is null)
        {
            return Result<Dataset>.Success(new Dataset(headers, data));
        }

        var granted = dossier.AcceptedAtUtc.Value;
        var now = _clock.UtcNow;
        var grantedAnchor = new DateTime(granted.Year, granted.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nowAnchor = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // Number of fully-elapsed calendar months between the two anchors.
        var elapsed = ((nowAnchor.Year - grantedAnchor.Year) * 12) + (nowAnchor.Month - grantedAnchor.Month);
        if (elapsed <= 0)
        {
            return Result<Dataset>.Success(new Dataset(headers, data));
        }

        var amount = dossier.ComputedAmountMdl ?? 0m;
        var amountStr = amount.ToString("F2", CultureInfo.InvariantCulture);

        for (var i = 1; i <= elapsed; i++)
        {
            var monthAnchor = grantedAnchor.AddMonths(i);
            data.Add(new[]
            {
                monthAnchor.ToString("o", CultureInfo.InvariantCulture),
                amountStr,
                "Paid",
            });
        }

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DOSSIERS-BY-SERVICE ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOSSIERS-BY-SERVICE</c> — per-service distribution of dossiers in
    /// existence as of <paramref name="parameters"/>.<c>asOfUtc</c>. A dossier "exists at
    /// asOf" when its <see cref="AuditableEntity.CreatedAtUtc"/> is ≤ the supplied moment.
    /// </summary>
    /// <remarks>
    /// Each row carries the <see cref="ServicePassport.Code"/>, its
    /// <see cref="ServicePassport.NameRo"/> RO title, and four per-status buckets derived
    /// from the owning <see cref="ServiceApplication.Status"/>:
    /// <list type="bullet">
    ///   <item><c>Open</c> — anything before a final outcome (Draft, Submitted,
    ///         RejectedIncomplete, UnderExamination, PendingApproval).</item>
    ///   <item><c>Approved</c> — <see cref="ApplicationStatus.Approved"/>.</item>
    ///   <item><c>Rejected</c> — <see cref="ApplicationStatus.Rejected"/>.</item>
    ///   <item><c>Closed</c> — <see cref="ApplicationStatus.Closed"/> or
    ///         <see cref="ApplicationStatus.Withdrawn"/>.</item>
    /// </list>
    /// Services with no dossiers at <c>asOfUtc</c> do not appear (no zero-row filler).
    /// </remarks>
    /// <param name="parameters">JSON object — must contain <c>asOfUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDossiersByServiceAsync(JsonElement parameters, CancellationToken cancellationToken)
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
            where d.IsActive && d.CreatedAtUtc <= asOf
            join a in _db.Applications on d.ApplicationId equals a.Id
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            select new
            {
                Code = p.Code,
                Title = p.NameRo,
                Status = a.Status,
            };

        var raw = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var grouped = raw
            .GroupBy(r => new { r.Code, r.Title })
            .Select(g => new
            {
                g.Key.Code,
                g.Key.Title,
                Open = g.Count(x => IsOpenStatus(x.Status)),
                Approved = g.Count(x => x.Status == ApplicationStatus.Approved),
                Rejected = g.Count(x => x.Status == ApplicationStatus.Rejected),
                Closed = g.Count(x =>
                    x.Status == ApplicationStatus.Closed || x.Status == ApplicationStatus.Withdrawn),
            })
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .ToList();

        var headers = new[]
        {
            "Service Code", "Service Title (RO)", "Open", "Approved", "Rejected", "Closed",
        };
        var data = grouped.Select(r => new[]
        {
            r.Code,
            r.Title,
            r.Open.ToString(CultureInfo.InvariantCulture),
            r.Approved.ToString(CultureInfo.InvariantCulture),
            r.Rejected.ToString(CultureInfo.InvariantCulture),
            r.Closed.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    /// <summary>
    /// Maps an <see cref="ApplicationStatus"/> to whether it counts toward the "Open"
    /// bucket in <c>RPT-DOSSIERS-BY-SERVICE</c>. Anything before a final outcome — i.e.
    /// not Approved / Rejected / Closed / Withdrawn — is open.
    /// </summary>
    private static bool IsOpenStatus(ApplicationStatus status) => status switch
    {
        ApplicationStatus.Draft => true,
        ApplicationStatus.Submitted => true,
        ApplicationStatus.RejectedIncomplete => true,
        ApplicationStatus.UnderExamination => true,
        ApplicationStatus.PendingApproval => true,
        _ => false,
    };
}
