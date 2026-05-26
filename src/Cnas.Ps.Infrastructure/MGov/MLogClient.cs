using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// HTTP adapter for MLog — the central government journaling service. Critical business
/// events are mirrored here in parallel with the local audit log (SEC 056). Failures are
/// surfaced via <see cref="Result"/> but the caller decides whether to abort the local
/// transaction; for most events the local <c>AuditService</c> is the system of record and
/// an MLog hiccup must not block citizens.
/// </summary>
/// <param name="httpClient">Injected typed-client.</param>
/// <param name="options">MGov configuration snapshot.</param>
/// <param name="logger">Structured logger; never logs the entry details (may contain PII).</param>
/// <param name="clock">UTC clock for the <c>X-Request-Date</c> header.</param>
public sealed class MLogClient(
    HttpClient httpClient,
    IOptions<MGovOptions> options,
    ILogger<MLogClient> logger,
    ICnasTimeProvider clock) : IMLogClient
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly MGovOptions _options = options.Value;
    private readonly ILogger<MLogClient> _logger = logger;
    private readonly ICnasTimeProvider _clock = clock;

    /// <summary>Request body posted to <c>/api/v1/journal/append</c>.</summary>
    private sealed record AppendBody(
        string EventCode,
        string ActorId,
        string? TargetEntity,
        long? TargetEntityId,
        string DetailsJson);

    /// <summary>Response body returned by <c>/api/v1/journal/append</c>.</summary>
    private sealed record AppendResponse(string LogId);

    /// <inheritdoc />
    public async Task<Result> AppendAsync(MLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(_options.MLogBaseUrl))
        {
            _logger.LogWarning("MLog called without configured base URL — returning INTERNAL_ERROR.");
            return Result.Failure(ErrorCodes.Internal, "MLog base URL is not configured.");
        }
        if (string.IsNullOrWhiteSpace(entry.EventCode))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Event code is required.");
        }

        var body = new AppendBody(entry.EventCode, entry.ActorId, entry.TargetEntity, entry.TargetEntityId, entry.DetailsJson);
        var canonical = JsonSerializer.Serialize(body, MGovHttp.JsonOptions);
        var correlationId = MGovHttp.DeriveCorrelationId(canonical);

        try
        {
            using var http = new HttpRequestMessage(HttpMethod.Post, $"{_options.MLogBaseUrl.TrimEnd('/')}/api/v1/journal/append")
            {
                Content = JsonContent.Create(body, options: MGovHttp.JsonOptions),
            };
            MGovHttp.Decorate(http, _options.MLogBearer, correlationId, _clock);

            using var response = await _httpClient.SendAsync(http, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MLog append failed for {EventCode} with status {Status} (correlation {Correlation}).",
                    entry.EventCode, (int)response.StatusCode, correlationId);
                return Result.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.");
            }

            // Drain the response body to surface malformed JSON as a failure rather than
            // a silent success — even though the caller doesn't care about LogId, the round
            // trip should be a clean handshake.
            var parsed = await response.Content
                .ReadFromJsonAsync<AppendResponse>(MGovHttp.JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            _ = parsed;
            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MLog transport failure for {EventCode} (correlation {Correlation}).",
                entry.EventCode, correlationId);
            return Result.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MLog timed out for {EventCode} (correlation {Correlation}).",
                entry.EventCode, correlationId);
            return Result.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MLog returned malformed JSON for {EventCode} (correlation {Correlation}).",
                entry.EventCode, correlationId);
            return Result.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.");
        }
    }

    /// <summary>
    /// JSON serializer options for the canonical 16-field event shape: snake_case naming
    /// + string-as-name enum encoding (matches MEGA MLog wire spec).
    /// </summary>
    private static readonly JsonSerializerOptions s_canonicalJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <inheritdoc />
    public async Task<Result<string>> RegisterEventAsync(MLogEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (string.IsNullOrWhiteSpace(_options.MLogBaseUrl))
        {
            return Result<string>.Failure(ErrorCodes.MLogFailed, "MLog base URL is not configured.");
        }

        try
        {
            using var http = new HttpRequestMessage(HttpMethod.Post, $"{_options.MLogBaseUrl.TrimEnd('/')}/register")
            {
                Content = JsonContent.Create(evt, options: s_canonicalJsonOptions),
            };
            MGovHttp.Decorate(http, _options.MLogBearer, evt.EventCorrelation ?? evt.EventId, _clock);

            using var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MLog /register failed with status {Status} for event {EventId}.",
                    (int)response.StatusCode, evt.EventId);
                return Result<string>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.");
            }

            // Upstream returns the assigned uid as a JSON string OR an envelope { "uid": "..." }.
            // Tolerate both — uid is the only field downstream cares about.
            var rawBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var uid = TryExtractUid(rawBody) ?? evt.EventId;
            return Result<string>.Success(uid);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MLog /register transport failure for event {EventId}.", evt.EventId);
            return Result<string>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "MLog /register timed out for event {EventId}.", evt.EventId);
            return Result<string>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MLog /register returned malformed JSON for event {EventId}.", evt.EventId);
            return Result<string>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<MLogQueryResult>> QueryAsync(string filter, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.MLogBaseUrl))
        {
            return Result<MLogQueryResult>.Failure(ErrorCodes.MLogFailed, "MLog base URL is not configured.");
        }

        try
        {
            var url = $"{_options.MLogBaseUrl.TrimEnd('/')}/query?filter={WebUtility.UrlEncode(filter ?? string.Empty)}&skip={skip}&take={take}";
            using var http = new HttpRequestMessage(HttpMethod.Get, url);
            MGovHttp.Decorate(http, _options.MLogBearer, string.Empty, _clock);

            using var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<MLogQueryResult>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.");
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<MLogQueryResult>(s_canonicalJsonOptions, ct)
                .ConfigureAwait(false);
            return parsed is null
                ? Result<MLogQueryResult>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.")
                : Result<MLogQueryResult>.Success(parsed);
        }
        catch (HttpRequestException) { return Result<MLogQueryResult>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Result<MLogQueryResult>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed."); }
        catch (JsonException) { return Result<MLogQueryResult>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed."); }
    }

    /// <inheritdoc />
    public async Task<Result<MLogEvent>> GetByUidAsync(string uid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.MLogBaseUrl))
        {
            return Result<MLogEvent>.Failure(ErrorCodes.MLogFailed, "MLog base URL is not configured.");
        }
        if (string.IsNullOrWhiteSpace(uid))
        {
            return Result<MLogEvent>.Failure(ErrorCodes.ValidationFailed, "uid is required.");
        }

        try
        {
            using var http = new HttpRequestMessage(HttpMethod.Get, $"{_options.MLogBaseUrl.TrimEnd('/')}/query/{Uri.EscapeDataString(uid)}");
            MGovHttp.Decorate(http, _options.MLogBearer, string.Empty, _clock);

            using var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<MLogEvent>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.");
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<MLogEvent>(s_canonicalJsonOptions, ct)
                .ConfigureAwait(false);
            return parsed is null
                ? Result<MLogEvent>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed.")
                : Result<MLogEvent>.Success(parsed);
        }
        catch (HttpRequestException) { return Result<MLogEvent>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Result<MLogEvent>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed."); }
        catch (JsonException) { return Result<MLogEvent>.Failure(ErrorCodes.MLogFailed, "Upstream MLog call failed."); }
    }

    /// <summary>Extracts the upstream uid from either a bare JSON string or a <c>{"uid":"..."}</c> envelope. Returns null if neither shape matches.</summary>
    private static string? TryExtractUid(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString(),
                JsonValueKind.Object when doc.RootElement.TryGetProperty("uid", out var uid) && uid.ValueKind == JsonValueKind.String => uid.GetString(),
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }
}
