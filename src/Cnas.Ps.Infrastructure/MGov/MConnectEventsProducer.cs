using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// HTTP adapter for the MConnect Events producer surface. Publishes CloudEvents v1.0
/// envelopes either one-at-a-time (<c>POST /ce/produce/event</c>) or in batches
/// (<c>POST /ce/produce/events</c>). The on-wire body uses the structured CloudEvents
/// JSON encoding so the receiver can deserialize without any out-of-band schema.
/// </summary>
/// <remarks>
/// <para>
/// The producer is idempotent on <see cref="CloudEventEnvelope.Id"/>: if MConnect Events
/// receives the same id twice it deduplicates downstream, so retrying a failed publish is
/// always safe — callers can drive retries from Quartz without dedupe state of their own.
/// </para>
/// <para>
/// Authentication is currently <c>Bearer &lt;token&gt;</c> matching the rest of the MGov
/// adapter layer. The real EGov spec mandates mTLS with an X.509 client certificate;
/// that is captured as a known gap in <c>docs/EGOV-INTEGRATION-GAP.md</c> and will land in
/// the universal mTLS refactor (one delegating handler per service).
/// </para>
/// <para>
/// PII safety: the request body is NEVER logged. Failures log only the upstream status,
/// the correlation id, and the exception type — never the data payload or the envelope id.
/// </para>
/// </remarks>
/// <param name="httpClient">Injected typed HTTP client. Timeout configured at registration.</param>
/// <param name="options">MGov configuration snapshot (base URL + bearer).</param>
/// <param name="logger">Structured logger.</param>
/// <param name="clock">UTC clock for the <c>X-Request-Date</c> header (CLAUDE.md UTC-Everywhere).</param>
public sealed class MConnectEventsProducer(
    HttpClient httpClient,
    IOptions<MGovOptions> options,
    ILogger<MConnectEventsProducer> logger,
    ICnasTimeProvider clock) : IMConnectEventsProducer
{
    /// <summary>Media type for a single structured CloudEvent (RFC 9251 §3.1).</summary>
    private const string SingleEventMediaType = "application/cloudevents+json";

    /// <summary>Media type for a CloudEvents batch (RFC 9251 §3.2).</summary>
    private const string BatchMediaType = "application/cloudevents-batch+json";

    /// <summary>UTF-8 charset advertised in the <c>Content-Type</c> header.</summary>
    private const string Charset = "utf-8";

    private readonly HttpClient _httpClient = httpClient;
    private readonly MGovOptions _options = options.Value;
    private readonly ILogger<MConnectEventsProducer> _logger = logger;
    private readonly ICnasTimeProvider _clock = clock;

    /// <inheritdoc />
    public async Task<Result> PublishAsync(CloudEventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(_options.MConnectEventsBaseUrl))
        {
            _logger.LogWarning("MConnect Events called without configured base URL — returning INTERNAL_ERROR.");
            return Result.Failure(ErrorCodes.Internal, "MConnect Events base URL not configured.");
        }

        // Build the canonical wire payload — a JsonObject so the `data` field is inlined as a
        // raw JSON value rather than a doubly-encoded string. Two envelopes with the same id
        // produce the same canonical bytes, hence the same X-Correlation-Id, hence the same
        // upstream dedup key.
        var canonical = SerializeSingle(envelope);
        var correlationId = MGovHttp.DeriveCorrelationId(canonical);

        return await PostAsync(
            uri: $"{_options.MConnectEventsBaseUrl.TrimEnd('/')}/ce/produce/event",
            body: canonical,
            mediaType: SingleEventMediaType,
            correlationId: correlationId,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> PublishBatchAsync(IReadOnlyList<CloudEventEnvelope> envelopes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        if (string.IsNullOrWhiteSpace(_options.MConnectEventsBaseUrl))
        {
            _logger.LogWarning("MConnect Events called without configured base URL — returning INTERNAL_ERROR.");
            return Result.Failure(ErrorCodes.Internal, "MConnect Events base URL not configured.");
        }

        // Empty batch is a no-op rather than an error: lets callers pass a pre-filtered
        // collection without an explicit guard at every call site.
        if (envelopes.Count == 0)
        {
            return Result.Success();
        }

        var canonical = SerializeBatch(envelopes);
        var correlationId = MGovHttp.DeriveCorrelationId(canonical);

        return await PostAsync(
            uri: $"{_options.MConnectEventsBaseUrl.TrimEnd('/')}/ce/produce/events",
            body: canonical,
            mediaType: BatchMediaType,
            correlationId: correlationId,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the CloudEvents v1.0 JSON object for a single envelope. The <c>data</c>
    /// member is parsed from <see cref="CloudEventEnvelope.DataJson"/> so the receiver
    /// sees the original JSON shape, not a quoted string. Falsy payloads (empty,
    /// whitespace) are coerced to <c>null</c> so we still emit a well-formed envelope.
    /// </summary>
    private static string SerializeSingle(CloudEventEnvelope envelope)
    {
        var node = ToCloudEventNode(envelope);
        return node.ToJsonString();
    }

    /// <summary>Builds the JSON array body for a batch publish.</summary>
    private static string SerializeBatch(IReadOnlyList<CloudEventEnvelope> envelopes)
    {
        var array = new JsonArray();
        foreach (var env in envelopes)
        {
            array.Add(ToCloudEventNode(env));
        }
        return array.ToJsonString();
    }

    /// <summary>
    /// Converts one envelope to its CloudEvents v1.0 JSON representation. Attribute
    /// names are the lower-case keys mandated by the spec (<c>specversion</c>, not
    /// <c>SpecVersion</c>).
    /// </summary>
    private static JsonObject ToCloudEventNode(CloudEventEnvelope envelope)
    {
        var node = new JsonObject
        {
            ["specversion"] = envelope.SpecVersion,
            ["id"] = envelope.Id,
            ["source"] = envelope.Source,
            ["type"] = envelope.Type,
            // ISO-8601 round-trip format ("O") keeps sub-second precision — required by §3.4.7.
            ["time"] = envelope.TimeUtc.ToString("O", CultureInfo.InvariantCulture),
            ["datacontenttype"] = envelope.DataContentType,
        };

        if (!string.IsNullOrEmpty(envelope.PartitionKey))
        {
            // CloudEvents partitioning extension (lowercase attribute name per the spec).
            node["partitionkey"] = envelope.PartitionKey;
        }

        // Parse the data payload back into a JsonNode so it is emitted as a raw JSON value.
        // If parsing fails (caller passed non-JSON), emit it as a quoted string instead so
        // we still produce a valid envelope rather than throwing inside the producer.
        if (!string.IsNullOrWhiteSpace(envelope.DataJson))
        {
            try
            {
                node["data"] = JsonNode.Parse(envelope.DataJson);
            }
            catch (JsonException)
            {
                node["data"] = envelope.DataJson;
            }
        }
        else
        {
            node["data"] = null;
        }

        return node;
    }

    /// <summary>
    /// Executes the HTTP POST with the supplied body and structured CloudEvents content
    /// type. All transport-level failure modes (5xx, 4xx, transport, timeout, JSON
    /// deserialization) collapse to <see cref="ErrorCodes.MConnectFailed"/>.
    /// </summary>
    private async Task<Result> PostAsync(
        string uri,
        string body,
        string mediaType,
        string correlationId,
        CancellationToken ct)
    {
        try
        {
            // StringContent's MediaTypeHeaderValue automatically appends "; charset=utf-8"
            // when constructed from an Encoding, matching the CloudEvents spec example.
            using var content = new StringContent(body, Encoding.UTF8, mediaType);
            content.Headers.ContentType!.CharSet = Charset;

            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
#pragma warning disable CS0618 // MGovOptions.MConnectEventsBearer is [Obsolete] — kept for transitional back-compat; mTLS attaches the cert via the primary handler.
            MGovHttp.Decorate(request, _options.MConnectEventsBearer, correlationId, _clock);
#pragma warning restore CS0618

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "MConnect Events publish to {Uri} failed with status {Status} (correlation {Correlation}).",
                    uri, (int)response.StatusCode, correlationId);
                return Result.Failure(ErrorCodes.MConnectFailed, "Upstream MConnect Events call failed.");
            }

            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MConnect Events transport failure for {Uri} (correlation {Correlation}).",
                uri, correlationId);
            return Result.Failure(ErrorCodes.MConnectFailed, "Upstream MConnect Events call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MConnect Events timed out for {Uri} (correlation {Correlation}).",
                uri, correlationId);
            return Result.Failure(ErrorCodes.MConnectFailed, "Upstream MConnect Events call failed.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MConnect Events JSON failure for {Uri} (correlation {Correlation}).",
                uri, correlationId);
            return Result.Failure(ErrorCodes.MConnectFailed, "Upstream MConnect Events call failed.");
        }
    }
}
