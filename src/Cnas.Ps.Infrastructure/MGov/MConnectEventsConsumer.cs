using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// MConnect Events consumer — a hosted background service that streams CloudEvents over
/// a WebSocket and dispatches each event to the registered <see cref="ICloudEventHandler"/>
/// instances.
/// </summary>
/// <remarks>
/// <para>
/// Wire protocol: connects to <c>wss://{BaseUrl}/ce/consume/ws</c> with sub-protocol
/// <c>cloudevents.json</c>. On open, sends a subscription frame
/// <c>{ "kind": "subscribe", "topics": [...] }</c>. Each subsequent frame is a
/// CloudEvents v1.0 JSON object.
/// </para>
/// <para>
/// Reconnect policy: on any disconnect (graceful or aborted), waits
/// <see cref="ReconnectDelay"/> and tries again until <c>stoppingToken</c> is cancelled.
/// Backoff is deliberately constant — the upstream is highly available and exponential
/// backoff would delay reconnect after transient network blips on the producer side.
/// </para>
/// <para>
/// Disabled mode: if <see cref="MGovOptions.MConnectEventsBaseUrl"/> is empty, the
/// consumer logs a single informational line and returns without entering the connect
/// loop. This keeps local-dev environments runnable without MEGA staging access.
/// </para>
/// <para>
/// Known gap: there is NO end-to-end integration test for this consumer because the
/// behaviour requires a real WebSocket server. The gap is intentional and tracked in
/// <c>docs/EGOV-INTEGRATION-GAP.md</c>; verification will happen in staging once
/// MEGA-issued certificates are available. The producer side IS unit-tested.
/// </para>
/// </remarks>
/// <param name="services">Root provider used to create per-event scopes (handlers may be scoped).</param>
/// <param name="options">MGov configuration snapshot.</param>
/// <param name="logger">Structured logger.</param>
/// <param name="clock">UTC clock — currently unused but reserved for backoff/jitter.</param>
public sealed class MConnectEventsConsumer(
    IServiceProvider services,
    IOptions<MGovOptions> options,
    ILogger<MConnectEventsConsumer> logger,
    ICnasTimeProvider clock) : BackgroundService
{
    /// <summary>WebSocket sub-protocol negotiated with MConnect Events.</summary>
    private const string SubProtocol = "cloudevents.json";

    /// <summary>Constant delay between failed connect attempts.</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>Maximum size of a single inbound frame we are willing to buffer (256 KiB).</summary>
    private const int MaxFrameBytes = 256 * 1024;

    /// <summary>Default topic subscription. Wildcards mirror the EGov integration guide.</summary>
    private static readonly IReadOnlyList<string> DefaultTopics =
    [
        "RSP.*",
        "SFS.*",
        "ECMND.*",
    ];

    private readonly IServiceProvider _services = services;
    private readonly MGovOptions _options = options.Value;
    private readonly ILogger<MConnectEventsConsumer> _logger = logger;
    private readonly ICnasTimeProvider _clock = clock;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.MConnectEventsBaseUrl))
        {
            _logger.LogInformation("MConnect Events disabled — base URL not configured. Consumer will not start.");
            return;
        }

        _logger.LogInformation("MConnect Events consumer starting; clock={ClockUtc}.", _clock.UtcNow);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — exit the loop without an error log.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MConnect Events consumer connection failed; reconnecting in {Delay}.", ReconnectDelay);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("MConnect Events consumer stopped.");
    }

    /// <summary>
    /// Runs a single connect-subscribe-receive lifecycle. Returns when the WebSocket
    /// closes or the cancellation token fires; never throws on a normal close.
    /// </summary>
    private async Task RunConnectionAsync(CancellationToken stoppingToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(SubProtocol);
#pragma warning disable CS0618 // MGovOptions.MConnectEventsBearer is [Obsolete] — kept for transitional back-compat. The WebSocket handshake will move to mTLS once the consumer's own primary handler is migrated; see docs/EGOV-INTEGRATION-GAP.md §"MConnect Events".
        if (!string.IsNullOrEmpty(_options.MConnectEventsBearer))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {_options.MConnectEventsBearer}");
        }
