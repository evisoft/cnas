namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// R0102 / TOR CF 14.02 — canonical outbound-message envelope shape required by
/// every CNAS event published onto MConnect Events. Pins the seven fields the
/// integration contract mandates so producers cannot accidentally omit one.
/// </summary>
/// <remarks>
/// <para>
/// The envelope is a thin DTO — it carries no business logic, only the metadata
/// fields the receiver needs to correlate, dedupe, and version the payload.
/// CloudEvents v1.0 attribute names map to envelope fields one-for-one:
/// <list type="bullet">
///   <item><see cref="MessageId"/> → CloudEvents <c>id</c>.</item>
///   <item><see cref="CorrelationId"/> → custom <c>correlationid</c> extension.</item>
///   <item><see cref="CausationId"/> → custom <c>causationid</c> extension.</item>
///   <item><see cref="Timestamp"/> → CloudEvents <c>time</c>.</item>
///   <item><see cref="Source"/> → CloudEvents <c>source</c>.</item>
///   <item><see cref="Schema"/> → CloudEvents <c>dataschema</c>
///     (event-type URN, e.g. <c>urn:cnas:event:application.submitted:v1</c>).</item>
///   <item><see cref="Payload"/> → CloudEvents <c>data</c> (raw JSON).</item>
/// </list>
/// </para>
/// <para>
/// <b>Causation chain.</b> <see cref="CausationId"/> is the <see cref="MessageId"/>
/// of the event that directly caused this one. Root-cause events whose origin is a
/// human request (not another event) fall back to a freshly-minted GUID so the
/// chain head is still uniquely identified.
/// </para>
/// </remarks>
/// <param name="MessageId">Unique id of this event. Reused across retries to make republishing idempotent.</param>
/// <param name="CorrelationId">
/// Correlation id linking this event to the originating user / request /
/// workflow. Stable across the entire causation chain so downstream
/// consumers can group all events belonging to one logical transaction.
/// </param>
/// <param name="CausationId">
/// <see cref="MessageId"/> of the directly-preceding event in the causation
/// chain. For root events without an in-system predecessor, callers SHOULD
/// supply a fresh GUID so the chain head is uniquely identified.
/// </param>
/// <param name="Timestamp">UTC instant the producer minted the envelope.</param>
/// <param name="Source">
/// CloudEvents <c>source</c> URI identifying the producer system, e.g.
/// <c>cnas-ps</c> or <c>urn:cnas:component:application-service</c>.
/// </param>
/// <param name="Schema">
/// Canonical event-type URN (e.g. <c>urn:cnas:event:application.submitted:v1</c>).
/// MUST be a stable string — version bumps go in the URN suffix, never by
/// reusing an existing URN for a new schema shape.
/// </param>
/// <param name="Payload">
/// Raw JSON payload of the event. The producer owns the schema referenced by
/// <see cref="Schema"/>; the receiver deserialises against the same URN.
/// </param>
public sealed record MConnectEnvelope(
    string MessageId,
    string CorrelationId,
    string CausationId,
    DateTime Timestamp,
    string Source,
    string Schema,
    string Payload);
