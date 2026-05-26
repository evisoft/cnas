using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// Cloudflare Turnstile implementation of <see cref="ICaptchaVerifier"/>. Posts the
/// supplied token to the provider's <c>siteverify</c> endpoint
/// (<c>https://challenges.cloudflare.com/turnstile/v0/siteverify</c>) and translates
/// the JSON response into a <see cref="Result"/> with one of the documented
/// <see cref="ErrorCodes"/> codes (R0035).
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-closed posture (CLAUDE.md §5 — security built in).</b> Any transport-grade
/// failure (timeout, <see cref="HttpRequestException"/>, non-success status, malformed
/// JSON) maps to <see cref="ErrorCodes.CaptchaProviderUnreachable"/>. The
/// <c>RequireCaptchaAttribute</c> then surfaces this as HTTP 503 — a degraded provider
/// must NOT become an open gate.
/// </para>
/// <para>
/// <b>Token / secret hygiene.</b> The verifier never writes the token, the secret key,
/// or any portion of either to logs or to its returned <see cref="Result.ErrorMessage"/>.
/// The provider's <c>error-codes</c> array IS appended to the failure message — those
/// are stable Turnstile diagnostic codes (e.g. <c>"timeout-or-duplicate"</c>) and carry
/// no caller-supplied data.
/// </para>
/// <para>
/// <b>HTTP client.</b> The client is resolved by name (<see cref="ClientName"/>) from
/// <see cref="IHttpClientFactory"/> so the registration site (in
/// <c>InfrastructureServiceCollectionExtensions.AddCnasInfrastructure</c>) owns the
/// timeout / user-agent / future resilience policies. The factory's per-call lifetime
/// avoids socket exhaustion under load.
/// </para>
/// <para>
/// <b>Dev / CI bypass.</b> When <see cref="TurnstileOptions.BypassForTesting"/> is
/// <c>true</c>, the verifier returns <see cref="Result.Success()"/> without making an
/// HTTP call. Used by the in-process test fixtures so the existing integration suite
/// keeps running without hitting Cloudflare from CI. Production configuration must set
/// this flag to <c>false</c>.
/// </para>
/// </remarks>
public sealed class TurnstileCaptchaVerifier : ICaptchaVerifier
{
    /// <summary>
    /// Named-client identifier under which the Turnstile <see cref="HttpClient"/> is
    /// registered. Exposed as a public constant so the composition root and the test
    /// fixtures share a single source of truth.
    /// </summary>
    public const string ClientName = "turnstile";

    /// <summary>Form-encoded content type required by the Turnstile siteverify endpoint.</summary>
    private const string FormContentType = "application/x-www-form-urlencoded";

    /// <summary>JSON deserializer options for the siteverify response body. Camel-case match.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TurnstileOptions _options;
    private readonly ILogger<TurnstileCaptchaVerifier> _logger;

