using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// HTTP adapter for MCabinet — the Moldovan government's unified citizen dashboard
/// (<c>mcabinet.gov.md</c>). Implements the outbound REST surface described in the
/// CNAS-MCabinet interface contract:
/// <list type="bullet">
///   <item><c>POST   {base}/api/cards</c> — publish (create or update) a dossier card.</item>
///   <item><c>DELETE {base}/api/cards/{systemCode}/{externalId}</c> — retire a card.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The publisher is push-only: state-change events on a dossier are mirrored to
/// MCabinet so the citizen sees them in their dashboard. MCabinet de-duplicates by
/// <c>(systemCode, externalId)</c>, so republishing the same card is safe and
/// idempotent (CLAUDE.md cross-cutting: Idempotent Callbacks).
/// </para>
/// <para>
/// Empty <see cref="MCabinetOptions.BaseUrl"/> returns
/// <see cref="ErrorCodes.MCabinetPublishFailed"/> without making any HTTP call — this
/// local-dev safety guard prevents accidentally hitting the production endpoint when
/// configuration is missing.
/// </para>
/// <para>
/// Correlation: when an
/// <see cref="System.Diagnostics.Activity"/> is in scope (typical inside an ASP.NET
/// Core request pipeline), its <see cref="System.Diagnostics.Activity.Id"/> is
/// forwarded as <c>X-Correlation-Id</c> so the MCabinet trace can be joined to the
/// originating CNAS request.
/// </para>
/// </remarks>
/// <param name="httpClient">Injected typed-client; timeout + user-agent configured at DI registration.</param>
/// <param name="options">MCabinet configuration snapshot.</param>
/// <param name="logger">Structured logger; never logs bearer tokens or IDNPs at <c>Information</c>+ level.</param>
public sealed class MCabinetPublisher(
    HttpClient httpClient,
    IOptions<MCabinetOptions> options,
    ILogger<MCabinetPublisher> logger) : IMCabinetPublisher
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly MCabinetOptions _options = options.Value;
    private readonly ILogger<MCabinetPublisher> _logger = logger;

    /// <summary>
    /// JSON options for the outbound MCabinet body. Enum values are serialised by name
    /// (e.g. <c>"InExamination"</c>) so MCabinet can map them to citizen-facing i18n
    /// labels without coupling to CNAS internal codes; date values are serialised in
    /// strict ISO-8601 Zulu form (no offset) by the explicit converter below.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = BuildJsonOptions();

    /// <summary>Builds the immutable JSON option set used by every outbound call.</summary>
    private static JsonSerializerOptions BuildJsonOptions()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        opts.Converters.Add(new JsonStringEnumConverter());
        opts.Converters.Add(new IsoUtcDateTimeConverter());
        return opts;
    }

    /// <summary>Outbound JSON body for <c>POST /api/cards</c>.</summary>
    private sealed record CardBody(
        string SystemCode,
        string ExternalId,
        string CitizenIdnp,
        string ServiceCode,
        MCabinetStatus Status,
        string TitleRo,
        string? SubtitleRo,
        DateTime EventUtc,
        string? DeepLink);

    /// <inheritdoc />
    public async Task<Result> PublishCardAsync(MCabinetCard card, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogWarning("MCabinet publish skipped — base URL not configured.");
            return Result.Failure(ErrorCodes.MCabinetPublishFailed, "MCabinet BaseUrl not configured");
        }

        var body = new CardBody(
            SystemCode: _options.SystemCode,
            ExternalId: card.ExternalId,
            CitizenIdnp: card.CitizenIdnp,
            ServiceCode: card.ServiceCode,
            Status: card.Status,
            TitleRo: card.TitleRo,
            SubtitleRo: card.SubtitleRo,
            // Force UTC kind so the ISO-8601 converter emits a trailing Z even if the
            // caller passed Unspecified/Local — defensive normalisation at the boundary.
            EventUtc: DateTime.SpecifyKind(card.EventUtc, DateTimeKind.Utc),
            DeepLink: card.DeepLink?.ToString());

        try
        {
            var uri = $"{_options.BaseUrl.TrimEnd('/')}/api/cards";
            using var http = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(body, options: JsonOptions),
            };
            Decorate(http);

            using var response = await _httpClient.SendAsync(http, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "MCabinet publish failed with status {Status} for externalId {ExternalId}.",
                    (int)response.StatusCode, card.ExternalId);
                return Result.Failure(
                    ErrorCodes.MCabinetPublishFailed,
                    string.IsNullOrEmpty(detail)
                        ? $"MCabinet returned HTTP {(int)response.StatusCode}."
                        : detail);
            }

            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MCabinet publish transport failure for externalId {ExternalId}.", card.ExternalId);
            return Result.Failure(ErrorCodes.MCabinetPublishFailed, "Upstream MCabinet call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MCabinet publish timed out for externalId {ExternalId}.", card.ExternalId);
            return Result.Failure(ErrorCodes.MCabinetPublishFailed, "Upstream MCabinet call timed out.");
        }
    }

    /// <inheritdoc />
    public async Task<Result> RetireCardAsync(string externalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogWarning("MCabinet retire skipped — base URL not configured.");
            return Result.Failure(ErrorCodes.MCabinetPublishFailed, "MCabinet BaseUrl not configured");
        }

        try
        {
            var uri = $"{_options.BaseUrl.TrimEnd('/')}/api/cards/{Uri.EscapeDataString(_options.SystemCode)}/{Uri.EscapeDataString(externalId)}";
            using var http = new HttpRequestMessage(HttpMethod.Delete, uri);
            Decorate(http);

            using var response = await _httpClient.SendAsync(http, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "MCabinet retire failed with status {Status} for externalId {ExternalId}.",
                    (int)response.StatusCode, externalId);
                return Result.Failure(
                    ErrorCodes.MCabinetPublishFailed,
                    string.IsNullOrEmpty(detail)
                        ? $"MCabinet returned HTTP {(int)response.StatusCode}."
                        : detail);
            }

            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MCabinet retire transport failure for externalId {ExternalId}.", externalId);
            return Result.Failure(ErrorCodes.MCabinetPublishFailed, "Upstream MCabinet call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MCabinet retire timed out for externalId {ExternalId}.", externalId);
            return Result.Failure(ErrorCodes.MCabinetPublishFailed, "Upstream MCabinet call timed out.");
        }
    }

    /// <summary>
    /// Decorates an outbound MCabinet request with the bearer-auth header, an
    /// accept-json header, and (when an <see cref="Activity"/> is in scope) the
    /// ambient correlation id so MCabinet logs can be joined to CNAS traces.
    /// </summary>
    /// <param name="request">Outbound message to decorate.</param>
    private void Decorate(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // MCabinet has migrated to mTLS — the cert is attached by the typed-client's
        // primary handler. The Bearer header is kept as a graceful-degradation path for
        // environments still on the legacy auth model (and for dev/CI mocks). The
        // [Obsolete] suppression below acknowledges that this branch is dead in
        // production but intentionally left wired for back-compat.
#pragma warning disable CS0618 // MCabinetOptions.Bearer is [Obsolete] — retained for transitional back-compat.
        if (!string.IsNullOrEmpty(_options.Bearer))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Bearer);
        }
