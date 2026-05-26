using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Cnas.Ps.Application.MessageBus;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services.MessageBus;

/// <summary>
/// R0117 / CF 14.11 — HTTP adapter for the Portalul guvernamental de date (PGD) open-data
/// portal. Implements the outbound REST surface as documented on
/// <see cref="IPgdPublisher"/>: a single <c>POST {base}/api/datasets/{datasetCode}</c>
/// publishing the supplied payload with the upstream Content-Type header.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placeholder mode.</b> When <see cref="PgdPublisherOptions.BaseUrl"/> is blank the
/// adapter returns a deterministic <see cref="ErrorCodes.PgdNotConfigured"/> failure
/// without touching the network — matches the shape of
/// <c>S3CompatibleBackupTarget</c> (iter 90). Operations dashboards can rely on this
/// behaviour to detect missing wiring.
/// </para>
/// <para>
/// <b>Metrics.</b> Every call increments <see cref="CnasMeter.PgdPublishAttempted"/>
/// (tagged with <c>dataset_code</c>) at entry, and
/// <see cref="CnasMeter.PgdPublishOutcome"/> (tagged with <c>dataset_code</c> +
/// <c>status</c>) on the way out. Tags are bounded-cardinality strings, never user IDs.
/// </para>
/// </remarks>
/// <param name="httpClient">Injected typed HTTP client.</param>
/// <param name="options">Bound options snapshot.</param>
/// <param name="logger">Structured logger; never logs the API key.</param>
public sealed class PgdPublisher(
    HttpClient httpClient,
    IOptions<PgdPublisherOptions> options,
    ILogger<PgdPublisher> logger) : IPgdPublisher
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly PgdPublisherOptions _options = options.Value;
    private readonly ILogger<PgdPublisher> _logger = logger;

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<Result<PgdPublishOutcomeDto>> PublishAsync(
        PgdDatasetPublishInputDto input,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Emit the attempted counter first so we observe even publishes that never reach
        // the network. dataset_code is a bounded admin-supplied string — safe to tag.
        CnasMeter.PgdPublishAttempted.Add(1, new KeyValuePair<string, object?>("dataset_code", input.DatasetCode));

        // ─── Local-dev safety guard: blank BaseUrl short-circuits to Skipped. ───
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogWarning(
                "PGD publish skipped — base URL not configured. DatasetCode={DatasetCode}",
                input.DatasetCode);
            CnasMeter.PgdPublishOutcome.Add(1,
                new KeyValuePair<string, object?>("dataset_code", input.DatasetCode),
                new KeyValuePair<string, object?>("status", "skipped"));
            return Result<PgdPublishOutcomeDto>.Failure(
                ErrorCodes.PgdNotConfigured,
                "PGD publisher base URL is not configured for this environment.");
        }

        try
        {
            var uri = $"{_options.BaseUrl.TrimEnd('/')}/api/datasets/{Uri.EscapeDataString(input.DatasetCode)}";
            using var http = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(input.PayloadJson, Encoding.UTF8, input.ContentType),
            };
            // Stable headers (title + description forwarded out-of-band so the body remains
            // an opaque payload).
            http.Headers.TryAddWithoutValidation("X-Pgd-Title", input.Title);
            http.Headers.TryAddWithoutValidation("X-Pgd-Description", input.Description);
            http.Headers.TryAddWithoutValidation("X-Pgd-System-Code", _options.SystemCode);

            Decorate(http);

            using var response = await _httpClient.SendAsync(http, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "PGD publish failed with status {Status} for dataset {DatasetCode}.",
                    (int)response.StatusCode, input.DatasetCode);
                CnasMeter.PgdPublishOutcome.Add(1,
                    new KeyValuePair<string, object?>("dataset_code", input.DatasetCode),
                    new KeyValuePair<string, object?>("status", "rejected"));
                return Result<PgdPublishOutcomeDto>.Failure(
                    ErrorCodes.PgdPublishFailed,
                    $"PGD returned HTTP {(int)response.StatusCode}.");
            }

            // Upstream reference id (when supplied) lives in the X-Pgd-Reference-Id
            // response header. The publisher does not parse the body; the upstream
            // contract returns a small JSON body but the header is sufficient for the
            // outcome shape.
            response.Headers.TryGetValues("X-Pgd-Reference-Id", out var refValues);
            var referenceId = refValues?.FirstOrDefault();

            CnasMeter.PgdPublishOutcome.Add(1,
                new KeyValuePair<string, object?>("dataset_code", input.DatasetCode),
                new KeyValuePair<string, object?>("status", "accepted"));

            return Result<PgdPublishOutcomeDto>.Success(new PgdPublishOutcomeDto(
                Status: PgdPublishStatus.Accepted,
                PgdReferenceId: referenceId,
                FailureReason: null));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "PGD publish transport failure for dataset {DatasetCode}.", input.DatasetCode);
            CnasMeter.PgdPublishOutcome.Add(1,
                new KeyValuePair<string, object?>("dataset_code", input.DatasetCode),
                new KeyValuePair<string, object?>("status", "rejected"));
            return Result<PgdPublishOutcomeDto>.Failure(
                ErrorCodes.PgdPublishFailed,
                "Upstream PGD call failed.");
        }
        catch (System.Threading.Tasks.TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "PGD publish timed out for dataset {DatasetCode}.", input.DatasetCode);
            CnasMeter.PgdPublishOutcome.Add(1,
                new KeyValuePair<string, object?>("dataset_code", input.DatasetCode),
                new KeyValuePair<string, object?>("status", "rejected"));
            return Result<PgdPublishOutcomeDto>.Failure(
                ErrorCodes.PgdPublishFailed,
                "Upstream PGD call timed out.");
        }
    }

    /// <summary>
    /// Decorates an outbound PGD request with the optional API key + correlation header.
    /// </summary>
    /// <param name="request">Outbound message to decorate.</param>
    private void Decorate(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
        var correlationId = Activity.Current?.Id;
        if (!string.IsNullOrEmpty(correlationId))
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        }
    }
}
