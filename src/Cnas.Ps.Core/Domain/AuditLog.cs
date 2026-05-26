namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Fișier log — audit record produced by SI PS in response to a business or security event.
/// TOR §2.3 #11, UC23 (Jurnalizez evenimente), SEC 038–048.
/// </summary>
/// <remarks>
/// <para>
/// Each record carries the minimum fields demanded by SEC 042: timestamp, subject (actor),
/// object (target), event, and source IP. Records do not contain PII (SEC 044). Critical
/// events are mirrored to MLog by the application layer (SEC 056) — this entity is the
/// local journal.
/// </para>
/// <para>
/// R0194 / SEC 047 — every row participates in a SHA-256 hash chain so any retroactive
/// modification of a row (e.g. an attacker editing the DB after the fact to cover their
/// tracks) is detectable. <see cref="PrevHash"/> stores the previous row's
/// <see cref="RowHash"/> (or the literal <c>"GENESIS"</c> for the very first row);
/// <see cref="RowHash"/> stores the SHA-256 digest (lowercase hex) of this row's
/// canonical form chained from <see cref="PrevHash"/>. The canonical form and the
/// digest are computed by <c>AuditFlushProjector.ComputeRowHash</c> — the single
/// source of truth shared by the drainer and the archive-replay job so the chain
/// cannot drift between the live write path and the failure-replay path. Chain
/// integrity is validated by <c>IAuditChainVerifier</c>.
/// </para>
/// </remarks>
public sealed class AuditLog : AuditableEntity, IExternalId
{
    /// <summary>UTC timestamp of the event (separate from <see cref="AuditableEntity.CreatedAtUtc"/>).</summary>
    public DateTime EventAtUtc { get; set; }

    /// <summary>Severity classification.</summary>
    public AuditSeverity Severity { get; set; }

    /// <summary>Stable event code (e.g. <c>USER.LOGIN.SUCCESS</c>, <c>DOSSIER.CLOSED</c>).</summary>
    public required string EventCode { get; set; }

    /// <summary>Actor identifier (user id or system id) — never a name with PII.</summary>
    public required string ActorId { get; set; }

    /// <summary>Affected entity kind (e.g. <c>Application</c>, <c>Dossier</c>, <c>Document</c>).</summary>
    public string? TargetEntity { get; set; }

    /// <summary>Affected entity primary key.</summary>
    public long? TargetEntityId { get; set; }

    /// <summary>Source IP (textual, supports IPv4 + IPv6).</summary>
    public string? SourceIp { get; set; }

    /// <summary>Correlation id of the HTTP request or background job.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Structured details — JSON document. Schema is event-specific. Confidential data
    /// must never be written here (SEC 044); use the redacted projection instead.
    /// </summary>
    public string DetailsJson { get; set; } = "{}";

    /// <summary>
    /// SHA-256 hash of the previous row in the chain, lowercase hex (64 chars). The
    /// very first row in an empty table uses the literal anchor <c>"GENESIS"</c>;
    /// every subsequent row stores the previous row's <see cref="RowHash"/>. See
    /// <c>AuditFlushProjector.ComputeRowHash</c> for the canonical form. R0194 / SEC 047.
    /// </summary>
    public required string PrevHash { get; set; }

    /// <summary>
    /// SHA-256 hash of this row's canonical form chained from <see cref="PrevHash"/>,
    /// lowercase hex (64 chars). Computed by <c>AuditFlushProjector.ComputeRowHash</c> —
    /// the single computation site shared by the drainer and the archive-replay job.
    /// R0194 / SEC 047 tamper-evidence anchor.
    /// </summary>
    public required string RowHash { get; set; }
}
