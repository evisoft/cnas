using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Unit tests for <see cref="FileCertificateStore"/>. Covers happy-path PFX loading, wrong
/// password handling, optional thumbprint pinning, missing-service handling, and the
/// process-lifetime cache.
/// </summary>
/// <remarks>
/// Tests generate self-signed certificates in-memory via <see cref="CertificateRequest"/>
/// and write them to temp PFX files so the store has something realistic to load from
/// disk. All temp files are cleaned up via <see cref="IDisposable"/> regardless of test
/// outcome.
/// </remarks>
public sealed class FileCertificateStoreTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempPfx(string password, out string thumbprint)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=cnas-mtls-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
        thumbprint = cert.Thumbprint;

        var pfxBytes = cert.Export(X509ContentType.Pfx, password);
        var path = Path.Combine(Path.GetTempPath(), $"cnas-mtls-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, pfxBytes);
        _tempFiles.Add(path);
        return path;
    }

    private static FileCertificateStore Build(MTlsOptions options) =>
        new(Options.Create(options), NullLogger<FileCertificateStore>.Instance);

    [Fact]
    public void GetCertificate_ServiceNotConfigured_ReturnsFailureCertificateNotConfigured()
    {
        var store = Build(new MTlsOptions());

        var result = store.GetCertificate("mnotify");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CertificateNotConfigured);
    }

    [Fact]
    public void TryGetCertificate_ServiceNotConfigured_ReturnsSuccessWithNull()
    {
        var store = Build(new MTlsOptions());

        var result = store.TryGetCertificate("mnotify");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void GetCertificate_ValidPfx_ReturnsLoadedCertificate()
    {
        var path = CreateTempPfx("p@ss", out var expectedThumbprint);
        var opts = new MTlsOptions();
        opts.Certificates["mnotify"] = new MTlsCertificateOptions(path, "p@ss", Thumbprint: null);
        var store = Build(opts);

        var result = store.GetCertificate("mnotify");

        result.IsSuccess.Should().BeTrue();
        result.Value.Thumbprint.Should().Be(expectedThumbprint);
    }

    [Fact]
    public void GetCertificate_PasswordWrong_ReturnsCertificateLoadFailed()
    {
        var path = CreateTempPfx("correct", out _);
        var opts = new MTlsOptions();
        opts.Certificates["mnotify"] = new MTlsCertificateOptions(path, "wrong", Thumbprint: null);
        var store = Build(opts);

        var result = store.GetCertificate("mnotify");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CertificateLoadFailed);
    }

    [Fact]
    public void GetCertificate_ThumbprintConfigured_AndMatches_ReturnsSuccess()
    {
        var path = CreateTempPfx("p", out var thumbprint);
        var opts = new MTlsOptions();
        // Lowercase to prove case-insensitive comparison
        opts.Certificates["mnotify"] = new MTlsCertificateOptions(path, "p", thumbprint.ToLowerInvariant());
        var store = Build(opts);

        var result = store.GetCertificate("mnotify");

        result.IsSuccess.Should().BeTrue();
        result.Value.Thumbprint.Should().Be(thumbprint);
    }

    [Fact]
    public void GetCertificate_ThumbprintConfigured_AndMismatches_ReturnsThumbprintMismatchFailure()
    {
        var path = CreateTempPfx("p", out _);
        var opts = new MTlsOptions();
        opts.Certificates["mnotify"] = new MTlsCertificateOptions(
            path,
            "p",
            // 40-char hex string that obviously won't match the just-generated cert
            Thumbprint: "0000000000000000000000000000000000000000");
        var store = Build(opts);

        var result = store.GetCertificate("mnotify");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CertificateThumbprintMismatch);
    }

    [Fact]
    public void GetCertificate_CachedAfterFirstLoad_DoesNotReReadFile()
    {
        var path = CreateTempPfx("p", out var expectedThumbprint);
        var opts = new MTlsOptions();
        opts.Certificates["mnotify"] = new MTlsCertificateOptions(path, "p", Thumbprint: null);
        var store = Build(opts);

        var first = store.GetCertificate("mnotify");
        first.IsSuccess.Should().BeTrue();

        // Delete the file; the second call must still succeed by virtue of the cache.
        File.Delete(path);
        _tempFiles.Remove(path);

        var second = store.GetCertificate("mnotify");
        second.IsSuccess.Should().BeTrue();
        second.Value.Thumbprint.Should().Be(expectedThumbprint);
    }

    [Fact]
    public void GetCertificate_ServiceNameLookup_IsCaseInsensitive()
    {
        var path = CreateTempPfx("p", out _);
        var opts = new MTlsOptions();
        opts.Certificates["MNotify"] = new MTlsCertificateOptions(path, "p", Thumbprint: null);
        var store = Build(opts);

        var result = store.GetCertificate("mnotify");

        result.IsSuccess.Should().BeTrue();
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
                // Best-effort cleanup — never fail the test on a leftover temp file.
            }
        }
    }
}