#pragma warning restore CS0618

        var wsUri = BuildWebSocketUri(_options.MConnectEventsBaseUrl);
        await socket.ConnectAsync(wsUri, stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("MConnect Events WebSocket connected to {Uri}.", wsUri);

        await SendSubscribeAsync(socket, DefaultTopics, stoppingToken).ConfigureAwait(false);

        await ReceiveLoopAsync(socket, stoppingToken).ConfigureAwait(false);

        if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Best-effort close; downstream might already be torn down.
            }
        }
    }

    /// <summary>
    /// Translates the HTTPS MConnect Events base URL to its WebSocket variant
    /// (<c>wss://</c>) and appends the consumer path. Handles both <c>http://</c> and
    /// <c>https://</c> input for parity with local-dev configurations.
    /// </summary>
    internal static Uri BuildWebSocketUri(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "wss://" + trimmed["https://".Length..];
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "ws://" + trimmed["http://".Length..];
        }
        return new Uri($"{trimmed}/ce/consume/ws");
    }

    /// <summary>
    /// Sends the JSON subscription frame. The shape is the one documented in the EGov
    /// integration guide: <c>{ "kind": "subscribe", "topics": [...] }</c>.
    /// </summary>
    private static async Task SendSubscribeAsync(
        ClientWebSocket socket,
        IReadOnlyList<string> topics,
        CancellationToken ct)
    {
        var frame = new JsonObject
        {
            ["kind"] = "subscribe",
            ["topics"] = new JsonArray(topics.Select(t => (JsonNode?)t).ToArray()),
        };
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Receives frames until the socket closes or cancellation fires. Each text frame is
    /// parsed as a CloudEvent and dispatched. Binary frames and oversize messages are
    /// dropped with a warning — we do not want a malformed producer to take the consumer
    /// down.
    /// </summary>
    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken stoppingToken)
    {
        var buffer = new byte[8192];
        using var assembler = new MemoryStream();

        while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
        {
            assembler.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, stoppingToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("MConnect Events WebSocket closed by server (status={Status}).",
                        result.CloseStatus);
                    return;
                }

                if (assembler.Length + result.Count > MaxFrameBytes)
                {
                    _logger.LogWarning("MConnect Events dropping oversized frame (> {Max} bytes).", MaxFrameBytes);
                    // Drain the rest of the frame so we don't desynchronize.
                    while (!result.EndOfMessage)
                    {
                        result = await socket.ReceiveAsync(buffer, stoppingToken).ConfigureAwait(false);
                    }
                    assembler.SetLength(0);
                    break;
                }

                assembler.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (assembler.Length == 0 || result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            await DispatchAsync(assembler.ToArray(), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses one CloudEvent and fans it out to every <see cref="ICloudEventHandler"/> that
    /// returns true from <see cref="ICloudEventHandler.CanHandle"/>. Handlers are resolved
    /// in a fresh DI scope per event so scoped handlers (e.g. ones with a DbContext) work.
    /// </summary>
    private async Task DispatchAsync(byte[] frame, CancellationToken ct)
    {
        CloudEventEnvelope envelope;
        try
        {
            envelope = ParseEnvelope(frame);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MConnect Events received malformed JSON; frame dropped.");
            return;
        }

        using var scope = _services.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<ICloudEventHandler>();
        foreach (var handler in handlers)
        {
            if (!handler.CanHandle(envelope.Type))
            {
                continue;
            }
            try
            {
                await handler.HandleAsync(envelope, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Individual handler failures must not break the consume loop or starve
                // sibling handlers — log and continue.
                _logger.LogWarning(ex,
                    "MConnect Events handler {Handler} failed for event type {Type}.",
                    handler.GetType().FullName, envelope.Type);
            }
        }
    }

    /// <summary>
    /// Parses a UTF-8 JSON frame into a <see cref="CloudEventEnvelope"/>. Reads CloudEvents
    /// v1.0 lower-case attribute names; missing required fields become empty strings so a
    /// half-broken event doesn't bring down the consumer.
    /// </summary>
    internal static CloudEventEnvelope ParseEnvelope(byte[] frame)
    {
        var node = JsonNode.Parse(frame) as JsonObject
            ?? throw new JsonException("CloudEvent frame is not a JSON object.");

        var id = node["id"]?.GetValue<string>() ?? string.Empty;
        var source = node["source"]?.GetValue<string>() ?? string.Empty;
        var type = node["type"]?.GetValue<string>() ?? string.Empty;
        var timeRaw = node["time"]?.GetValue<string>();
        // Fail-safe parse: a malformed `time` attribute used to throw FormatException
        // and tear down the WebSocket connection. We now treat unparseable values as
        // "absent" (default DateTime) so the consumer continues processing. Parsing
        // failures here are not a security boundary — the producer is out of contract
        // and the downstream handlers can use the rest of the envelope.
        // RoundtripKind cannot be combined with AdjustToUniversal (ArgumentException at runtime).
        // Use AssumeUniversal|AdjustToUniversal so naked timestamps (no offset) are treated as
        // UTC and offset-bearing timestamps are normalised to UTC.
        var time = string.IsNullOrEmpty(timeRaw)
            || !DateTime.TryParse(
                timeRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            ? default
            : parsed;
        var partitionKey = node["partitionkey"]?.GetValue<string>();
        var dataContentType = node["datacontenttype"]?.GetValue<string>() ?? "application/json";
        var dataJson = node["data"] switch
        {
            null => string.Empty,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            var n => n.ToJsonString(),
        };

        return new CloudEventEnvelope(id, source, type, time, partitionKey, dataContentType, dataJson);
    }
}

/// <summary>
/// Default catch-all <see cref="ICloudEventHandler"/>. Accepts every event type and:
/// (1) consults the R0103 <see cref="Cnas.Ps.Application.MessageBus.IIntegrationEventDeduper"/>
/// to enforce inbound MessageId idempotency, short-circuiting duplicates; (2) on the
/// first observation of an envelope, logs the receipt (envelope metadata only, never
/// the data payload).
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency contract.</b> The first action on every received envelope is
/// <see cref="Cnas.Ps.Application.MessageBus.IIntegrationEventDeduper.TryClaimAsync"/>.
/// When the MessageId has already been processed the handler emits the
/// <see cref="Cnas.Ps.Infrastructure.Observability.CnasMeter.IntegrationEventDeduped"/>
/// counter (tagged with the envelope's source + type), logs an Info-level line,
/// and returns successfully without invoking any downstream logging.
/// </para>
/// <para>
/// <b>Scoped lifetime.</b> Because the deduper holds a per-request DbContext, this
/// handler is registered as Scoped in DI; the
/// <see cref="MConnectEventsConsumer"/> creates a fresh scope per inbound frame
/// so the lifetime is safe.
/// </para>
/// </remarks>
public sealed class LoggingCloudEventHandler : ICloudEventHandler
{
    /// <summary>Structured logger.</summary>
    private readonly ILogger<LoggingCloudEventHandler> _logger;

    /// <summary>R0103 inbound idempotency ledger.</summary>
    private readonly Cnas.Ps.Application.MessageBus.IIntegrationEventDeduper _deduper;

    /// <summary>
    /// Constructs the handler with its scoped collaborators.
    /// </summary>
    /// <param name="logger">Structured logger.</param>
    /// <param name="deduper">R0103 inbound idempotency ledger.</param>
    public LoggingCloudEventHandler(
        ILogger<LoggingCloudEventHandler> logger,
        Cnas.Ps.Application.MessageBus.IIntegrationEventDeduper deduper)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(deduper);
        _logger = logger;
        _deduper = deduper;
    }

    /// <inheritdoc />
    public bool CanHandle(string eventType) => true;

    /// <inheritdoc />
    public async Task HandleAsync(CloudEventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // R0103 — claim the MessageId atomically before doing any downstream work.
        // If the deduper's validator rejects the inputs (empty id / source / type) we
        // fall through to the logging-only path — the producer is out of contract
        // and we don't want to silently drop the trace. Real production envelopes
        // always have these fields populated by the MConnect Events transport.
        var claim = await _deduper.TryClaimAsync(envelope.Id, envelope.Source, envelope.Type, ct)
            .ConfigureAwait(false);
        if (claim.IsSuccess && claim.Value.AlreadyProcessed)
        {
            // R0103 — duplicate detected. Emit the dedup metric and short-circuit.
            Cnas.Ps.Infrastructure.Observability.CnasMeter.IntegrationEventDeduped.Add(
                1,
                new System.Collections.Generic.KeyValuePair<string, object?>("source", envelope.Source),
                new System.Collections.Generic.KeyValuePair<string, object?>("type", envelope.Type));
            _logger.LogInformation(
                "MConnect Events deduped id={Id} type={Type} source={Source} (already processed at {EarlierAt}).",
                envelope.Id, envelope.Type, envelope.Source, claim.Value.EarlierProcessedAtUtc);
            return;
        }

        // First observation OR validation failure — proceed with the logging path.
        // Never log the data payload — it may carry PII. The size + type/source/id are
        // enough to debug routing without leaking content.
        _logger.LogInformation(
            "MConnect Events received id={Id} type={Type} source={Source} dataBytes={Bytes}.",
            envelope.Id, envelope.Type, envelope.Source,
            envelope.DataJson is null ? 0 : Encoding.UTF8.GetByteCount(envelope.DataJson));

        if (claim.IsSuccess && !claim.Value.AlreadyProcessed)
        {
            Cnas.Ps.Infrastructure.Observability.CnasMeter.IntegrationEventAccepted.Add(
                1,
                new System.Collections.Generic.KeyValuePair<string, object?>("source", envelope.Source),
                new System.Collections.Generic.KeyValuePair<string, object?>("type", envelope.Type));
        }
    }
}
