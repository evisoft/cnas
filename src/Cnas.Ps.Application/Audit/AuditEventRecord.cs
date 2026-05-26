using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// Public contract record describing a single queued audit event, captured at the
/// point the caller invoked the audit facade. Used both by the in-memory write
/// queue and by the durable archive that retains batches whose primary flush
/// failed (R0186 / R0188).
/// </summary>
/// <remarks>
/// <para>
/// All fields mirror the public <c>IAuditService.RecordAsync</c> parameter list.
/// <see cref="EventAtUtc"/> is taken from the caller's clock at enqueue time so
/// the eventual <c>AuditLog</c> row carries the true business-event instant —
/// not the (potentially much later) flush instant.
/// </para>
/// <para>
/// <see cref="DetailsJson"/> has already been redacted by
/// <see cref="PiiRedactor"/> at the producer boundary; the archive simply persists
/// what it receives and never relaxes that invariant.
/// </para>
/// <para>
/// Moved from the Infrastructure layer (previously <c>internal</c>) to the
/// Application layer in R0188 so the audit-archive abstraction
/// (<c>IAuditArchive</c>) can be expressed as a contract type without leaking
/// implementation concerns across the layer boundary.
/// </para>
/// </remarks>
/// <param name="EventCode">Stable event code (e.g. <c>USER.LOGIN.SUCCESS</c>).</param>
/// <param name="Severity">Severity classification.</param>
/// <param name="ActorId">Actor identifier — never a name with PII.</param>
/// <param name="TargetEntity">Affected entity kind (nullable).</param>
/// <param name="TargetEntityId">Affected entity primary key (nullable).</param>
/// <param name="DetailsJson">Redacted structured details — JSON document.</param>
/// <param name="SourceIp">Source IP (nullable).</param>
/// <param name="CorrelationId">Correlation id (nullable).</param>
/// <param name="EventAtUtc">UTC instant at which the business event occurred.</param>
public sealed record AuditEventRecord(
    string EventCode,
    AuditSeverity Severity,
    string ActorId,
    string? TargetEntity,
    long? TargetEntityId,
    string DetailsJson,
    string? SourceIp,
    string? CorrelationId,
    DateTime EventAtUtc)
{
    /// <summary>
    /// R0182 — optional coarse-grained module identifier (e.g. <c>Solicitant</c>,
    /// <c>Cerere</c>) used by the audit-policy resolver to filter candidate policies.
    /// Producers MAY leave this null, in which case the resolver matches policies
    /// regardless of their <c>Module</c> filter. Excluded from the SHA-256 hash-chain
    /// canonical form (see <c>AuditFlushProjector.ComputeRowHash</c>) so adding the
    /// field is a non-breaking change to the R0194 chain — existing rows continue to
    /// verify against the same recipe.
    /// </summary>
    public string? Module { get; init; }

    /// <summary>
    /// R0182 — optional finer-grained screen identifier (e.g. <c>Search</c>,
    /// <c>Detail</c>) paired with <see cref="Module"/> for policy resolution. Null
    /// bypasses the screen filter. Like <see cref="Module"/>, NOT included in the
    /// hash-chain recipe.
    /// </summary>
    public string? Screen { get; init; }

    /// <summary>
    /// R0182 — optional data-category tag (e.g. <c>PII</c>, <c>Financial</c>) used by
    /// the audit-policy resolver. Null bypasses the category filter. Like
    /// <see cref="Module"/> / <see cref="Screen"/>, NOT included in the hash-chain
    /// recipe.
    /// </summary>
    public string? DataCategory { get; init; }
}
