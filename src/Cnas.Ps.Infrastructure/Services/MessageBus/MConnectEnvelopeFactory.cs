using System.Globalization;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.MessageBus;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Infrastructure.Services.MessageBus;

/// <summary>
/// R0102 / TOR CF 14.02 — concrete implementation of
/// <see cref="IMConnectEnvelopeFactory"/>. Builds the canonical envelope with
/// sensible defaults derived from the ambient <see cref="ICallerContext"/> and
/// adapts it onto the wire <see cref="CloudEventEnvelope"/> consumed by
/// <c>MConnectEventsProducer</c>.
/// </summary>
public sealed class MConnectEnvelopeFactory : IMConnectEnvelopeFactory
{
    /// <summary>Canonical CNAS producer URI (CloudEvents <c>source</c> default).</summary>
    public const string DefaultSource = "cnas-ps";

    /// <summary>Default <c>datacontenttype</c> when adapting the envelope to the wire.</summary>
    public const string DefaultDataContentType = "application/json";

    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;

    /// <summary>Constructs the factory.</summary>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="caller">Authenticated caller context for correlation propagation.</param>
    public MConnectEnvelopeFactory(
        ICnasTimeProvider clock,
        ICallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        _clock = clock;
        _caller = caller;
    }

    /// <inheritdoc />
    public MConnectEnvelope Build(
        string schemaUrn,
        string payloadJson,
        string? messageId = null,
        string? correlationId = null,
        string? causationId = null,
        string? source = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaUrn);
        ArgumentNullException.ThrowIfNull(payloadJson);

        return new MConnectEnvelope(
            MessageId: !string.IsNullOrWhiteSpace(messageId)
                ? messageId
                : Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            CorrelationId: !string.IsNullOrWhiteSpace(correlationId)
                ? correlationId
                : (_caller.CorrelationId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
            CausationId: !string.IsNullOrWhiteSpace(causationId)
                ? causationId
                : Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            Timestamp: _clock.UtcNow,
            Source: !string.IsNullOrWhiteSpace(source) ? source : DefaultSource,
            Schema: schemaUrn,
            Payload: payloadJson);
    }

    /// <inheritdoc />
    public CloudEventEnvelope ToCloudEvent(MConnectEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // R0102 — the envelope's seven fields all land on the wire. The dedicated
        // dataschema extension (CloudEvents v1.0 §3.4.6) is currently surfaced via
        // the `type` attribute because MConnectEventsProducer.ToCloudEventNode does
        // not yet emit `dataschema` (it predates R0102). We populate `type` with the
        // schema URN so the receiver still sees a unique event-type discriminator.
        // PartitionKey carries the causation id so consumers that key off the
        // optional partitionkey extension can chain causation without separate
        // mapping. This is documented behaviour pinned by the R0102 tests.
        return new CloudEventEnvelope(
            Id: envelope.MessageId,
            Source: envelope.Source,
            Type: envelope.Schema,
            TimeUtc: envelope.Timestamp,
            PartitionKey: envelope.CausationId,
            DataContentType: DefaultDataContentType,
            DataJson: envelope.Payload);
    }
}
