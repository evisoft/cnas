using System.Collections.Generic;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Infrastructure;
using Cnas.Ps.Infrastructure.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Infrastructure.Tests.Search;

/// <summary>
/// R0522 / TOR CF 03.03 — wiring test confirming that
/// <see cref="IFullTextSearchEngine"/> is resolvable from the DI container produced by
/// <see cref="InfrastructureServiceCollectionExtensions.AddCnasInfrastructure"/>, that the
/// default registration is the Postgres ILIKE adapter, and that the registration honours
/// the singleton lifetime contract.
/// </summary>
public sealed class FullTextSearchEngineWiringTests
{
    private static IConfiguration BuildConfig()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=cnas_test;Username=postgres;Password=postgres",
            ["Minio:Endpoint"] = "localhost:9000",
            ["Minio:AccessKey"] = "minio",
            ["Minio:SecretKey"] = "minio12345",
            ["Minio:UseSsl"] = "false",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void IFullTextSearchEngine_RegistrationIsSingleton_AndResolvesPostgresIlikeAdapter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCnasInfrastructure(BuildConfig());

        // Sanity — the descriptor itself must declare Singleton lifetime so the same
        // instance can be shared across requests (the engine is stateless and the
        // option-binding lives in DI options).
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IFullTextSearchEngine));
        descriptor.Should().NotBeNull("IFullTextSearchEngine must be registered by the composition root");
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be<PostgresIlikeFullTextSearchEngine>();
    }
}
