using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure;
using Cnas.Ps.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Infrastructure.Tests.Storage;

/// <summary>
/// Tests for the missing-MinIO-credentials sentinel registered by
/// <c>AddCnasInfrastructure</c>. Mirrors the
/// <see cref="Cnas.Ps.Infrastructure.Security.MissingKeyFieldEncryptor"/> /
/// <see cref="Cnas.Ps.Infrastructure.Security.MissingSaltHmacHasher"/>
/// fail-loud pattern: when <c>Minio:AccessKey</c> or <c>Minio:SecretKey</c>
/// is absent, DI must resolve a sentinel that throws on every method call —
/// never silently no-op, never throw at DI construction time.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the sentinel exists.
/// They pin down the externally observable behaviour:
/// <list type="bullet">
///   <item>DI must build successfully when creds are blank (no throw on resolution)</item>
///   <item>Every <see cref="IFileStorage"/> method on the sentinel must throw <see cref="InvalidOperationException"/></item>
///   <item>Real creds still wire the real <see cref="MinioFileStorage"/></item>
/// </list>
/// </remarks>
public class MissingMinioFileStorageTests
{
    /// <summary>
    /// Without credentials configured, the service provider must successfully
    /// resolve an <see cref="IFileStorage"/> AND that instance must be the
    /// sentinel — not <see cref="MinioFileStorage"/>. The DI construction
    /// must NOT throw (the previous behaviour was an exception from
    /// <c>MinioClient.Build()</c> at activation time, which broke unrelated
    /// controller activations).
    /// </summary>
    [Fact]
    public void WhenCredsMissing_DI_ResolvesSentinel()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["Minio:Endpoint"] = "localhost:9000",
            ["Minio:AccessKey"] = string.Empty,
            ["Minio:SecretKey"] = string.Empty,
            // The rest of the DI graph needs these to validate-on-start; they
            // are unrelated to the MinIO branch under test.
            ["Sqids:Alphabet"] = "FedcbHijklmnoGpqrstuvwxyZ0123456789ABCDEIJKLMNOPQRSTUVWXY",
            ["Sqids:MinLength"] = "8",
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=cnas;Username=u;Password=p",
        });

        var storage = provider.GetRequiredService<IFileStorage>();

        storage.Should().BeOfType<MissingMinioFileStorage>(
            "blank Minio creds must short-circuit to the fail-loud sentinel rather than constructing a real MinioClient that throws at activation time.");
    }

    /// <summary>
    /// Exercises every public method on the sentinel and asserts each one throws
    /// <see cref="InvalidOperationException"/> carrying the canonical
    /// "MinIO not configured" diagnostic. The message must mention the offending
    /// configuration keys so operators can grep production logs to the root cause.
    /// </summary>
    [Fact]
    public async Task Sentinel_AnyMethodCall_ThrowsWithClearMessage()
    {
        var sentinel = new MissingMinioFileStorage();

        await using var stream = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 });

        var putAct = async () => await sentinel.PutAsync("bucket", stream, "application/pdf");
        var getAct = async () => await sentinel.GetAsync("bucket", "key");
        var presignAct = async () => await sentinel.PresignDownloadAsync("bucket", "key", TimeSpan.FromMinutes(5));
        var deleteAct = async () => await sentinel.DeleteAsync("bucket", "key");

        await putAct.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("MinIO not configured", StringComparison.Ordinal)
                     && e.Message.Contains("AccessKey", StringComparison.Ordinal)
                     && e.Message.Contains("SecretKey", StringComparison.Ordinal));
        await getAct.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("MinIO not configured", StringComparison.Ordinal));
        await presignAct.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("MinIO not configured", StringComparison.Ordinal));
        await deleteAct.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("MinIO not configured", StringComparison.Ordinal));
    }

    /// <summary>
    /// Production behaviour: when both AccessKey and SecretKey are configured,
    /// DI must continue to resolve the real <see cref="MinioFileStorage"/>
    /// (and therefore the real <c>IMinioClient</c>). This guards against an
    /// over-zealous sentinel selector that breaks production once real creds
    /// land in the secret store.
    /// </summary>
    [Fact]
    public void WhenCredsPresent_DI_ResolvesRealClient()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["Minio:Endpoint"] = "localhost:9000",
            ["Minio:AccessKey"] = "real-access-key",
            ["Minio:SecretKey"] = "real-secret-key",
            ["Sqids:Alphabet"] = "FedcbHijklmnoGpqrstuvwxyZ0123456789ABCDEIJKLMNOPQRSTUVWXY",
            ["Sqids:MinLength"] = "8",
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=cnas;Username=u;Password=p",
        });

        var storage = provider.GetRequiredService<IFileStorage>();

        storage.Should().BeOfType<MinioFileStorage>(
            "with real creds present, DI must continue to wire the real MinioFileStorage — the sentinel exists only to cover the unconfigured branch.");
    }

    /// <summary>
    /// Builds a minimal service provider that invokes <c>AddCnasInfrastructure</c>
    /// against an in-memory configuration. We don't need the API layer for this
    /// test — only the registration the MinIO branch lives in.
    /// </summary>
    private static ServiceProvider BuildServiceProvider(IDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCnasInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
