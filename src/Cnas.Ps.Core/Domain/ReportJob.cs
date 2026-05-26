namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — durable work item describing one background
/// execution of a <see cref="ReportTemplate"/>. The runner picks the oldest
/// <see cref="ReportJobStatus.Queued"/> row, flips it to
/// <see cref="ReportJobStatus.Running"/>, invokes <c>IReportEngine.ExportAsync</c>,
/// persists the bytes through the R0227 attachment subsystem, and notifies the
/// requester via the R0171 in-app + MNotify orchestrator. Combines the
/// previously-shipped primitives R0156 (template + engine), R0227 (attachment +
/// blob storage) and R0171/R0128 (notification dispatch).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Linear transitions:
/// <c>Queued → Running → (Succeeded | Failed)</c>.
/// <c>Queued → Cancelled</c> is the only alternative path — once a job has flipped
/// to <see cref="ReportJobStatus.Running"/> it can no longer be cancelled (the
/// engine call is in flight). Terminal states are immutable.
/// </para>
/// <para>
/// <b>Output handoff.</b> The exported bytes are persisted through
/// <c>IAttachmentService.UploadAsync</c> with
/// <c>OwnerEntityType = "ReportJob"</c> + <c>OwnerEntityId = this.Id</c>; the
/// resulting <c>AttachmentRecord.Id</c> lands on <see cref="AttachmentRecordId"/>.
/// Clients download the bytes through the existing
/// <c>/api/attachments/{sqid}/download</c> endpoint so the runner does not need to
/// surface a parallel byte-stream API.
/// </para>
/// <para>
/// <b>Notification.</b> On every terminal transition the runner enqueues an in-app
/// notification (subject: <c>"Report.Ready"</c> on success / <c>"Report.Failed"</c>
/// on failure) addressed to <see cref="RequestedByUserId"/>. The notification
/// row carries the <see cref="AttachmentRecordId"/> when present so the citizen
/// portal can link straight to the download.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> The numeric <see cref="AuditableEntity.Id"/> never leaves
/// the system — <see cref="IExternalId"/> marks the entity as a public surface so
/// the architectural test pairs it with the corresponding Sqid-string DTO
/// (<c>ReportJobDto</c>).
/// </para>
/// </remarks>
public sealed class ReportJob : AuditableEntity, IExternalId
{
    /// <summary>Foreign-key into <see cref="ReportTemplate"/>.</summary>
    public long ReportTemplateId { get; set; }

    /// <summary>
    /// Internal <c>UserProfile.Id</c> of the actor that enqueued this job. The
    /// notifications produced on terminal transition are addressed to this user.
    /// </summary>
    public long RequestedByUserId { get; set; }

    /// <summary>
    /// Output format requested by the caller. Persisted as the underlying
    /// <see cref="int"/> ordinal — numeric values mirror
    /// <c>Cnas.Ps.Contracts.ExportFormat</c> (<c>Csv=0</c>, <c>Xlsx=1</c>,
    /// <c>Pdf=2</c>); we deliberately use the raw <see cref="int"/> in the
    /// domain layer because Core cannot reference Contracts without violating
    /// the architecture rule. The service layer round-trips the value
    /// through the typed enum at the system boundary.
    /// </summary>
    public int Format { get; set; }

    /// <summary>Lifecycle state — see <see cref="ReportJobStatus"/>.</summary>
    public ReportJobStatus Status { get; set; }

    /// <summary>UTC instant the row was first persisted (status flipped to <see cref="ReportJobStatus.Queued"/>).</summary>
    public DateTime QueuedAtUtc { get; set; }

    /// <summary>
    /// UTC instant the runner flipped the row to <see cref="ReportJobStatus.Running"/>.
    /// <c>null</c> while the row is still <see cref="ReportJobStatus.Queued"/> or
    /// has been cancelled before pickup.
    /// </summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>
    /// UTC instant the runner stamped a terminal status
    /// (<see cref="ReportJobStatus.Succeeded"/> / <see cref="ReportJobStatus.Failed"/> /
    /// <see cref="ReportJobStatus.Cancelled"/>). <c>null</c> on non-terminal rows.
    /// </summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Foreign-key into <see cref="AttachmentRecord"/> — the attachment row that
    /// carries the rendered export bytes. <c>null</c> until
    /// <see cref="Status"/> transitions to <see cref="ReportJobStatus.Succeeded"/>.
    /// Declared as <see cref="long"/> (not <c>int</c>) because the underlying PK is
    /// <see cref="long"/> across every <see cref="AuditableEntity"/>.
    /// </summary>
    public long? AttachmentRecordId { get; set; }

    /// <summary>
    /// Human-readable failure message captured when <see cref="Status"/> is
    /// <see cref="ReportJobStatus.Failed"/>. Capped at 2000 chars by the EF
    /// mapping so a runaway exception text cannot bloat the table. <c>null</c> on
    /// every non-failed row.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Wall-clock duration of the engine invocation in milliseconds. Captured by
    /// the runner across the Stopwatch envelope around
    /// <c>IReportEngine.ExportAsync</c> + the attachment upload. <c>null</c> until
    /// a terminal status is stamped.
    /// </summary>
    public int? DurationMs { get; set; }
}

/// <summary>
/// R0583 — lifecycle state of a <see cref="ReportJob"/>. Numeric values are part
/// of the persistence contract (round-tripped via the <c>Status</c> column as
/// <c>int</c>) — renumbering is a breaking change that requires a data migration.
/// </summary>
public enum ReportJobStatus
{
    /// <summary>Created but not yet picked up by the background runner.</summary>
    Queued = 0,

    /// <summary>The background runner has picked the row up and is invoking the engine.</summary>
    Running = 1,

    /// <summary>Engine succeeded — <c>AttachmentRecordId</c> populated, notification dispatched.</summary>
    Succeeded = 2,

    /// <summary>Engine failed — <c>FailureReason</c> populated, notification dispatched.</summary>
    Failed = 3,

    /// <summary>
    /// The requester cancelled the job before the runner picked it up. Only valid
    /// as a transition out of <see cref="Queued"/>; once <see cref="Running"/> a
    /// job can no longer be cancelled.
    /// </summary>
    Cancelled = 4,
}
