namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0191 / TOR SEC 050 / TOR ARH 028 — application-level history snapshot row.
/// One row per tracked-entity mutation; produced by the universal
/// <c>HistoryTrackingInterceptor</c> for entities marked
/// <c>Cnas.Ps.Core.Audit.IHistoryTracked</c>. Drives point-in-time queries
/// ("show me this row as of 2025-01-15") and admin timeline UX.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a single shared table.</b> Per-entity history tables would explode
/// the schema (one extra table per tracked entity, each with its own migration
/// and indices) and tightly couple the projection to the live shape — every
/// column addition would require a parallel migration on the shadow table.
/// Using a single <c>{EntityType, EntityId, ChangedAtUtc, Operation,
/// PayloadJson}</c> row instead trades a column-projection denormalisation
/// for schema stability. Indexes on <see cref="EntityType"/> and
/// <see cref="EntityId"/> keep the timeline query O(log n).
/// </para>
/// <para>
/// <b>Payload contract.</b> <see cref="PayloadJson"/> is a JSON object whose
/// keys are property names and whose values are JSON-encoded representations
/// of the column values AS OF the moment of the snapshot (post-image for
/// Insert / Update, pre-image for Delete). PII columns are excluded by the
/// <c>HistoryTrackingInterceptor</c> (same exclusion list the
/// <c>AuditingInterceptor</c> uses) and the resulting payload is also passed
/// through <c>PiiRedactor</c> as defence in depth. The payload is size-capped
/// to <c>MaxPayloadChars</c> to bound storage cost.
/// </para>
/// <para>
/// <b>Immutability.</b> Rows are append-only. There is no UPDATE / DELETE
/// path on this table — the live business write that mutates the source
/// entity is paired with an INSERT here. <see cref="AuditableEntity.IsActive"/>
/// is therefore always <c>true</c>; the inherited soft-delete flag is
/// retained only because every entity in <c>Cnas.Ps.Core.Domain</c> derives
/// from <see cref="AuditableEntity"/>.
/// </para>
/// </remarks>
public sealed class EntityHistoryRow : AuditableEntity, IExternalId
{
    /// <summary>
    /// CLR type name of the tracked entity (e.g. <c>UserProfile</c>,
    /// <c>Claim</c>). Stable across deployments — the admin timeline query
    /// matches on this exact case-sensitive string. Indexed for the
    /// <c>WHERE EntityType = X AND EntityId = Y</c> lookup pattern.
    /// </summary>
    public required string EntityType { get; set; }

    /// <summary>
    /// Surrogate primary-key value of the tracked entity at the moment of
    /// the mutation. <c>long</c>-typed because all <see cref="AuditableEntity"/>
    /// rows carry a <see cref="AuditableEntity.Id"/> of that shape; entities
    /// without a long-shaped key fall outside the tracked set by construction.
    /// </summary>
    public long EntityId { get; set; }

    /// <summary>
    /// UTC instant at which the source entity was mutated. Sourced from
    /// <c>ICnasTimeProvider.UtcNow</c> via the interceptor — never from
    /// <c>DateTime.UtcNow</c> directly. Indexed-secondary on
    /// <c>(EntityType, EntityId, ChangedAtUtc)</c> drives the timeline query.
    /// </summary>
    public DateTime ChangedAtUtc { get; set; }

    /// <summary>
    /// Single-character operation kind: <c>I</c> = Insert, <c>U</c> = Update,
    /// <c>D</c> = Delete. Stable values — change to multi-character codes
    /// would break the in-place migration backfill story.
    /// </summary>
    public required string Operation { get; set; }

    /// <summary>
    /// JSON document carrying the snapshot. See remarks on
    /// <see cref="EntityHistoryRow"/> for the column-exclusion + PII-redaction
    /// contract. Size-capped to keep storage cost bounded.
    /// </summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>
    /// Maximum number of characters retained in <see cref="PayloadJson"/>.
    /// Mirrors the <c>AuditingInterceptor.MaxPayloadChars</c> budget so the
    /// two projections share a consistent storage ceiling.
    /// </summary>
    public const int MaxPayloadChars = 4096;

    /// <summary>
    /// Actor SQID (or "system" for background-job writes) that performed the
    /// mutation. Null for early-bootstrap inserts that pre-date the
    /// <c>ICallerContext</c> being populated.
    /// </summary>
    public string? ActorSqid { get; set; }
}
