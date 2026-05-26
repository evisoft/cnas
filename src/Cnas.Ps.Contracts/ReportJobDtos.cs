namespace Cnas.Ps.Contracts;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — request body for
/// <c>POST /api/reports/jobs</c>. Carries the Sqid-encoded report template
/// reference plus the desired output format. The requester is the authenticated
/// caller — the DTO deliberately does NOT carry a <c>RequestedByUserSqid</c>
/// field so a non-admin caller cannot forge a job for someone else
/// (mass-assignment protection per CLAUDE.md §2.4 / §5.5).
/// </summary>
/// <param name="ReportTemplateSqid">
/// Sqid-encoded id of the <see cref="ReportTemplateDto"/> to execute. Decoded
/// server-side; an unparseable value yields <c>INVALID_SQID</c> (HTTP 400).
/// </param>
/// <param name="Format">
/// Stable enum name (<c>Csv</c> / <c>Xlsx</c> / <c>Pdf</c>) parsed against
/// <see cref="ExportFormat"/>. Validator rejects unknown values.
/// </param>
public sealed record ReportJobEnqueueDto(
    string ReportTemplateSqid,
    string Format);

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — output projection of a
/// <c>Cnas.Ps.Core.Domain.ReportJob</c> row. All ids are Sqid-encoded per
/// CLAUDE.md RULE 3; status / format are stable enum-name strings so the wire
/// contract survives a server-side renaming pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Status vocabulary.</b> <see cref="Status"/> is one of <c>"Queued"</c>,
/// <c>"Running"</c>, <c>"Succeeded"</c>, <c>"Failed"</c>, or <c>"Cancelled"</c>.
/// Stable strings on the wire — renaming is a breaking change.
/// </para>
/// <para>
/// <b>Attachment handoff.</b> When <see cref="Status"/> is <c>"Succeeded"</c>
/// the <see cref="AttachmentSqid"/> is populated; clients fetch the bytes via
/// the existing <c>/api/attachments/{sqid}/download</c> endpoint. The DTO does
/// NOT carry the bytes inline so the JSON response stays compact.
/// </para>
/// </remarks>
/// <param name="Id">Sqid-encoded id of the job row.</param>
/// <param name="ReportTemplateSqid">Sqid-encoded id of the source template.</param>
/// <param name="RequestedByUserSqid">Sqid-encoded id of the actor that enqueued the job.</param>
/// <param name="Format">Stable <see cref="ExportFormat"/> name (<c>Csv</c> / <c>Xlsx</c> / <c>Pdf</c>).</param>
/// <param name="Status">Stable status-enum name — see remarks for the vocabulary.</param>
/// <param name="QueuedAtUtc">UTC instant the job was enqueued.</param>
/// <param name="StartedAtUtc">UTC instant the runner picked the job up; <c>null</c> while still queued.</param>
/// <param name="CompletedAtUtc">UTC instant the job reached a terminal status; <c>null</c> on non-terminal rows.</param>
/// <param name="AttachmentSqid">
/// Sqid-encoded id of the <see cref="AttachmentRecordDto"/> carrying the export bytes;
/// populated only when <see cref="Status"/> is <c>"Succeeded"</c>.
/// </param>
/// <param name="FailureReason">Human-readable failure message; populated only when <see cref="Status"/> is <c>"Failed"</c>.</param>
/// <param name="DurationMs">Wall-clock duration of the engine invocation; <c>null</c> on non-terminal rows.</param>
public sealed record ReportJobDto(
    string Id,
    string ReportTemplateSqid,
    string RequestedByUserSqid,
    string Format,
    string Status,
    DateTime QueuedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? AttachmentSqid,
    string? FailureReason,
    int? DurationMs);
