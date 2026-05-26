using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// HTTP adapter for MNotify — government citizen notification dispatch (email/SMS).
/// Calls the real MEGA endpoint <c>POST /api/Notification</c> with the canonical
/// multi-language <see cref="NotificationRequest"/> body. Auth is mTLS (configured at the
/// primary handler via <c>ICertificateStore</c>); the client itself sends no
/// <c>Authorization</c> header. The legacy <see cref="SendAsync(MNotifyMessage, CancellationToken)"/>
/// entry point is preserved as a back-compat shim that translates the original 4-tuple
/// into a <see cref="NotificationRequest"/>.
/// </summary>
/// <param name="httpClient">Injected typed-client.</param>
/// <param name="options">MGov configuration snapshot.</param>
/// <param name="logger">Structured logger; never logs the recipient IDNP or parameters.</param>
/// <param name="clock">UTC clock for the <c>X-Request-Date</c> header.</param>
public sealed partial class MNotifyClient(
    HttpClient httpClient,
    IOptions<MGovOptions> options,
    ILogger<MNotifyClient> logger,
    ICnasTimeProvider clock) : IMNotifyClient
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly MGovOptions _options = options.Value;
    private readonly ILogger<MNotifyClient> _logger = logger;
    private readonly ICnasTimeProvider _clock = clock;

    /// <summary>
    /// JSON serializer options for the MEGA <c>NotificationRequest</c>. camelCase property
    /// names everywhere except for the recipient <c>type</c>, which carries mixed-case
    /// wire spellings (<c>email</c> / <c>IDNP</c> / <c>msisdn</c>) via a custom converter.
    /// </summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null, // language tags ("ro", "ru") preserved verbatim
        Converters = { new NotificationRecipientTypeJsonConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Response body returned by <c>POST /api/Notification</c>.</summary>
    private sealed record NotificationResponse(string NotificationId);

    /// <inheritdoc />
    public async Task<Result<string>> SendNotificationAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.MNotifyBaseUrl))
        {
            _logger.LogWarning("MNotify called without configured base URL — returning MNOTIFY_FAILED.");
            return Result<string>.Failure(ErrorCodes.MNotifyFailed, "MNotify base URL is not configured.");
        }
        if (request.Recipients is null || request.Recipients.Count == 0)
        {
            return Result<string>.Failure(ErrorCodes.ValidationFailed, "At least one recipient is required.");
        }

        var canonical = JsonSerializer.Serialize(request, s_jsonOptions);
        var correlationId = request.CorrelationId ?? MGovHttp.DeriveCorrelationId(canonical);

        try
        {
            using var http = new HttpRequestMessage(HttpMethod.Post, $"{_options.MNotifyBaseUrl.TrimEnd('/')}/api/Notification")
            {
                Content = new StringContent(canonical, System.Text.Encoding.UTF8, "application/json"),
            };
            // mTLS replaces bearer auth — pass empty bearer so MGovHttp.Decorate omits the header.
            MGovHttp.Decorate(http, string.Empty, correlationId, _clock);

            using var response = await _httpClient.SendAsync(http, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MNotify call failed with status {Status} (correlation {Correlation}).",
                    (int)response.StatusCode, correlationId);
                return Result<string>.Failure(ErrorCodes.MNotifyFailed, "Upstream MNotify call failed.");
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<NotificationResponse>(s_jsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (parsed is null || string.IsNullOrEmpty(parsed.NotificationId))
            {
                _logger.LogWarning("MNotify returned empty notification id (correlation {Correlation}).", correlationId);
                return Result<string>.Failure(ErrorCodes.MNotifyFailed, "Upstream MNotify call failed.");
            }

            return Result<string>.Success(parsed.NotificationId);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MNotify transport failure (correlation {Correlation}).", correlationId);
            return Result<string>.Failure(ErrorCodes.MNotifyFailed, "Upstream MNotify call failed.");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "MNotify timed out (correlation {Correlation}).", correlationId);
            return Result<string>.Failure(ErrorCodes.MNotifyFailed, "Upstream MNotify call failed.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MNotify returned malformed JSON (correlation {Correlation}).", correlationId);
            return Result<string>.Failure(ErrorCodes.MNotifyFailed, "Upstream MNotify call failed.");
        }
    }

    /// <inheritdoc />
    public async Task<Result> SendAsync(MNotifyMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(_options.MNotifyBaseUrl))
        {
            return Result.Failure(ErrorCodes.MNotifyFailed, "MNotify base URL is not configured.");
        }
        if (string.IsNullOrWhiteSpace(message.RecipientIdnp))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Recipient IDNP is required.");
        }

        // Translate the legacy 4-tuple into a NotificationRequest:
        //   • The IDNP becomes the sole recipient with type IDNP.
        //   • Subject / body parameters (if supplied) become the multi-language dicts
        //     under the default Romanian tag. Any other parameters are ignored — the
        //     legacy callers either set subject + body explicitly, or rely on MNotify
        //     to pick a template by code (in which case empty subject/body is fine).
        var parameters = message.Parameters ?? new Dictionary<string, string>();
        parameters.TryGetValue("subject", out var subject);
        parameters.TryGetValue("body", out var body);
        var request = new NotificationRequest(
            Subject: new Dictionary<string, string> { ["ro"] = subject ?? message.TemplateCode },
            Body: new Dictionary<string, string> { ["ro"] = body ?? string.Empty },
            BodyShort: null,
            Recipients: new[] { new NotificationRecipient(NotificationRecipientType.Idnp, message.RecipientIdnp) },
            Attachments: null,
            CorrelationId: null);

        var result = await SendNotificationAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Result.Success()
            : Result.Failure(result.ErrorCode!, result.ErrorMessage!);
    }
}

/// <summary>
/// Custom JSON converter that serialises <see cref="NotificationRecipientType"/> using
/// the mixed-case wire spellings mandated by the MEGA MNotify spec:
/// <c>Email → "email"</c>, <c>Idnp → "IDNP"</c>, <c>Msisdn → "msisdn"</c>. Reading is
/// case-insensitive (tolerates upstream variations).
/// </summary>
internal sealed class NotificationRecipientTypeJsonConverter : JsonConverter<NotificationRecipientType>
{
    /// <inheritdoc />
    public override NotificationRecipientType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString()?.ToUpperInvariant() switch
        {
            "EMAIL" => NotificationRecipientType.Email,
            "IDNP" => NotificationRecipientType.Idnp,
            "MSISDN" => NotificationRecipientType.Msisdn,
            var v => throw new JsonException($"Unrecognised recipient type '{v}'."),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, NotificationRecipientType value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            NotificationRecipientType.Email => "email",
            NotificationRecipientType.Idnp => "IDNP",
            NotificationRecipientType.Msisdn => "msisdn",
            _ => throw new JsonException($"Unhandled recipient type {value}."),
        });
}
