namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2161 / TOR INT 002 — a generic offline batch job submitted by a CNAS user
/// through the <c>/api/offline-batch/*</c> surface. Captures the metadata
/// needed to run an ingest or export over a multi-record payload without
/// holding the HTTP request open. The processor side is owned by
/// <c>OfflineBatchProcessor</c> in Infrastructure/Jobs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> This is the lightweight, user-facing batch primitive — separate
/// from the Annex-4 / INT 002 B2B file-based registry shipped under R1710
/// (<see cref="OfflineBatchSubmission"/>). The R1710 surface is tailored to
/// Annex-4 op codes and consumer-subject scoping; <see cref="OfflineBatchJob"/>
/// is the generic CnasUser-facing fallback for ad-hoc ingest / export.
/// </para>
/// <para>
/// <b>Lifecycle.</b>
/// <list type="bullet">
///   <item><c>Pending</c> — row inserted by the submission service.</item>
///   <item><c>Running</c> — processor picked the row up, stamped <see cref="StartedAtUtc"/>.</item>
///   <item><c>Completed</c> — terminal happy-path; <see cref="ResultBlobKey"/> populated.</item>
///   <item><c>Failed</c> — terminal sad-path; <see cref="ErrorMessage"/> populated.</item>
/// </list>
/// </para>
/// <para>
/// <b>No PII.</b> The row carries only kind / status / actor id / timestamps /
/// sanitised error string / opaque blob key. The payload itself is held on
/// the processor side (a future <c>IOfflineBatchBlobStore</c> integration);
/// rows here never echo user-supplied content.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>OfflineBatchJobDto.Id</c>) carries a Sqid-encoded
/// surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class OfflineBatchJob : AuditableEntity, IExternalId
{
    /// <summary>Discriminator between ingest and export jobs.</summary>
    public OfflineBatchJobKind Kind { get; set; }

    /// <summary>Current lifecycle status; defaults to <see cref="OfflineBatchJobStatus.Pending"/>.</summary>
    public OfflineBatchJobStatus Status { get; set; } = OfflineBatchJobStatus.Pending;

    /// <summary>Internal id of the CnasUser who submitted the job.</summary>
    public long SubmittedByUserId { get; set; }

    /// <summary>UTC instant the submission row was inserted.</summary>
    public DateTime SubmittedAtUtc { get; set; }

    /// <summary>UTC instant the processor began running the job; null while still <c>Pending</c>.</summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>UTC instant the processor finalised the job; null while not yet terminal.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Sanitised processor failure reason — populated when <see cref="Status"/> is <see cref="OfflineBatchJobStatus.Failed"/>.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Opaque blob-storage key for the produced artefact — populated when <see cref="Status"/> is <see cref="OfflineBatchJobStatus.Completed"/>.</summary>
    public string? ResultBlobKey { get; set; }

    /// <summary>Count of rows the consumer submitted (validated ≤ 10 000 by the service layer).</summary>
    public int RowCount { get; set; }
}

/// <summary>
/// R2161 / TOR INT 002 — discriminator between ingest and export
/// <see cref="OfflineBatchJob"/> rows. Stored as a stable string in the
/// database so renames are caught at compile time.
/// </summary>
public enum OfflineBatchJobKind
{
    /// <summary>Inbound batch — caller is sending data into CNAS.</summary>
    Ingest = 1,

    /// <summary>Outbound batch — caller is requesting a bulk export from CNAS.</summary>
    Export = 2,
}

/// <summary>
/// R2161 / TOR INT 002 — lifecycle states of an <see cref="OfflineBatchJob"/>.
/// </summary>
public enum OfflineBatchJobStatus
{
    /// <summary>Initial state after submission; waiting for the processor.</summary>
    Pending = 1,

    /// <summary>Processor picked the row up and is iterating.</summary>
    Running = 2,

    /// <summary>Terminal happy-path; <see cref="OfflineBatchJob.ResultBlobKey"/> is populated.</summary>
    Completed = 3,

    /// <summary>Terminal sad-path; <see cref="OfflineBatchJob.ErrorMessage"/> is populated.</summary>
    Failed = 4,
}
