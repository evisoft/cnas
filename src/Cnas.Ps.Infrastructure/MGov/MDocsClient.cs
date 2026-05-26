using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// HTTP adapter for MDocs — the government managed-document-storage service.
/// Implements the placeholder REST surface described in
/// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MDocs":
/// <list type="bullet">
///   <item><c>POST  {base}/api/v1/documents</c> — multipart upload (file + metadata).</item>
///   <item><c>GET   {base}/api/v1/documents/{id}/content</c> — raw bytes.</item>
///   <item><c>GET   {base}/api/v1/documents/{id}</c> — JSON metadata block.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The production contract will require client-certificate (MEGA-issued) authentication
/// PLUS an MPass-forwarded bearer token. This implementation currently sends only a
/// static bearer token via <see cref="MGovOptions.MDocsBearer"/>; the mTLS + token-relay
/// upgrade is queued for the next integration iteration and will swap out
/// <see cref="MGovHttp.Decorate(HttpRequestMessage, string, string, ICnasTimeProvider)"/>
/// for an auth-chain helper without changing the call signatures here.
/// </para>
/// <para>
/// All idempotency is keyed by the <c>X-Correlation-Id</c> header. Uploads with the same
/// payload (filename + content hash) derive the same correlation id, so MDocs can treat a
/// retry as a no-op rather than a duplicate (CLAUDE.md — Idempotent Callbacks).
/// </para>
/// </remarks>
/// <param name="httpClient">Injected typed-client; timeout + user-agent configured at DI registration.</param>
/// <param name="options">MGov configuration snapshot.</param>
/// <param name="logger">Structured logger; never logs bytes or filenames at <c>Information</c>+ level.</param>
/// <param name="clock">UTC clock — used for the <c>X-Request-Date</c> header.</param>
public sealed class MDocsClient(
    HttpClient httpClient,
    IOptions<MGovOptions> options,
    ILogger<MDocsClient> logger,
    ICnasTimeProvider clock) : IMDocsClient
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly MGovOptions _options = options.Value;
    private readonly ILogger<MDocsClient> _logger = logger;
    private readonly ICnasTimeProvider _clock = clock;

    /// <summary>Shape of the JSON body returned by <c>POST /api/v1/documents</c>.</summary>
    private sealed record UploadResponse(
        string DocumentId,
        string Version,
        string Sha256,
        DateTime UploadedAtUtc);

    /// <summary>Shape of the JSON body returned by <c>GET /api/v1/documents/{id}</c>.</summary>
    private sealed record MetadataResponse(
        string DocumentId,
        string FileName,
        string ContentType,
        long SizeBytes,
        string Sha256,
        string Version,
        DateTime UploadedAtUtc,
        Dictionary<string, string>? Tags);

    /// <inheritdoc />
    public async Task<Result<MDocsUploadReceipt>> UploadAsync(MDocsUploadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.MDocsBaseUrl))
        {
            _logger.LogWarning("MDocs upload called without configured base URL — returning INTERNAL_ERROR.");
            return Result<MDocsUploadReceipt>.Failure(ErrorCodes.Internal, "MDocs base URL is not configured.");
        }
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return Result<MDocsUploadReceipt>.Failure(ErrorCodes.ValidationFailed, "FileName is required.");
        }
        if (string.IsNullOrWhiteSpace(request.ContentType))
        {
            return Result<MDocsUploadReceipt>.Failure(ErrorCodes.ValidationFailed, "ContentType is required.");
        }
        if (request.Content is null || request.Content.Length == 0)
        {
            return Result<MDocsUploadReceipt>.Failure(ErrorCodes.ValidationFailed, "Content is required.");
        }

        // Correlation id derived from filename + content hash so retries of the same upload
        // hit the same upstream row (MDocs uses X-Correlation-Id for dedupe).
        var correlationSeed = $"{request.FileName}\n{Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(request.Content))}";
        var correlationId = MGovHttp.DeriveCorrelationId(correlationSeed);

        try
        {
            var uri = $"{_options.MDocsBaseUrl.TrimEnd('/')}/api/v1/documents";
            using var multipart = new MultipartFormDataContent($"----cnas-{correlationId}");

            var fileContent = new ByteArrayContent(request.Content);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType);
            multipart.Add(fileContent, name: "file", fileName: request.FileName);

            if (!string.IsNullOrWhiteSpace(request.CategoryCode))
            {
                multipart.Add(new StringContent(request.CategoryCode), name: "categoryCode");
            }
            if (request.Tags is { Count: > 0 })
            {
                var tagsJson = JsonSerializer.Serialize(request.Tags, MGovHttp.JsonOptions);
                multipart.Add(new StringContent(tagsJson, System.Text.Encoding.UTF8, "application/json"), name: "tags");
            }

            using var http = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = multipart,
            };
