namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1710 / TOR INT 002 / Annex 4 — one offline-batch submission uploaded by a
/// B2B consumer. The consumer uploads a CSV request file containing many rows
/// of inputs, the system queues a job, processes each row against the same
/// underlying interop query as the synchronous endpoint, produces a response
/// CSV, signs it, and makes it available for download. Useful for nightly
/// reconciliations and large back-fills.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> The consumer-facing service inserts a <c>Submitted</c>
/// row, persists the request CSV via <c>IOfflineBatchBlobStore</c>, parses the
/// rows into <see cref="OfflineBatchRow"/> children, and transitions the
/// submission to <c>Queued</c>. The Quartz job
/// <c>OfflineBatchProcessingJob</c> picks the oldest <c>Queued</c> row per
/// fire, calls <c>IOfflineBatchProcessor.ProcessAsync</c>, which transitions
/// the row through <c>Running → Completed</c> (or <c>Failed</c>).
/// </para>
/// <para>
/// <b>Cancellation.</b> A <c>Submitted</c> or <c>Queued</c> submission can be
/// cancelled by the originating consumer; a <c>Running</c> submission cannot
/// be — the processor is mid-iteration and the response file is being
/// streamed to blob storage.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.OfflineBatchSubmissionDto.Id</c>)
/// carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>No PII.</b> Neither the submission row nor the
/// <see cref="OfflineBatchRow"/> children echo IDNP / IDNO / IBAN text. The
/// <see cref="RequestFileStorageKey"/> + <see cref="ResponseFileStorageKey"/>
/// columns are opaque storage keys. The row-level error description is
/// sanitised by the processor to the stable error-code string only — no
/// citizen-identifying fragments leak.
/// </para>
/// </remarks>
public sealed class OfflineBatchSubmission : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable human-readable batch number, shape <c>OBS-{year}-{seq:000000}</c>.
    /// Unique across the registry. Surfaces on the response CSV's
    /// <c>Content-Disposition</c> filename.
    /// </summary>
    public string BatchNumber { get; set; } = string.Empty;

    /// <summary>
    /// Opaque identifier of the B2B consumer that submitted the batch
    /// (typically the OAuth client id / MConnect subject). Plain string —
    /// NOT a Sqid (external stable identifier).
    /// </summary>
    public string ConsumerSubject { get; set; } = string.Empty;

    /// <summary>Stable enum-name of the Annex-4 op the batch targets.</summary>
    public AnnexFourBatchOp OpCode { get; set; }

    /// <summary>Lifecycle status — defaults to <see cref="OfflineBatchStatus.Submitted"/>.</summary>
    public OfflineBatchStatus Status { get; set; } = OfflineBatchStatus.Submitted;

    /// <summary>Original filename the consumer uploaded — must end in <c>.csv</c>.</summary>
    public string RequestFileName { get; set; } = string.Empty;

    /// <summary>Size of the uploaded request CSV in bytes (1 byte ≤ size ≤ 10 MB).</summary>
    public long RequestFileSizeBytes { get; set; }

    /// <summary>Hex-encoded SHA-256 hash of the uploaded request CSV (64 lower-case hex chars).</summary>
    public string RequestFileHashSha256 { get; set; } = string.Empty;

    /// <summary>Opaque storage key for the persisted request CSV (resolvable via <c>IOfflineBatchBlobStore</c>).</summary>
    public string RequestFileStorageKey { get; set; } = string.Empty;

    /// <summary>Count of data rows parsed from the request CSV. Excludes the header row.</summary>
    public int RequestRowCount { get; set; }

    /// <summary>Opaque storage key of the generated response CSV — null until <see cref="OfflineBatchStatus.Completed"/>.</summary>
    public string? ResponseFileStorageKey { get; set; }

    /// <summary>Hex-encoded SHA-256 hash of the generated response CSV — null until <see cref="OfflineBatchStatus.Completed"/>.</summary>
    public string? ResponseFileHashSha256 { get; set; }

    /// <summary>Base64 HMAC-SHA256 signature of the generated response CSV — null until <see cref="OfflineBatchStatus.Completed"/>.</summary>
    public string? ResponseFileSignatureBase64 { get; set; }

    /// <summary>UTC timestamp the submission row was created.</summary>
    public DateTime SubmittedAt { get; set; }

    /// <summary>UTC timestamp the processor started running this batch. Null until <see cref="OfflineBatchStatus.Running"/>.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>UTC timestamp the processor finalised this batch. Null until <see cref="OfflineBatchStatus.Completed"/> or <see cref="OfflineBatchStatus.Failed"/>.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Sanitised processor failure reason — populated when <see cref="Status"/> is <see cref="OfflineBatchStatus.Failed"/>.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Count of rows processed (succeeded + failed). Equals <see cref="RequestRowCount"/> at completion.</summary>
    public int TotalRowsProcessed { get; set; }

    /// <summary>Count of rows that ended in <see cref="OfflineBatchRowStatus.Failed"/>.</summary>
    public int TotalRowsFailed { get; set; }
}
