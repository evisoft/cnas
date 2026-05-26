using Cnas.Ps.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace Cnas.Ps.Api.Health;

/// <summary>
/// Readiness probe for the MinIO object-storage dependency. Issues a low-cost
/// <c>BucketExistsAsync</c> call against the bucket that backs CNAS-generated
/// documents. A missing bucket reports <see cref="HealthStatus.Degraded"/>
/// ("endpoint reachable but bucket missing") rather than <see cref="HealthStatus.Unhealthy"/>
/// because the storage layer creates the bucket lazily on first upload.
/// </summary>
/// <remarks>
/// The probe uses the same 2-second timeout ceiling as <see cref="HttpReachabilityProbe"/>.
/// Endpoint absence (empty <see cref="MinioOptions.Endpoint"/>) is treated as degraded
/// — at startup time <see cref="MinioOptions.Endpoint"/> is required by configuration
/// validation, so this guard is defence-in-depth against tests that bypass options
/// validation.
/// </remarks>
/// <param name="minioClient">Registered <see cref="IMinioClient"/> singleton.</param>
/// <param name="options">Bound MinIO configuration snapshot.</param>
public sealed class MinioHealthCheck(
    IMinioClient minioClient,
    IOptions<MinioOptions> options) : IHealthCheck
{
    /// <summary>Probe path tag — used for structured logging and tag-based filtering.</summary>
    private const string ProbePathTag = "storage.minio.ready";

    /// <summary>Bucket queried for existence — the canonical CNAS-generated-documents bucket.</summary>
    private const string ProbeBucket = "cnas-documents";

    /// <summary>Hard ceiling for the probe — keep aligned with <see cref="HttpReachabilityProbe.DefaultTimeout"/>.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly IMinioClient _minioClient = minioClient;
    private readonly MinioOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return HealthCheckResult.Degraded($"{ProbePathTag}: MinIO endpoint not configured.");
        }

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(ProbeTimeout);

        try
        {
            var exists = await _minioClient
                .BucketExistsAsync(new BucketExistsArgs().WithBucket(ProbeBucket), probeCts.Token)
                .ConfigureAwait(false);

            return exists
                ? HealthCheckResult.Healthy($"MinIO: ok (bucket '{ProbeBucket}' exists).")
                : HealthCheckResult.Degraded(
                    $"MinIO: endpoint reachable but bucket '{ProbeBucket}' is missing.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"MinIO: timed out after {ProbeTimeout.TotalSeconds:F1}s.", ex);
        }
#pragma warning disable CA1031 // Health checks must catch broad exceptions — the framework requires a never-throw contract.
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MinIO: transport failure.", ex);
        }
#pragma warning restore CA1031
    }
}
