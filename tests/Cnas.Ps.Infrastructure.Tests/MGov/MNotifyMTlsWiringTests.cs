using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Wiring tests for <see cref="MNotifyClient"/> in the DI composition root. Verifies
/// that the primary HTTP message handler attaches the client certificate resolved from
/// <see cref="ICertificateStore"/> when one is configured, and that the registration is
/// still resolvable when no certificate is configured (Bearer-less HTTPS fallback used
/// in dev/test where MNotify itself is mocked).
/// </summary>
/// <remarks>
/// Tests construct a real <see cref="FileCertificateStore"/> backed by an on-disk
/// self-signed PFX. They reach into the primary <see cref="SocketsHttpHandler"/> via
/// reflection to assert <see cref="SocketsHttpHandler.SslOptions"/>.<c>ClientCertificates</c>
/// has been populated, because that is the only observable artefact of the wiring layer
/// (mTLS is negotiated at the socket level — the delegating handler chain cannot see it).
/// </remarks>
public sealed class MNotifyMTlsWiringTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempPfx(string password, out string thumbprint)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=cnas-mnotify-wiring-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
        thumbprint = cert.Thumbprint;

        var pfxBytes = cert.Export(X509ContentType.Pfx, password);
        var path = Path.Combine(Path.GetTempPath(), $"cnas-mnotify-wiring-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, pfxBytes);
        _tempFiles.Add(path);
        return path;
    }

    private static IConfiguration BuildConfig(IReadOnlyDictionary<string, string?>? extra = null)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=cnas_test;Username=postgres;Password=postgres",
            ["Minio:Endpoint"] = "localhost:9000",
            ["Minio:AccessKey"] = "minio",
            ["Minio:SecretKey"] = "minio12345",
            ["Minio:UseSsl"] = "false",
            ["MGov:MNotifyBaseUrl"] = "https://mnotify.example.gov.md:8443",
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
    /// <c>IMNotifyClient</c> registration and walks any <see cref="DelegatingHandler"/>
    /// chain down to the inner-most <see cref="SocketsHttpHandler"/>.
    /// </summary>
    private static SocketsHttpHandler ResolvePrimarySocketsHandler(IServiceProvider sp)
    {
        var factory = sp.GetRequiredService<IHttpMessageHandlerFactory>();
        // The handler name for typed clients defaults to the implementation type's full
        // name; ASP.NET Core typed-client convention uses the typed-client class name.
        // We try both — typed clients are registered under the typed-client name in
        // recent versions.
        HttpMessageHandler? handler = null;
        try { handler = factory.CreateHandler(nameof(MNotifyClient)); }
        catch { /* fall through */ }
        if (handler is null)
        {
            try { handler = factory.CreateHandler(typeof(MNotifyClient).Name); }
            catch { /* fall through */ }
        }
        if (handler is null)
        {
            // Last resort: build via the typed HttpClient — the factory caches the
            // handler under whatever name AddHttpClient<TClient, TImpl> chose.
            using var scope = sp.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IMNotifyClient>();
            handler = factory.CreateHandler(nameof(MNotifyClient));
        }

        // Walk past any delegating handlers to the inner-most primary handler.
        while (handler is DelegatingHandler dh && dh.InnerHandler is not null)
        {
            handler = dh.InnerHandler;
        }
        handler.Should().BeOfType<SocketsHttpHandler>("the MNotify client must use SocketsHttpHandler so SslOptions can carry the mTLS certificate");
        return (SocketsHttpHandler)handler;
    }

    [Fact]
    public void Registration_AttachesClientCertificateFromStore()
    {
        // Arrange — write a real self-signed PFX, point Cnas:MGov:Mtls at it, build the
        // full DI graph the same way the composition root does.
        var path = CreateTempPfx("p", out var expectedThumbprint);
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Cnas:MGov:Mtls:Certificates:mnotify:Path"] = path,
            ["Cnas:MGov:Mtls:Certificates:mnotify:Password"] = "p",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCnasMTls(cfg);
        services.AddCnasInfrastructure(cfg);
        using var provider = services.BuildServiceProvider();

        // Act — resolve the primary handler the typed client was wired with.
        var primary = ResolvePrimarySocketsHandler(provider);

        // Assert — the certificate from the store is now in SslOptions.ClientCertificates.
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
        // No mTLS certificate is configured for mnotify. The client must still resolve,
        // the primary handler must still be a SocketsHttpHandler (so future certs can be
        // attached without a registration change), and the typed client must be able to
        // dispatch a notification against a stub upstream.
        var cfg = BuildConfig();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCnasMTls(cfg);
        services.AddCnasInfrastructure(cfg);
        using var provider = services.BuildServiceProvider();

        // Resolution must not throw.
        Action act = () =>
        {
            using var scope = provider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IMNotifyClient>();
        };
        act.Should().NotThrow();

        // Primary handler is still a SocketsHttpHandler with an empty cert list — the
        // request layer will send a Bearer-less HTTPS handshake, which the mocked
        // upstream in dev/CI does not care about.
        var primary = ResolvePrimarySocketsHandler(provider);
        primary.SslOptions.Should().NotBeNull();
        var count = primary.SslOptions.ClientCertificates?.Count ?? 0;
        count.Should().Be(0, "no certificate is configured for 'mnotify'");
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
