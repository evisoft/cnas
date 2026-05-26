namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — append-only execution history for a
/// <see cref="ReportTemplate"/>. One row per <c>IReportEngine.RunAsync</c> /
/// <c>ExportAsync</c> call, capturing who ran the template, when, how many rows it
/// produced, the outcome code, the wall-clock duration, and (for failures) the
/// human-readable reason.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only.</b> Rows are written once at run completion and never updated.
/// Soft-delete is not exposed at the service layer — the
/// <see cref="AuditableEntity.IsActive"/> column remains <c>true</c> for every row.
/// A future retention job may hard-delete rows older than the configured horizon
/// (out of scope for R0156).
/// </para>
/// <para>
/// <b>Outcome vocabulary.</b> <see cref="OutcomeCode"/> is one of:
/// <list type="bullet">
///   <item><c>"Success"</c> — the engine materialised the result set within budget.</item>
///   <item><c>"BudgetExceeded"</c> — the R0167 query-budget guard refused the run.</item>
///   <item><c>"ValidationFailed"</c> — the template's JSON payloads failed validation
///       (schema drift after a registry edit, etc.).</item>
///   <item><c>"ExportFailed"</c> — the export renderer returned a failure
///       (<c>EXPORT_TOO_LARGE</c> / <c>EXPORT_FORMAT_NOT_SUPPORTED</c>).</item>
/// </list>
/// Stable strings on the wire — renaming is a breaking change.
/// </para>
/// <para>
/// <b>No external surface.</b> Run rows are not currently surfaced through a DTO;
/// they are read by future operations dashboards and forensics queries. The entity
/// therefore does NOT implement <see cref="IExternalId"/> — the architecture test
/// pairs that marker with the existence of a public Sqid-string DTO.
/// </para>
/// </remarks>
public sealed class ReportRun : AuditableEntity
{
    /// <summary>
    /// Foreign-key into <see cref="ReportTemplate"/>. The pair
    /// (<see cref="ReportTemplateId"/>, <see cref="ExecutedAtUtc"/>) is indexed
    /// descending so the most recent runs of a template can be retrieved cheaply.
    /// </summary>
    public long ReportTemplateId { get; set; }

    /// <summary>
    /// Internal <c>UserProfile.Id</c> of the actor that executed the template. May be
    /// any user with <c>Reports.View</c> on the template (i.e. the owner or any caller
    /// when <see cref="ReportTemplate.IsShared"/> is <c>true</c>).
    /// </summary>
    public long ExecutedByUserId { get; set; }

    /// <summary>
    /// UTC wall-clock instant the run completed. Captured from
    /// <c>ICnasTimeProvider.UtcNow</c> AFTER the work finished so the duration reading
    /// is consistent with the timestamp.
    /// </summary>
    public DateTime ExecutedAtUtc { get; set; }

    /// <summary>
    /// Number of rows the run produced (post-paging-and-projection). Cached at
    /// execution time so historical reports can be rendered without re-running.
    /// </summary>
    public int RowCount { get; set; }

    /// <summary>
    /// Stable outcome string — one of <c>"Success"</c>, <c>"BudgetExceeded"</c>,
    /// <c>"ValidationFailed"</c>, <c>"ExportFailed"</c>. See class-level remarks.
    /// </summary>
    public required string OutcomeCode { get; set; }

    /// <summary>
    /// Wall-clock duration of the run in milliseconds. Captured via
    /// <see cref="System.Diagnostics.Stopwatch"/>; bounded by the per-call CNAS query
    /// budget timeout.
    /// </summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// Human-readable failure reason captured when <see cref="OutcomeCode"/> is not
    /// <c>"Success"</c>. <c>null</c> on the success path. Capped at 512 chars by the
    /// EF mapping so a pathological exception message cannot bloat the table.
    /// </summary>
    public string? FailureReason { get; set; }
}
