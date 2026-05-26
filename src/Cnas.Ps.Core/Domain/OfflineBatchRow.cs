namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1710 / TOR INT 002 / Annex 4 — one data row inside an
/// <see cref="OfflineBatchSubmission"/>. Each row mirrors a single
/// synchronous Annex-4 op invocation: the request payload is a JSON
/// snapshot of the op's input DTO, and (on success) the response payload
/// is a JSON snapshot of the op's output DTO.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> so admin tooling
/// can reference individual rows by Sqid.
/// </para>
/// <para>
/// <b>Sanitised error fields.</b> When the underlying interop call fails the
/// processor stores only the stable <see cref="ErrorCode"/> + a short,
/// PII-free <see cref="ErrorDescription"/>. IDNP / IDNO / IBAN fragments are
/// never persisted to these columns.
/// </para>
/// </remarks>
public sealed class OfflineBatchRow : AuditableEntity, IExternalId
{
    /// <summary>FK pointer back to <c>OfflineBatchSubmission.Id</c>.</summary>
    public long SubmissionId { get; set; }

    /// <summary>1-based position of the row inside the request file (excluding the header).</summary>
    public int RowOrdinal { get; set; }

    /// <summary>Per-row lifecycle status — defaults to <see cref="OfflineBatchRowStatus.Pending"/>.</summary>
    public OfflineBatchRowStatus Status { get; set; } = OfflineBatchRowStatus.Pending;

    /// <summary>
    /// JSON snapshot of the op's input DTO mirroring the synchronous shape.
    /// Bounded to 4096 chars by the configuration so a single pathological
    /// row cannot blow out the registry.
    /// </summary>
    public string RequestPayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// JSON snapshot of the op's output DTO. Populated on
    /// <see cref="OfflineBatchRowStatus.Succeeded"/>; <c>null</c> on Pending
    /// / Failed.
    /// </summary>
    public string? ResponsePayloadJson { get; set; }

    /// <summary>Stable error code from the underlying interop call (e.g. <c>NOT_FOUND</c>). Null on success.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Short, PII-free description of the failure. Null on success.</summary>
    public string? ErrorDescription { get; set; }

    /// <summary>UTC timestamp the processor finalised this row. Null while Pending.</summary>
    public DateTime? ProcessedAt { get; set; }
}
