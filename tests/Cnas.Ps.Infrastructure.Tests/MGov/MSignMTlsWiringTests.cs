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
/// Wiring tests for <see cref="MSignClient"/> in the DI composition root. Verifies that
/// the primary HTTP message handler attaches the client certificate resolved from
/// <see cref="ICertificateStore"/> when one is configured (the MSign refactor moves the
/// service off the Bearer-token model and onto mTLS), and that the registration is still
/// resolvable when no certificate is configured (Bearer-less HTTPS fallback used in
/// dev/test where MSign is mocked or unreachable).
/// </summary>
/// <remarks>
/// Mirrors <see cref="MNotifyMTlsWiringTests"/> — both clients share the same primary
/// handler factory (<c>BuildMGovPrimaryHandler</c>) and the same observable artefact
/// (<see cref="SocketsHttpHandler.SslOptions"/>.<c>ClientCertificates</c>).
/// </remarks>
public sealed class MSignMTlsWiringTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    /// <summary>
    /// Writes a brand-new self-signed PFX to a unique temp path, returns the path, and
    /// emits the SHA-1 thumbprint via <paramref name="thumbprint"/> so the caller can
    /// assert the certificate landed in <see cref="SocketsHttpHandler.SslOptions"/>.
    /// </summary>
    private string CreateTempPfx(string password, out string thumbprint)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=cnas-msign-wiring-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
        thumbprint = cert.Thumbprint;

        var pfxBytes = cert.Export(X509ContentType.Pfx, password);
        var path = Path.Combine(Path.GetTempPath(), $"cnas-msign-wiring-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, pfxBytes);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Standard in-memory configuration for an Infrastructure DI graph.</summary>
    private static IConfiguration BuildConfig(IReadOnlyDictionary<string, string?>? extra = null)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=cnas_test;Username=postgres;Password=postgres",
            ["Minio:Endpoint"] = "localhost:9000",
            ["Minio:AccessKey"] = "minio",
            ["Minio:SecretKey"] = "minio12345",
            ["Minio:UseSsl"] = "false",
            ["MGov:MSignBaseUrl"] = "https://msign.example.gov.md:8443",
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
    /// <c>IMSignClient</c> registration and walks any <see cref="DelegatingHandler"/>
    /// chain down to the inner-most <see cref="SocketsHttpHandler"/>.
    /// </summary>
    private static SocketsHttpHandler ResolvePrimarySocketsHandler(IServiceProvider sp)
    {
        var factory = sp.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler? handler = null;
        try { handler = factory.CreateHandler(nameof(MSignClient)); }
        catch { /* fall through */ }
        if (handler is null)
        {
            using var scope = sp.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IMSignClient>();
            handler = factory.CreateHandler(nameof(MSignClient));
        }

        while (handler is DelegatingHandler dh && dh.InnerHandler is not null)
        {
            handler = dh.InnerHandler;
        }
        handler.Should().BeOfType<SocketsHttpHandler>("the MSign client must use SocketsHttpHandler so SslOptions can carry the mTLS certificate");
        return (SocketsHttpHandler)handler;
    }

    [Fact]
    public void Registration_AttachesClientCertificateFromStore()
    {
        var path = CreateTempPfx("p", out var expectedThumbprint);
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Cnas:MGov:Mtls:Certificates:msign:Path"] = path,
            ["Cnas:MGov:Mtls:Certificates:msign:Password"] = "p",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCnasMTls(cfg);
        services.AddCnasInfrastructure(cfg);
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
        services.AddCnasInfrastructure(cfg);
        using var provider = services.BuildServiceProvider();

        Action act = () =>
        {
            using var scope = provider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IMSignClient>();
        };
        act.Should().NotThrow();

        var primary = ResolvePrimarySocketsHandler(provider);
        primary.SslOptions.Should().NotBeNull();
        var count = primary.SslOptions.ClientCertificates?.Count ?? 0;
        count.Should().Be(0, "no certificate is configured for 'msign'");
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
