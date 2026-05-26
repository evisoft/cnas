using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.MessageBus;

/// <summary>
/// R0102 / TOR CF 14.02 — single producer-site seam that materialises the
/// canonical <see cref="MConnectEnvelope"/> for an outbound CloudEvent and
/// hands the populated envelope plus the CloudEvents adapter to the publisher.
/// </summary>
/// <remarks>
/// <para>
/// <b>Field mapping.</b> The seven envelope fields map one-for-one onto the
/// CloudEvents v1.0 attributes published by
/// <c>MConnectEventsProducer</c>:
/// <list type="bullet">
///   <item><see cref="MConnectEnvelope.MessageId"/> → CloudEvents <c>id</c>.</item>
///   <item><see cref="MConnectEnvelope.CorrelationId"/> → custom <c>correlationid</c> extension.</item>
///   <item><see cref="MConnectEnvelope.CausationId"/> → custom <c>causationid</c> extension.</item>
///   <item><see cref="MConnectEnvelope.Timestamp"/> → CloudEvents <c>time</c>.</item>
///   <item><see cref="MConnectEnvelope.Source"/> → CloudEvents <c>source</c>.</item>
///   <item><see cref="MConnectEnvelope.Schema"/> → CloudEvents <c>dataschema</c> URN.</item>
///   <item><see cref="MConnectEnvelope.Payload"/> → CloudEvents <c>data</c>.</item>
/// </list>
/// </para>
/// <para>
/// Implementations populate sensible defaults when the caller supplies
/// <c>null</c>: MessageId falls back to a fresh GUID, CorrelationId to the
/// ambient <see cref="ICallerContext.CorrelationId"/> or a fresh GUID, and
/// CausationId to a fresh GUID when no preceding event id is supplied (root
/// event).
/// </para>
/// </remarks>
public interface IMConnectEnvelopeFactory
{
    /// <summary>
    /// Builds a fully-populated canonical envelope from the supplied caller
    /// data and the originating payload.
    /// </summary>
    /// <param name="schemaUrn">
    /// Event-type URN (e.g. <c>urn:cnas:event:application.submitted:v1</c>).
    /// MUST be a stable string — version bumps go in the URN suffix.
    /// </param>
    /// <param name="payloadJson">Raw JSON payload of the event.</param>
    /// <param name="messageId">Optional explicit message id; defaults to a fresh GUID.</param>
    /// <param name="correlationId">Optional explicit correlation id; defaults to ambient or fresh GUID.</param>
    /// <param name="causationId">Optional explicit causation id; defaults to fresh GUID.</param>
    /// <param name="source">
    /// Optional explicit source URI; defaults to <c>cnas-ps</c> (the canonical
    /// producer identifier).
    /// </param>
    /// <returns>The populated envelope.</returns>
    MConnectEnvelope Build(
        string schemaUrn,
        string payloadJson,
        string? messageId = null,
        string? correlationId = null,
        string? causationId = null,
        string? source = null);

    /// <summary>
    /// Adapts a canonical <see cref="MConnectEnvelope"/> into the wire
    /// <see cref="CloudEventEnvelope"/> the producer's HTTP adapter posts to
    /// MConnect Events. The mapping is mechanical — every envelope field maps
    /// to a CloudEvents attribute or extension as documented on the interface.
    /// </summary>
    /// <param name="envelope">Source envelope.</param>
    /// <returns>CloudEvents v1.0 envelope ready for publishing.</returns>
    CloudEventEnvelope ToCloudEvent(MConnectEnvelope envelope);
}
