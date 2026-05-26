using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Wiring tests for <see cref="MCabinetPublisher"/> in the DI composition root. Verifies
/// that the primary HTTP message handler attaches the client certificate resolved from
/// <see cref="ICertificateStore"/> when one is configured under the <c>mcabinet</c> key,
/// and that the registration is still resolvable when no certificate is configured (the
/// Bearer-less HTTPS fallback used in dev/test where MCabinet is mocked or unreachable).
/// </summary>
/// <remarks>
/// Mirrors <see cref="MSignMTlsWiringTests"/> — all MGov clients share the same primary
/// handler factory (<c>BuildMGovPrimaryHandler</c>) and the same observable artefact
/// (<see cref="SocketsHttpHandler.SslOptions"/>.<c>ClientCertificates</c>). The MCabinet
/// publisher is registered by <see cref="InfrastructureServiceCollectionExtensions.AddCnasMCabinet"/>
/// rather than <c>AddCnasInfrastructure</c>, so the tests call the dedicated extension.
/// </remarks>
public sealed class MCabinetMTlsWiringTests : IDisposable
{
    /// <summary>Temp PFX files created by the test that must be deleted on tear-down.</summary>
    private readonly List<string> _tempFiles = new();

    /// <summary>
    /// Writes a brand-new self-signed PFX to a unique temp path, returns the path, and
    /// emits the SHA-1 thumbprint via <paramref name="thumbprint"/> so the caller can
    /// assert the certificate landed in <see cref="SocketsHttpHandler.SslOptions"/>.
    /// </summary>
    /// <param name="password">PKCS#12 export password (also used at load time).</param>
    /// <param name="thumbprint">Receives the SHA-1 thumbprint for downstream assertion.</param>
    /// <returns>Absolute filesystem path to the newly written PFX.</returns>
    private string CreateTempPfx(string password, out string thumbprint)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=cnas-mcabinet-wiring-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
        thumbprint = cert.Thumbprint;

        var pfxBytes = cert.Export(X509ContentType.Pfx, password);
        var path = Path.Combine(Path.GetTempPath(), $"cnas-mcabinet-wiring-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, pfxBytes);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Standard in-memory configuration for the MCabinet DI graph.</summary>
    /// <param name="extra">Optional additional key/value pairs layered on top of the base config.</param>
    private static IConfiguration BuildConfig(IReadOnlyDictionary<string, string?>? extra = null)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cnas:MCabinet:BaseUrl"] = "https://mcabinet.example.gov.md",
        };
        if (extra is not null)
        {
            foreach (var kv in extra)
            {
                dict[kv.Key] = kv.Value;
            }
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    /// <summary>
    /// Resolves the configured primary <see cref="HttpMessageHandler"/> for the typed
    /// <c>IMCabinetPublisher</c> registration and walks any <see cref="DelegatingHandler"/>
    /// chain down to the inner-most <see cref="SocketsHttpHandler"/>.
    /// </summary>
    /// <param name="sp">Built service provider holding the registered factory.</param>
    private static SocketsHttpHandler ResolvePrimarySocketsHandler(IServiceProvider sp)
    {
        var factory = sp.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler? handler = null;
        try { handler = factory.CreateHandler(nameof(MCabinetPublisher)); }
        catch { /* fall through */ }
        if (handler is null)
        {
            using var scope = sp.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IMCabinetPublisher>();
            handler = factory.CreateHandler(nameof(MCabinetPublisher));
        }

        while (handler is DelegatingHandler dh && dh.InnerHandler is not null)
        {
            handler = dh.InnerHandler;
        }
        handler.Should().BeOfType<SocketsHttpHandler>(
            "the MCabinet publisher must use SocketsHttpHandler so SslOptions can carry the mTLS certificate");
        return (SocketsHttpHandler)handler;
    }

    [Fact]
    public void Registration_AttachesClientCertificateFromStore()
    {
        var path = CreateTempPfx("p", out var expectedThumbprint);
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Cnas:MGov:Mtls:Certificates:mcabinet:Path"] = path,
            ["Cnas:MGov:Mtls:Certificates:mcabinet:Password"] = "p",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCnasMTls(cfg);
        services.AddCnasMCabinet(cfg);
        using var provider = services.BuildServiceProvider();

        var primary = ResolvePrimarySocketsHandler(provider);

        primary.SslOptions.Should().NotBeNull();
        primary.SslOptions.ClientCertificates.Should().NotBeNull();
        primary.SslOptions.ClientCertificates!.Count.Should().BeGreaterThan(0,
            "the wiring layer must attach the per-service mTLS certificate to SocketsHttpHandler.SslOptions");
        primary.SslOptions.ClientCertificates
            .Cast<X509Certificate2>()
            .Should().Contain(c => c.Thumbprint == expectedThumbprint);
    }

    [Fact]
    public async Task Registration_NoCertConfigured_DoesNotFail()
    {
        var cfg = BuildConfig();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCnasMTls(cfg);
        services.AddCnasMCabinet(cfg);
        using var provider = services.BuildServiceProvider();

        Action act = () =>
        {
            using var scope = provider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IMCabinetPublisher>();
        };
        act.Should().NotThrow();

        var primary = ResolvePrimarySocketsHandler(provider);
        primary.SslOptions.Should().NotBeNull();
        var count = primary.SslOptions.ClientCertificates?.Count ?? 0;
        count.Should().Be(0, "no certificate is configured for 'mcabinet'");
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try
            {
                if (File.Exists(f))
                {
                    File.Delete(f);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
