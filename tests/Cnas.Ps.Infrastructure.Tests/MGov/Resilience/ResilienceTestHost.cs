using System;
using System.Net.Http;
using Cnas.Ps.Infrastructure.MGov;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.MGov.Resilience;

/// <summary>
/// Test harness that wires an <see cref="HttpClient"/> registered through
/// <c>AddHttpClient</c> + <see cref="MGovResilienceExtensions.AddMGovResilience"/>, with
/// <see cref="ResilienceTestHandler"/> swapped in as the primary handler. Hides the
/// boilerplate of building a service collection per test.
/// </summary>
/// <remarks>
/// Each <c>BuildClient</c> call produces an isolated <see cref="ServiceProvider"/> so
/// breaker state cannot leak between tests in the same class — Polly v8 pipelines are
/// singletons inside their container, and a leaked breaker would make assertions
/// non-deterministic. The harness disposes the container when itself disposed.
/// </remarks>
internal sealed class ResilienceTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    /// <summary>The handler injected as the primary HTTP message handler.</summary>
    public ResilienceTestHandler Handler { get; }

    /// <summary>The typed-client name (resolves the same name through IHttpClientFactory).</summary>
    public string ClientName { get; }

    private ResilienceTestHost(ServiceProvider provider, ResilienceTestHandler handler, string clientName)
    {
        _provider = provider;
        Handler = handler;
        ClientName = clientName;
    }

    /// <summary>
    /// Builds an isolated host: one <see cref="ResilienceTestHandler"/> behind one
    /// resilience pipeline. <paramref name="configure"/> is invoked on the
    /// <see cref="MGovResilienceOptions"/> bound for this host so each test picks its
    /// own retry / breaker knobs.
    /// </summary>
    /// <param name="serviceName">Stable service key the pipeline is registered for.</param>
    /// <param name="configure">Mutates the bound options.</param>
    /// <returns>A disposable host whose <see cref="GetClient"/> returns the typed client.</returns>
    public static ResilienceTestHost Build(string serviceName, Action<MGovResilienceOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<MGovResilienceOptions>(configure);

        var handler = new ResilienceTestHandler();
        services.AddHttpClient(serviceName)
            .ConfigurePrimaryHttpMessageHandler(_ => handler)
            .AddMGovResilience(serviceName);

        var sp = services.BuildServiceProvider();
        return new ResilienceTestHost(sp, handler, serviceName);
    }

    /// <summary>
    /// Builds a host with two pipelines so tests that need per-client overrides can
    /// fire requests at each in isolation.
    /// </summary>
    public static ResilienceTwoClientHost BuildTwo(
        string serviceA,
        string serviceB,
        Action<MGovResilienceOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<MGovResilienceOptions>(configure);

        var handlerA = new ResilienceTestHandler();
        var handlerB = new ResilienceTestHandler();
        services.AddHttpClient(serviceA)
            .ConfigurePrimaryHttpMessageHandler(_ => handlerA)
            .AddMGovResilience(serviceA);
        services.AddHttpClient(serviceB)
            .ConfigurePrimaryHttpMessageHandler(_ => handlerB)
            .AddMGovResilience(serviceB);

        var sp = services.BuildServiceProvider();
        return new ResilienceTwoClientHost(sp, handlerA, handlerB, serviceA, serviceB);
    }

    /// <summary>Resolves the typed <see cref="HttpClient"/> for this host's service.</summary>
    public HttpClient GetClient()
    {
        var factory = _provider.GetRequiredService<IHttpClientFactory>();
        return factory.CreateClient(ClientName);
    }

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();
}

/// <summary>Two-client variant of <see cref="ResilienceTestHost"/>.</summary>
internal sealed class ResilienceTwoClientHost : IDisposable
{
    private readonly ServiceProvider _provider;
    public ResilienceTestHandler HandlerA { get; }
    public ResilienceTestHandler HandlerB { get; }
    public string ClientNameA { get; }
    public string ClientNameB { get; }

    internal ResilienceTwoClientHost(
        ServiceProvider provider,
        ResilienceTestHandler handlerA,
        ResilienceTestHandler handlerB,
        string clientNameA,
        string clientNameB)
    {
        _provider = provider;
        HandlerA = handlerA;
        HandlerB = handlerB;
        ClientNameA = clientNameA;
        ClientNameB = clientNameB;
    }

    public HttpClient GetClientA()
        => _provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientNameA);
    public HttpClient GetClientB()
        => _provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientNameB);

    public void Dispose() => _provider.Dispose();
}