#pragma warning disable CS0618 // MGovOptions.MDocsBearer is [Obsolete] — kept for transitional back-compat; mTLS attaches the cert via the primary handler.
            MGovHttp.Decorate(http, _options.MDocsBearer, correlationId, _clock);
#pragma warning restore CS0618

            using var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "MDocs upload failed with status {Status} (correlation {Correlation}).",
                    (int)response.StatusCode, correlationId);
                return Result<MDocsUploadReceipt>.Failure(ErrorCodes.MDocsFailed, "Upstream MDocs call failed.");
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<UploadResponse>(MGovHttp.JsonOptions, ct)
                .ConfigureAwait(false);
            if (parsed is null || string.IsNullOrEmpty(parsed.DocumentId))
            {
                _logger.LogWarning("MDocs upload returned empty body (correlation {Correlation}).", correlationId);
                return Result<MDocsUploadReceipt>.Failure(ErrorCodes.MDocsFailed, "MDocs returned an empty response.");
            }

            return Result<MDocsUploadReceipt>.Success(new MDocsUploadReceipt(
                parsed.DocumentId,
                parsed.Version,
                parsed.Sha256,
                DateTime.SpecifyKind(parsed.UploadedAtUtc, DateTimeKind.Utc)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MDocs upload transport failure (correlation {Correlation}).", correlationId);
            return Result<MDocsUploadReceipt>.Failure(ErrorCodes.MDocsFailed, "Upstream MDocs call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MDocs upload timed out (correlation {Correlation}).", correlationId);
            return Result<MDocsUploadReceipt>.Failure(ErrorCodes.MDocsFailed, "Upstream MDocs call failed.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MDocs upload returned malformed JSON (correlation {Correlation}).", correlationId);
            return Result<MDocsUploadReceipt>.Failure(ErrorCodes.MDocsFailed, "Upstream MDocs call failed.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> DownloadAsync(string documentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        if (string.IsNullOrWhiteSpace(_options.MDocsBaseUrl))
        {
            _logger.LogWarning("MDocs download called without configured base URL — returning INTERNAL_ERROR.");
            return Result<Stream>.Failure(ErrorCodes.Internal, "MDocs base URL is not configured.");
        }

        var correlationId = MGovHttp.DeriveCorrelationId($"download\n{documentId}");

        try
        {
            var uri = $"{_options.MDocsBaseUrl.TrimEnd('/')}/api/v1/documents/{Uri.EscapeDataString(documentId)}/content";
            using var http = new HttpRequestMessage(HttpMethod.Get, uri);
#pragma warning disable CS0618 // MGovOptions.MDocsBearer is [Obsolete] — kept for transitional back-compat; mTLS attaches the cert via the primary handler.
            MGovHttp.Decorate(http, _options.MDocsBearer, correlationId, _clock);
#pragma warning restore CS0618

            // Do NOT wrap response in a using — we hand the stream to the caller. The
            // HttpResponseMessage gets disposed when the returned MemoryStream is read by
            // the caller; we copy the bytes here so the typed HttpClient's connection can
            // be returned to the pool immediately.
            var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "MDocs download failed with status {Status} (correlation {Correlation}).",
                        (int)response.StatusCode, correlationId);
                    return Result<Stream>.Failure(ErrorCodes.MDocsFailed, "Upstream MDocs call failed.");
                }

                var ms = new MemoryStream();
                await response.Content.CopyToAsync(ms, ct).ConfigureAwait(false);
                ms.Position = 0;
                return Result<Stream>.Success(ms);
            }
            finally
            {
                response.Dispose();
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MDocs download transport failure (correlation {Correlation}).", correlationId);
            return Result<Stream>.Failure(ErrorCodes.MDocsFailed, "Upstream MDocs call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MDocs download timed out (correlation {Correlation}).", correlationId);
            return Result<Stream>.Failure(ErrorCodes.MDocsFailed, "Upstream MDocs call failed.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<MDocsMetadata>> GetMetadataAsync(string documentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        if (string.IsNullOrWhiteSpace(_options.MDocsBaseUrl))
        {
            _logger.LogWarning("MDocs metadata called without configured base URL — returning INTERNAL_ERROR.");
            return Result<MDocsMetadata>.Failure(ErrorCodes.Internal, "MDocs base URL is not configured.");
        }

        var correlationId = MGovHttp.DeriveCorrelationId($"metadata\n{documentId}");

        try
        {
            var uri = $"{_options.MDocsBaseUrl.TrimEnd('/')}/api/v1/documents/{Uri.EscapeDataString(documentId)}";
            using var http = new HttpRequestMessage(HttpMethod.Get, uri);
#pragma warning disable CS0618 // MGovOptions.MDocsBearer is [Obsolete] — kept for transitional back-compat; mTLS attaches the cert via the primary handler.
            MGovHttp.Decorate(http, _options.MDocsBearer, correlationId, _clock);
#pragma warning restore CS0618

            using var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "MDocs metadata fetch failed with status {Status} (correlation {Correlation}).",
                    (int)response.StatusCode, correlationId);
                return Result<MDocsMetadata>.Failure(ErrorCodes.MDocsFailed, "Upstream MDocs call failed.");
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<MetadataResponse>(MGovHttp.JsonOptions, ct)
                .ConfigureAwait(false);
            if (parsed is null || string.IsNullOrEmpty(parsed.DocumentId))
            {
                _logger.LogWarning("MDocs metadata returned empty body (correlation {Correlation}).", correlationId);
                return Result<MDocsMetadata>.Failure(ErrorCodes.MDocsFailed, "MDocs returned an empty response.");
            }

            // Tags is optional on the wire — surface an empty dictionary so consumers don't
            // have to null-check.
            var tags = (IReadOnlyDictionary<string, string>)(parsed.Tags ?? new Dictionary<string, string>());

            return Result<MDocsMetadata>.Success(new MDocsMetadata(
                parsed.DocumentId,
                parsed.FileName,
                parsed.ContentType,
                parsed.SizeBytes,
                parsed.Sha256,
                parsed.Version,
                DateTime.SpecifyKind(parsed.UploadedAtUtc, DateTimeKind.Utc),
                tags));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MDocs metadata transport failure (correlation {Correlation}).", correlationId);
            return Result<MDocsMetadata>.Failure(ErrorCodes.MDocsFailed, "Upstream MDocs call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MDocs metadata timed out (correlation {Correlation}).", correlationId);
            return Result<MDocsMetadata>.Failure(ErrorCodes.MDocsFailed, "Upstream MDocs call failed.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MDocs metadata returned malformed JSON (correlation {Correlation}).", correlationId);
            return Result<MDocsMetadata>.Failure(ErrorCodes.MDocsFailed, "Upstream MDocs call failed.");
        }
    }
}
