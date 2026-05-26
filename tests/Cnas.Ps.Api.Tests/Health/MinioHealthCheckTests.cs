using Cnas.Ps.Api.Health;
using Cnas.Ps.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Health;

/// <summary>
/// Unit tests for <see cref="MinioHealthCheck"/>. The MinIO SDK exposes
/// <c>BucketExistsAsync</c> on <see cref="IMinioClient"/>, which we substitute via NSubstitute.
/// </summary>
public sealed class MinioHealthCheckTests
{
    private static IOptions<MinioOptions> Options_(Action<MinioOptions>? mutate = null)
    {
        var opts = new MinioOptions();
        mutate?.Invoke(opts);
        return Options.Create(opts);
    }

    [Fact]
    public async Task MinioHealthCheck_EndpointUnconfigured_ReturnsDegraded()
    {
        var client = Substitute.For<IMinioClient>();
        var sut = new MinioHealthCheck(client, Options_(o => o.Endpoint = string.Empty));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        // No interaction with the MinIO client when not configured.
        await client.DidNotReceiveWithAnyArgs().BucketExistsAsync(default!, default);
    }

    [Fact]
    public async Task MinioHealthCheck_BucketExists_ReturnsHealthy()
    {
        var client = Substitute.For<IMinioClient>();
        client.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(true));
        var sut = new MinioHealthCheck(client, Options_(o => o.Endpoint = "minio:9000"));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task MinioHealthCheck_BucketMissing_ReturnsDegraded()
    {
        var client = Substitute.For<IMinioClient>();
        client.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(false));
        var sut = new MinioHealthCheck(client, Options_(o => o.Endpoint = "minio:9000"));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task MinioHealthCheck_ClientThrows_ReturnsUnhealthy()
    {
        var client = Substitute.For<IMinioClient>();
        client.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>())
              .Returns<Task<bool>>(_ => throw new InvalidOperationException("boom"));
        var sut = new MinioHealthCheck(client, Options_(o => o.Endpoint = "minio:9000"));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }
}
