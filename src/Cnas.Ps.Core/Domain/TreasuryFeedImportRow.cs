namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1810 / TOR BP 1.2-I — one row per parsed line of a Treasury feed file.
/// Captures the raw parsed payload, the row outcome, and (on success) the
/// resulting <c>TreasuryPaymentReceipt</c> id for forensic replay.
/// </summary>
/// <remarks>
/// <para>
/// <b>Forensic replay.</b> The <see cref="RawPayloadJson"/> column stores a
/// JSON snapshot of the parsed input fields so operators can replay a single
/// row without re-fetching the source file. Bounded to 4096 chars by the EF
/// configuration so a pathological row cannot blow out the registry.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because admin
/// tooling references individual rows by Sqid.
/// </para>
/// <para>
/// <b>MappedReceiptId is intentionally raw long.</b> CLAUDE.md RULE 3 has a
/// documented exception for internal-ops fields: the import-row aggregate is
/// only ever consumed by operators inside the admin surface, never crosses
/// out to a citizen, and operators need the raw id to correlate against
/// distribution telemetry. The companion DTO surfaces the same value as a
/// long to keep the contract honest about the exception.
/// </para>
/// <para>
/// <b>No PII.</b> <see cref="ErrorDescription"/> is bounded to 500 chars and
/// MUST be a sanitised, code-style description — never the row's payer
/// IDNO, name, or amount.
/// </para>
/// </remarks>
public sealed class TreasuryFeedImportRow : AuditableEntity, IExternalId
{
    /// <summary>FK pointer back to <c>TreasuryFeedImport.Id</c>.</summary>
    public long ImportId { get; set; }

    /// <summary>1-based position of the row inside the request file (excludes the header).</summary>
    public int RowOrdinal { get; set; }

    /// <summary>Per-row lifecycle status; defaults to <see cref="TreasuryFeedImportRowStatus.Pending"/>.</summary>
    public TreasuryFeedImportRowStatus Status { get; set; } = TreasuryFeedImportRowStatus.Pending;

    /// <summary>JSON snapshot of the parsed row inputs. Bounded to 4096 chars by the EF configuration.</summary>
    public string RawPayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// Internal id of the resulting <c>TreasuryPaymentReceipt</c> when the
    /// row resulted in an Imported or Updated outcome. Raw long — documented
    /// internal-ops exception to CLAUDE.md RULE 3.
    /// </summary>
    public long? MappedReceiptId { get; set; }

    /// <summary>Stable code categorising a row-level failure (e.g. <c>BAD_AMOUNT</c>). Null on success.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Short, PII-free description of the failure. Bounded to 500 chars. Null on success.</summary>
    public string? ErrorDescription { get; set; }

    /// <summary>UTC instant the importer finalised this row. Null while Pending.</summary>
    public DateTime? ProcessedAt { get; set; }
}