#pragma warning restore CS0618
        var correlationId = Activity.Current?.Id;
        if (!string.IsNullOrEmpty(correlationId))
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        }
    }

    /// <summary>
    /// Reads up to a small bounded prefix of <paramref name="response"/>'s body for
    /// inclusion in the failure message. Never throws — a body that cannot be read
    /// becomes the empty string so the caller still gets the HTTP-status fallback
    /// message.
    /// </summary>
    /// <param name="response">Upstream response (already known to be non-2xx).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // Cap the included detail so error logs stay bounded even if the upstream
            // returns a large HTML error page.
            const int Max = 512;
            return body.Length <= Max ? body : body[..Max];
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
        catch (TaskCanceledException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// JSON converter that writes <see cref="DateTime"/> values in strict ISO-8601 Zulu
    /// form (e.g. <c>2026-05-19T10:00:00Z</c>) regardless of the inbound
    /// <see cref="DateTime.Kind"/>. Reading uses the default round-trip parser. CNAS
    /// stores everything in UTC (CLAUDE.md cross-cutting — UTC Everywhere), so this
    /// converter is purely a wire-format guarantee.
    /// </summary>
    // Internal so the unit-test suite can exercise the Unspecified-kind handling
    // directly. Production callers always go through MCabinetPublisher and never
    // touch this type by name.
    internal sealed class IsoUtcDateTimeConverter : JsonConverter<DateTime>
    {
        /// <inheritdoc />
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var raw = reader.GetString();
            return raw is null
                ? default
                : DateTime.SpecifyKind(DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeKind.Utc);
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            // Unspecified-kind handling: previously a value with Kind=Unspecified was
            // treated as Local (the default for ToUniversalTime) and shifted by the
            // server's local offset. CNAS stores UTC everywhere (CLAUDE.md), so an
            // Unspecified value coming through this converter is *already* a UTC
            // wall-clock; we must NOT apply a timezone shift. Only true Local values
            // get converted.
            var utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => value,
            };
            // ISO-8601 with second precision and trailing Z — matches the contract
            // documented in IMCabinetPublisher and is parseable by every MCabinet
            // implementation we have visibility into.
            writer.WriteStringValue(utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        }
    }

}
