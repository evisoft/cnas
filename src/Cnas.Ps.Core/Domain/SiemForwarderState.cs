namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Persistent checkpoint for the SIEM (Security Information and Event Management) forwarder
/// background job (R0190 / SEC 049). The forwarder polls <see cref="AuditLog"/> rows whose
/// <see cref="AuditableEntity.Id"/> is greater than <see cref="LastForwardedAuditId"/>,
/// emits them as ArcSight CEF (Common Event Format) over syslog, and then advances the
/// checkpoint so the next iteration resumes exactly where the previous one stopped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton-via-known-key pattern.</b> The table is intentionally NOT keyed on the
/// surrogate <see cref="AuditableEntity.Id"/> — instead, every environment carries a
/// single row whose <see cref="Key"/> column equals the literal <c>"default"</c>.
/// Migrations seed that row idempotently with <c>ON CONFLICT (Key) DO NOTHING</c>; the
/// unique index on <see cref="Key"/> protects against accidental second-row inserts.
/// Holding the checkpoint in a row (rather than in an in-memory job field) is the only
/// way to guarantee crash-safety: a process restart between two polling cycles must NOT
/// cause already-forwarded rows to be re-emitted to the SIEM, otherwise downstream
/// alerting rules will fire duplicates.
/// </para>
/// <para>
/// <b>Why not <see cref="IExternalId"/>.</b> The checkpoint is purely an internal
/// implementation detail of the forwarder — it never surfaces in any output DTO, REST
/// route, or webhook payload. Marking it as an external-id-bearing entity would falsely
/// imply its surrogate id is part of the public contract, which it is not. See
/// <see cref="IExternalId"/> remarks for the contract.
/// </para>
/// <para>
/// <b>Failure handling contract.</b> The forwarder advances
/// <see cref="LastForwardedAuditId"/> ONLY after the syslog transport call returns
/// success. On transport failure (UDP send exception / TCP refused) the checkpoint
/// stays at its previous value so the next iteration retries the same range — at-least-
/// once delivery with the trade-off of duplicate SIEM ingestion in rare retry-after-
/// partial-success windows. UDP is fire-and-forget; "transport success" therefore means
/// "<see cref="System.Net.Sockets.UdpClient.SendAsync(byte[], int, string, int)"/> did
/// not throw" — a packet lost in flight is, by design, indistinguishable from one
/// delivered.
/// </para>
/// <para>
/// <b>Soft-delete.</b> Inherits the standard <see cref="AuditableEntity.IsActive"/>
/// soft-delete marker. Operators MAY soft-delete the row to disable forwarding without
/// touching configuration — the job's query is gated on <c>IsActive == true</c>, so an
/// inactive row makes the next iteration log a warning and return. Re-activating restores
/// forwarding from the checkpoint stored on the row.
/// </para>
/// </remarks>
public sealed class SiemForwarderState : AuditableEntity
{
    /// <summary>
    /// Stable singleton-row key. The forwarder reads and writes exclusively the row whose
    /// <see cref="Key"/> equals <c>"default"</c>; the migration seeds that row and the
    /// unique index on this column prevents accidental duplicates. Capped at 32 characters
    /// at the EF mapping layer to leave headroom for a future per-tenant variant
    /// (<c>"tenant-X"</c>, ...) without touching the schema again.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// Highest <see cref="AuditLog"/> primary key that has been successfully formatted as
    /// CEF and written to the configured syslog endpoint. The forwarder's next iteration
    /// queries <c>AuditLogs.Where(a =&gt; a.Id &gt; LastForwardedAuditId)</c> — strictly
    /// greater-than so a row is never re-emitted. Seeded to <c>0</c> by the migration so
    /// the first iteration starts from the bottom of the table.
    /// </summary>
    public long LastForwardedAuditId { get; set; }

    /// <summary>
    /// UTC instant at which the most recent forwarding cycle successfully advanced
    /// <see cref="LastForwardedAuditId"/>. <c>null</c> until the first successful
    /// forward; useful for operator dashboards charting "freshness" of the SIEM feed.
    /// Not used by the forwarder's decision logic — the checkpoint id alone is the
    /// authoritative resume anchor.
    /// </summary>
    public DateTime? LastForwardedAtUtc { get; set; }
}
