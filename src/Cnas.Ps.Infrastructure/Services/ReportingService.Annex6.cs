using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Annex 6 named-report extensions for <see cref="ReportingService"/>. Each builder lives in
/// this partial file so that the stock five-report dispatcher remains unmodified above the
/// closing default arm in <c>BuildDatasetAsync</c>. The new codes are recognised by
/// <see cref="IsAnnex6ReportCode"/> and dispatched by <see cref="BuildAnnex6DatasetAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// External identifiers in every row are Sqid-encoded via <c>_sqids.Encode(long)</c>
/// (CLAUDE.md RULE 3). All timestamps in headers and row values are UTC, formatted with the
/// round-trip "o" format using <see cref="CultureInfo.InvariantCulture"/>. Money is rendered
/// using the invariant culture's <c>F2</c> format so locales never inject thousand separators.
/// </para>
/// <para>
/// Filter contract for date windows: <c>[fromUtc, toUtc)</c> (half-open). This matches the
/// stock <c>AUDIT_LOG</c> report's semantics with the exception that here <c>toUtc</c> is
/// strictly excluded — boundary equality drops the row.
/// </para>
/// </remarks>
public sealed partial class ReportingService
{
    // ─────────────────────────── Codes ───────────────────────────

    /// <summary>Annex 6 — list of beneficiaries of pensions active as of a given UTC date.</summary>
    private const string PenActiveCode = "RPT-PEN-ACTIVE";

    /// <summary>Annex 6 — newly granted pensions in <c>[fromUtc, toUtc)</c>.</summary>
    private const string PenNewPeriodCode = "RPT-PEN-NEW-PERIOD";

    /// <summary>Annex 6 — dossiers in examination longer than <c>nDays</c> days.</summary>
    private const string DosPendingExamCode = "RPT-DOS-PENDING-EXAM";

    /// <summary>Annex 6 — external document requests sent but not resolved.</summary>
    private const string DocRequestsOutCode = "RPT-DOC-REQUESTS-OUT";

    /// <summary>Annex 6 — distribution of decision outcomes (Approved/Rejected) by service in a single month.</summary>
    private const string DecisionOutcomesCode = "RPT-DECISION-OUTCOMES";

    // ─────────────────────────── Dispatcher hooks ───────────────────────────

    /// <summary>True when the supplied code is one of the Annex 6 report identifiers.</summary>
    /// <param name="code">Caller-supplied report code (case-sensitive — codes are stable contract).</param>
    /// <remarks>
    /// The check chains in <see cref="IsAnnex6bReportCode"/> so the dispatcher recognises the
    /// extended Annex 6b batch without further edits to <see cref="ReportingService"/>'s
    /// <c>IsKnownReportCode</c>. New Annex 6 batches should follow the same pattern.
    /// </remarks>
    private static bool IsAnnex6ReportCode(string code)
        => code is PenActiveCode or PenNewPeriodCode or DosPendingExamCode
            or DocRequestsOutCode or DecisionOutcomesCode
            || IsAnnex6bReportCode(code);

