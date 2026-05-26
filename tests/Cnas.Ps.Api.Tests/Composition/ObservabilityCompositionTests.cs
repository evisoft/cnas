using System.Collections.Generic;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Cnas.Ps.Api.Tests.Composition;

/// <summary>
/// Tests for <see cref="ApiCompositionRoot.AddCnasObservability(IServiceCollection, IConfiguration)"/>.
/// Verifies that the OpenTelemetry SDK is wired into DI safely whether or not the
/// OTLP endpoint is configured, that options bind from <c>Cnas:Observability:*</c>,
/// that the call is idempotent, and that the <see cref="CnasTelemetry"/> singletons
/// expose the documented names so dashboards can pin against them.
/// </summary>
public sealed class ObservabilityCompositionTests
{
    /// <summary>Builds an <see cref="IConfiguration"/> from a flat in-memory dictionary.</summary>
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>Adds the minimal infrastructure OTel needs to resolve providers (logging).</summary>
    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        return services;
    }

    [Fact]
    public void AddCnasObservability_NoOtlpEndpoint_ResolvesProviderWithoutExporter()
    {
        var services = NewServices();
        var config = BuildConfig(new Dictionary<string, string?>());

        services.AddCnasObservability(config);

        using var sp = services.BuildServiceProvider();
        var tracer = sp.GetService<TracerProvider>();
        var meter = sp.GetService<MeterProvider>();

        tracer.Should().NotBeNull();
        meter.Should().NotBeNull();
    }

    [Fact]
    public void AddCnasObservability_WithOtlpEndpoint_ResolvesProviderSuccessfully()
    {
        var services = NewServices();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Cnas:Observability:OtlpEndpoint"] = "http://otel-collector:4317",
            ["Cnas:Observability:ServiceName"] = "cnas-ps-api",
        });

        services.AddCnasObservability(config);

        using var sp = services.BuildServiceProvider();
        var tracer = sp.GetService<TracerProvider>();
        var meter = sp.GetService<MeterProvider>();

        tracer.Should().NotBeNull();
        meter.Should().NotBeNull();
    }

    [Fact]
    public void AddCnasObservability_BindsServiceNameFromConfig()
    {
        var services = NewServices();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Cnas:Observability:ServiceName"] = "cnas-test",
            ["Cnas:Observability:ServiceVersion"] = "9.9.9",
            ["Cnas:Observability:Environment"] = "ci",
        });

        services.AddCnasObservability(config);

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<ObservabilityOptions>>().Value;

        opts.ServiceName.Should().Be("cnas-test");
        opts.ServiceVersion.Should().Be("9.9.9");
        opts.Environment.Should().Be("ci");
    }

    [Fact]
    public void AddCnasObservability_EnableConsoleExporter_DoesNotThrow()
    {
        var services = NewServices();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Cnas:Observability:EnableConsoleExporter"] = "true",
        });

        var act = () =>
        {
            services.AddCnasObservability(config);
            using var sp = services.BuildServiceProvider();
            _ = sp.GetService<TracerProvider>();
            _ = sp.GetService<MeterProvider>();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void AddCnasObservability_TwiceIdempotent_DoesNotThrow()
    {
        var services = NewServices();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Cnas:Observability:OtlpEndpoint"] = "http://otel-collector:4317",
        });

        var act = () =>
        {
            services.AddCnasObservability(config);
            services.AddCnasObservability(config);
            using var sp = services.BuildServiceProvider();
            _ = sp.GetService<TracerProvider>();
            _ = sp.GetService<MeterProvider>();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void CnasTelemetry_ActivitySourceAndMeter_AreSingletons()
    {
        CnasTelemetry.ActivitySource.Name.Should().Be("Cnas.Ps.Api");
        CnasTelemetry.Meter.Name.Should().Be("Cnas.Ps.Api");
        CnasTelemetry.DossiersAcceptedForExamination.Name.Should().Be("cnas.dossiers.accepted_for_examination");
        CnasTelemetry.DossiersApproved.Name.Should().Be("cnas.dossiers.approved");
        CnasTelemetry.DossiersRejected.Name.Should().Be("cnas.dossiers.rejected");
        CnasTelemetry.DocumentExaminationLatencyMs.Name.Should().Be("cnas.documents.examination_latency_ms");
    }

    [Fact]
    public void AddCnasObservability_DefaultsApplied_WhenNoConfigPresent()
    {
        var services = NewServices();
        var config = BuildConfig(new Dictionary<string, string?>());

        services.AddCnasObservability(config);

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<ObservabilityOptions>>().Value;

        opts.ServiceName.Should().Be("cnas-ps-api");
        opts.ServiceVersion.Should().Be("1.0.0");
        opts.Environment.Should().Be("development");
        opts.OtlpEndpoint.Should().BeNull();
        opts.EnableConsoleExporter.Should().BeFalse();
    }
}