    /// <summary>
    /// Initialises the verifier with its HTTP client factory, options snapshot, and
    /// structured logger. The options snapshot is captured at construction time — DI
    /// re-creates the singleton if configuration is reloaded, matching the lifetime
    /// of every other MGov / external-service client in the project.
    /// </summary>
    /// <param name="httpClientFactory">Factory supplying the named <c>turnstile</c> client.</param>
    /// <param name="options">Bound <see cref="TurnstileOptions"/> snapshot.</param>
    /// <param name="logger">Structured logger; never logs token or secret values.</param>
    public TurnstileCaptchaVerifier(
        IHttpClientFactory httpClientFactory,
        IOptions<TurnstileOptions> options,
        ILogger<TurnstileCaptchaVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> VerifyAsync(string? token, string? remoteIp, CancellationToken ct = default)
    {
        // Bypass path used by integration / E2E fixtures so the suite never touches
        // Cloudflare from CI. Production config must set BypassForTesting = false.
        if (_options.BypassForTesting)
        {
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            // Short-circuit BEFORE any HTTP call so we don't waste a Turnstile request
            // (and a TCP RTT) on a request that obviously can't succeed. Also keeps
            // operator dashboards clear of self-inflicted 503s.
            return Result.Failure(ErrorCodes.CaptchaTokenMissing, "CAPTCHA token is required.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient(ClientName);

            // Build the form body explicitly so we control which fields are included.
            // The remoteip field is omitted when null/empty — Turnstile tolerates its
            // absence and we don't want to send an empty string that could confuse a
            // downstream WAF.
            var fields = new List<KeyValuePair<string, string>>(3)
            {
                new("secret", _options.SecretKey ?? string.Empty),
                new("response", token),
            };
            if (!string.IsNullOrWhiteSpace(remoteIp))
            {
                fields.Add(new KeyValuePair<string, string>("remoteip", remoteIp));
            }

            using var content = new FormUrlEncodedContent(fields);
            // FormUrlEncodedContent defaults the media type to application/x-www-form-urlencoded
            // but we set it explicitly anyway so the content-type assertion in the
            // unit tests does not depend on framework defaults.
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(FormContentType);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.Timeout);

            using var response = await client
                .PostAsync(_options.VerifyUrl, content, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Turnstile siteverify returned non-success status {Status}.",
                    (int)response.StatusCode);
                return Result.Failure(
                    ErrorCodes.CaptchaProviderUnreachable,
                    "Could not reach CAPTCHA provider.");
            }

            TurnstileResponse? parsed;
            try
            {
                parsed = await response.Content
                    .ReadFromJsonAsync<TurnstileResponse>(s_jsonOptions, ct)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Turnstile siteverify returned malformed JSON.");
                return Result.Failure(
                    ErrorCodes.CaptchaProviderUnreachable,
                    "Could not reach CAPTCHA provider.");
            }

            if (parsed is null)
            {
                _logger.LogWarning("Turnstile siteverify returned an empty body.");
                return Result.Failure(
                    ErrorCodes.CaptchaProviderUnreachable,
                    "Could not reach CAPTCHA provider.");
            }

            if (parsed.Success)
            {
                return Result.Success();
            }

            // Provider rejected the token. Surface the provider error codes in the
            // human message for operator diagnostics; the raw token NEVER appears here.
            var errors = parsed.ErrorCodes is { Length: > 0 }
                ? string.Join(", ", parsed.ErrorCodes)
                : "(no provider error codes)";
            return Result.Failure(
                ErrorCodes.CaptchaTokenInvalid,
                $"Token rejected by provider: {errors}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Per-call timeout elapsed (linked CTS above). Caller's own cancellation
            // is re-thrown as-is (the when filter excludes it).
            _logger.LogWarning(ex, "Turnstile siteverify timed out.");
            return Result.Failure(
                ErrorCodes.CaptchaProviderUnreachable,
                "Could not reach CAPTCHA provider.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Turnstile siteverify transport failure.");
            return Result.Failure(
                ErrorCodes.CaptchaProviderUnreachable,
                "Could not reach CAPTCHA provider.");
        }
    }

    /// <summary>
    /// Wire-format DTO for the Turnstile siteverify JSON response. Only the fields
    /// the verifier actually consults are bound — the upstream payload may carry
    /// additional fields (<c>challenge_ts</c>, <c>hostname</c>, <c>action</c>,
    /// <c>cdata</c>) which we deliberately ignore.
    /// </summary>
    private sealed class TurnstileResponse
    {
        /// <summary>Whether the provider accepted the token.</summary>
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        /// <summary>
        /// Stable provider error codes describing the rejection (e.g.
        /// <c>"invalid-input-response"</c>). Only present when <see cref="Success"/>
        /// is <c>false</c>.
        /// </summary>
        [JsonPropertyName("error-codes")]
        public string[]? ErrorCodes { get; init; }
    }
}