    /// <summary>
    /// Routes an Annex 6 report code to its materialiser. Returns <see cref="ErrorCodes.NotFound"/>
    /// for unknown codes so that the existing <see cref="BuildDatasetAsync"/> default arm
    /// continues to surface the same failure shape it did before the extension.
    /// </summary>
    /// <param name="reportCode">Stable report code, e.g. <c>RPT-PEN-ACTIVE</c>.</param>
    /// <param name="parameters">Parsed JSON parameter document.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildAnnex6DatasetAsync(
        string reportCode,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        return reportCode switch
        {
            PenActiveCode => await BuildPenActiveAsync(parameters, cancellationToken).ConfigureAwait(false),
            PenNewPeriodCode => await BuildPenNewPeriodAsync(parameters, cancellationToken).ConfigureAwait(false),
            DosPendingExamCode => await BuildDosPendingExamAsync(parameters, cancellationToken).ConfigureAwait(false),
            DocRequestsOutCode => await BuildDocRequestsOutAsync(parameters, cancellationToken).ConfigureAwait(false),
            DecisionOutcomesCode => await BuildDecisionOutcomesAsync(parameters, cancellationToken).ConfigureAwait(false),
            // Annex 6b batch — appended without disturbing the original five branches above.
            _ when IsAnnex6bReportCode(reportCode) =>
                await BuildAnnex6bDatasetAsync(reportCode, parameters, cancellationToken).ConfigureAwait(false),
            _ => Result<Dataset>.Failure(ErrorCodes.NotFound, "Unknown report code"),
        };
    }

    // ─────────────────────────── RPT-PEN-ACTIVE ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-PEN-ACTIVE</c> — beneficiaries whose pension is active as of the supplied
    /// <c>asOfUtc</c> moment. Rows: DossierSqid, BeneficiaryIdnp, FullName, ServiceCode,
    /// MonthlyAmount (MDL), GrantedFromUtc.
    /// </summary>
    /// <param name="parameters">JSON object — must contain <c>asOfUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildPenActiveAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var asOfUtc = ReadUtcDate(parameters, "asOfUtc");
        if (asOfUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'asOfUtc' is required (UTC ISO 8601).");
        }
        var asOf = asOfUtc.Value;

        // Join Dossiers ⨝ Applications (Approved) ⨝ Solicitants ⨝ ServicePassports. The grant date
        // is AcceptedAtUtc on the Dossier; rows where ClosedAtUtc <= asOf are excluded.
        var query =
            from d in _db.Dossiers
            where d.IsActive
                  && d.AcceptedAtUtc != null
                  && d.AcceptedAtUtc <= asOf
                  && (d.ClosedAtUtc == null || d.ClosedAtUtc > asOf)
            join a in _db.Applications on d.ApplicationId equals a.Id
            where a.IsActive && a.Status == ApplicationStatus.Approved
            join s in _db.Solicitants on a.SolicitantId equals s.Id
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            select new
            {
                DossierId = d.Id,
                Idnp = s.NationalId,
                FullName = s.DisplayName,
                ServiceCode = p.Code,
                Amount = d.ComputedAmountMdl,
                GrantedFromUtc = d.AcceptedAtUtc!.Value,
            };

        var rows = await query
            .OrderBy(r => r.GrantedFromUtc)
            .Take(AbsoluteRowCeiling)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[]
        {
            "Dossier Sqid", "Beneficiary IDNP", "Full Name", "Service Code",
            "Monthly Amount (MDL)", "Granted From (UTC)",
        };
        var data = rows.Select(r => new[]
        {
            _sqids.Encode(r.DossierId),
            r.Idnp,
            r.FullName,
            r.ServiceCode,
            r.Amount.HasValue ? r.Amount.Value.ToString("F2", CultureInfo.InvariantCulture) : string.Empty,
            r.GrantedFromUtc.ToString("o", CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-PEN-NEW-PERIOD ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-PEN-NEW-PERIOD</c> — newly granted pensions whose decision date falls in
    /// <c>[fromUtc, toUtc)</c>. Decision date = <see cref="Dossier.AcceptedAtUtc"/>; rows are
    /// emitted only for applications in <see cref="ApplicationStatus.Approved"/>.
    /// </summary>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildPenNewPeriodAsync(JsonElement parameters, CancellationToken cancellationToken)
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
                  && d.AcceptedAtUtc != null
                  && d.AcceptedAtUtc >= fromInstant
                  && d.AcceptedAtUtc < toInstant
            join a in _db.Applications on d.ApplicationId equals a.Id
            where a.IsActive && a.Status == ApplicationStatus.Approved
            join s in _db.Solicitants on a.SolicitantId equals s.Id
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            select new
            {
                DossierId = d.Id,
                Idnp = s.NationalId,
                FullName = s.DisplayName,
                ServiceCode = p.Code,
                DecisionUtc = d.AcceptedAtUtc!.Value,
                Amount = d.ComputedAmountMdl,
            };

        var rows = await query
            .OrderBy(r => r.DecisionUtc)
            .Take(AbsoluteRowCeiling)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[]
        {
            "Dossier Sqid", "Beneficiary IDNP", "Full Name", "Service Code",
            "Decision (UTC)", "Monthly Amount (MDL)",
        };
        var data = rows.Select(r => new[]
        {
            _sqids.Encode(r.DossierId),
            r.Idnp,
            r.FullName,
            r.ServiceCode,
            r.DecisionUtc.ToString("o", CultureInfo.InvariantCulture),
            r.Amount.HasValue ? r.Amount.Value.ToString("F2", CultureInfo.InvariantCulture) : string.Empty,
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DOS-PENDING-EXAM ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOS-PENDING-EXAM</c> — open dossiers currently in
    /// <see cref="ApplicationStatus.UnderExamination"/> whose age strictly exceeds
    /// <c>nDays</c>. "Received" timestamp is the dossier's <c>CreatedAtUtc</c>;
    /// "DaysOpen" is the integer number of days between received and <c>now</c>.
    /// </summary>
    /// <param name="parameters">JSON object — must contain a positive integer <c>nDays</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDosPendingExamAsync(JsonElement parameters, CancellationToken cancellationToken)
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
            where a.IsActive && a.Status == ApplicationStatus.UnderExamination
            join s in _db.Solicitants on a.SolicitantId equals s.Id
            select new
            {
                DossierId = d.Id,
                Idnp = s.NationalId,
                ExaminerId = d.AssignedExaminerId,
                ReceivedUtc = d.CreatedAtUtc,
            };

        var rows = await query
            .OrderBy(r => r.ReceivedUtc)
            .Take(AbsoluteRowCeiling)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[]
        {
            "Dossier Sqid", "Beneficiary IDNP", "Assigned Examiner", "Received (UTC)", "Days Open",
        };
        var data = rows.Select(r => new[]
        {
            _sqids.Encode(r.DossierId),
            r.Idnp,
            r.ExaminerId.HasValue ? _sqids.Encode(r.ExaminerId.Value) : string.Empty,
            r.ReceivedUtc.ToString("o", CultureInfo.InvariantCulture),
            ((int)Math.Floor((now - r.ReceivedUtc).TotalDays))
                .ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DOC-REQUESTS-OUT ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DOC-REQUESTS-OUT</c> — external document requests that have been sent
    /// (created) in <c>[fromUtc, toUtc)</c> but not yet resolved. In the absence of a
    /// dedicated <c>ExternalDocumentRequest</c> entity, requests live as
    /// <see cref="WorkflowTask"/> rows whose <see cref="WorkflowTask.Title"/> follows the
    /// <c>DOC-REQ:&lt;TargetRegistry&gt;</c> naming convention. <c>SentUtc</c> ≡
    /// <see cref="AuditableEntity.CreatedAtUtc"/>; resolved ≡
    /// <see cref="WorkflowTask.CompletedAtUtc"/> is non-null.
    /// </summary>
    /// <param name="parameters">JSON object — must contain <c>fromUtc</c> and <c>toUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDocRequestsOutAsync(JsonElement parameters, CancellationToken cancellationToken)
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

        // We materialise the rows first and then split on the colon in-memory. EF Core does not
        // need to translate substring extraction; the predicate is restricted to a server-side
        // prefix match.
        var docReqPrefix = "DOC-REQ:";

        var query = _db.WorkflowTasks
            .Where(t => t.IsActive
                && t.CompletedAtUtc == null
                && t.CreatedAtUtc >= from
                && t.CreatedAtUtc < to
                && t.Title.StartsWith(docReqPrefix))
            .Select(t => new
            {
                RequestId = t.Id,
                DossierId = t.DossierId,
                Title = t.Title,
                SentUtc = t.CreatedAtUtc,
            });

        var now = _clock.UtcNow;
        var rows = await query
            .OrderBy(r => r.SentUtc)
            .Take(AbsoluteRowCeiling)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[]
        {
            "Request Sqid", "Dossier Sqid", "Target Registry", "Sent (UTC)", "Age Days",
        };
        var data = rows.Select(r => new[]
        {
            _sqids.Encode(r.RequestId),
            _sqids.Encode(r.DossierId),
            // Strip the "DOC-REQ:" prefix. Length is fixed; guarded by the StartsWith filter above.
            r.Title.Length > docReqPrefix.Length ? r.Title[docReqPrefix.Length..] : string.Empty,
            r.SentUtc.ToString("o", CultureInfo.InvariantCulture),
            ((int)Math.Floor((now - r.SentUtc).TotalDays))
                .ToString(CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── RPT-DECISION-OUTCOMES ───────────────────────────

    /// <summary>
    /// Builds <c>RPT-DECISION-OUTCOMES</c> — count of <see cref="ApplicationStatus.Approved"/>
    /// and <see cref="ApplicationStatus.Rejected"/> applications grouped by service code,
    /// scoped to a single calendar month (UTC). The <c>monthUtc</c> parameter is anchored to
    /// the first day of its month: any day-of-month component is ignored.
    /// </summary>
    /// <param name="parameters">JSON object — must contain <c>monthUtc</c>.</param>
    /// <param name="cancellationToken">Cancellation token propagated to EF Core.</param>
    private async Task<Result<Dataset>> BuildDecisionOutcomesAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var monthUtc = ReadUtcDate(parameters, "monthUtc");
        if (monthUtc is null)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ValidationFailed, "Parameter 'monthUtc' is required (UTC ISO 8601).");
        }
        // Anchor the window to the first-of-month UTC moment; the next month starts the upper bound.
        var startOfMonth = new DateTime(monthUtc.Value.Year, monthUtc.Value.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var startOfNextMonth = startOfMonth.AddMonths(1);

        var query =
            from a in _db.Applications
            where a.IsActive
                  && a.ClosedAtUtc != null
                  && a.ClosedAtUtc >= startOfMonth
                  && a.ClosedAtUtc < startOfNextMonth
                  && (a.Status == ApplicationStatus.Approved || a.Status == ApplicationStatus.Rejected)
            join p in _db.ServicePassports on a.ServicePassportId equals p.Id
            group a by new { p.Code, a.Status } into g
            select new { g.Key.Code, g.Key.Status, Count = g.LongCount() };

        var grouped = await query
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Service Code", "Outcome", "Count" };
        var data = grouped
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .ThenBy(r => r.Status.ToString(), StringComparer.Ordinal)
            .Select(r => new[]
            {
                r.Code,
                r.Status.ToString(),
                r.Count.ToString(CultureInfo.InvariantCulture),
            })
            .ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }
}
