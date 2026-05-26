using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.MGov;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Tests.Observability;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// R0112 / TOR CF 14.06 — TDD coverage for
/// <see cref="IMSignClient.VerifySignatureAsync(byte[], MSignVerifyOptions, CancellationToken)"/>.
/// Covers (a) the chain-trust gate against an explicit trust anchor, (b) expiry detection
/// against the injected clock, (c) the "no trust anchor" denial, (d) the malformed-input
/// outcome path that must NOT throw, (e) the revocation-check-skipped flag, and (f) the
/// observability counter wiring.
/// </summary>
[Collection(CnasMeterCollection.Name)]
public class MSignVerifySignatureTests
{
    /// <summary>
    /// Builds a SUT with the supplied clock so tests can drive cert expiry deterministically.
    /// </summary>
    private static (MSignClient client, IAuditService audit, TestClock clock) BuildSut(DateTime? clockUtcNow = null)
    {
        var clock = new TestClock();
        if (clockUtcNow.HasValue)
        {
            clock.UtcNow = DateTime.SpecifyKind(clockUtcNow.Value, DateTimeKind.Utc);
        }
        // The HttpClient is never used by VerifySignatureAsync — a no-op handler is fine.
        var http = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        var opts = Options.Create(new MGovOptions { MSignBaseUrl = "https://msign.example.gov.md" });
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
            Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var sut = new MSignClient(http, opts, NullLogger<MSignClient>.Instance, clock, audit);
        return (sut, audit, clock);
    }

    /// <summary>
    /// Generates a self-signed RSA cert valid for the supplied window and builds a
    /// PKCS#7 SignedData envelope over <paramref name="payload"/> using that cert as
    /// the signer. The returned bytes can be fed directly into
    /// <see cref="IMSignClient.VerifySignatureAsync(byte[], MSignVerifyOptions, CancellationToken)"/>.
    /// </summary>
    /// <param name="payload">Payload to sign (any non-empty byte sequence).</param>
    /// <param name="subjectCn">Common Name to embed in the cert's subject.</param>
    /// <param name="notBefore">UTC start of the certificate's validity window.</param>
    /// <param name="notAfter">UTC end of the certificate's validity window.</param>
    /// <param name="signerCert">Out-parameter — the generated cert (so the test can pass it as the trust anchor).</param>
    private static byte[] BuildSelfSignedSignedData(
        byte[] payload, string subjectCn, DateTimeOffset notBefore, DateTimeOffset notAfter, out X509Certificate2 signerCert)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={subjectCn}, O=CNAS, C=MD",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        // CreateSelfSigned drops the private key on dispose; export+import to get a usable copy.
        using var ephemeral = req.CreateSelfSigned(notBefore, notAfter);
        var pfxBytes = ephemeral.Export(X509ContentType.Pfx, "x");
#pragma warning disable SYSLIB0057 // X509Certificate2(byte[], string) — fine for in-test PFX.
        signerCert = new X509Certificate2(pfxBytes, "x", X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057

        var contentInfo = new ContentInfo(payload);
        var cms = new SignedCms(contentInfo, detached: false);
        var signer = new CmsSigner(signerCert) { IncludeOption = X509IncludeOption.EndCertOnly };
        cms.ComputeSignature(signer);
        return cms.Encode();
    }

    /// <summary>
    /// MeterListener capture that records the <c>result</c> tag value for each
    /// measurement on <c>cnas.msign.verify</c>.
    /// </summary>
    private sealed class VerifyMeterCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<string> _results = new();
        private readonly object _gate = new();

        public IReadOnlyList<string> Results
        {
            get { lock (_gate) return _results.ToArray(); }
        }

        public VerifyMeterCapture()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName
                        && instrument.Name == "cnas.msign.verify")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var t in tags)
                {
                    if (t.Key == "result" && t.Value is string r)
                    {
                        lock (_gate) _results.Add(r);
                    }
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task VerifySignatureAsync_ValidSelfSignedTrustedRoot_IsValidTrue()
    {
        var verificationInstant = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);
        var (sut, audit, _) = BuildSut(verificationInstant);
        var notBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var notAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var signed = BuildSelfSignedSignedData(
            Encoding.UTF8.GetBytes("hello"),
            "Test Signer",
            notBefore,
            notAfter,
            out var cert);
        using (cert)
        {
            var opts = new MSignVerifyOptions(
                TrustedRoots: new[] { cert },
                RequireRevocationCheck: false,
                RequireTimestamp: false);

            var result = await sut.VerifySignatureAsync(signed, opts, CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.IsValid.Should().BeTrue("the self-signed cert is the explicit trust anchor");
            result.Value.ChainTrusted.Should().BeTrue();
            result.Value.NotExpired.Should().BeTrue();
            result.Value.NotRevoked.Should().BeTrue();
            result.Value.RevocationCheckSkipped.Should().BeTrue();
            result.Value.SubjectCn.Should().Be("Test Signer");
            result.Value.IssuerCn.Should().Be("Test Signer");
            result.Value.ValidationErrors.Should().BeEmpty();
            await audit.Received(1).RecordAsync(
                Arg.Is<string>(s => s == "MSIGN.SIGNATURE_VERIFIED"),
                Arg.Is<AuditSeverity>(a => a == AuditSeverity.Sensitive),
                Arg.Is<string>(s => s == "system"),
                Arg.Is<string?>(s => s == "MSign"),
                Arg.Is<long?>(v => v == null),
                Arg.Is<string>(d => d.Contains("\"result\":\"valid\"")),
                Arg.Is<string?>(s => s == null),
                Arg.Is<string?>(s => s == null),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task VerifySignatureAsync_ExpiredCertificate_IsValidFalse_WithNotExpiredFlag()
    {
        // Cert valid Jan-Mar 2025; verification instant is Jun 2026 so the cert is well past expiry.
        var (sut, _, _) = BuildSut(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var notBefore = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var notAfter = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var signed = BuildSelfSignedSignedData(
            Encoding.UTF8.GetBytes("hello"),
            "Expired Signer",
            notBefore,
            notAfter,
            out var cert);
        using (cert)
        {
            var opts = new MSignVerifyOptions(
                TrustedRoots: new[] { cert },
                RequireRevocationCheck: false,
                RequireTimestamp: false);

            var result = await sut.VerifySignatureAsync(signed, opts, CancellationToken.None);

            result.IsSuccess.Should().BeTrue("verification outcome is wrapped in a success Result");
            result.Value.IsValid.Should().BeFalse();
            result.Value.NotExpired.Should().BeFalse();
            result.Value.ValidationErrors.Should().Contain(e => e.StartsWith("NotExpired", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task VerifySignatureAsync_CertNotInTrustedRoots_ChainTrustedFalse()
    {
        var verificationInstant = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);
        var (sut, _, _) = BuildSut(verificationInstant);
        var notBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var notAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var signed = BuildSelfSignedSignedData(
            Encoding.UTF8.GetBytes("hello"),
            "Untrusted Signer",
            notBefore,
            notAfter,
            out var signerCert);

        // Build a DIFFERENT cert to serve as the (only) trust anchor.
        var unrelatedSigned = BuildSelfSignedSignedData(
            Encoding.UTF8.GetBytes("other"),
            "Some Other Authority",
            notBefore,
            notAfter,
            out var unrelatedCert);

        using (signerCert)
        using (unrelatedCert)
        {
            var opts = new MSignVerifyOptions(
                TrustedRoots: new[] { unrelatedCert },
                RequireRevocationCheck: false,
                RequireTimestamp: false);

            var result = await sut.VerifySignatureAsync(signed, opts, CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.IsValid.Should().BeFalse();
            result.Value.ChainTrusted.Should().BeFalse();
            result.Value.ValidationErrors.Should().NotBeEmpty();
            // Unrelated parameter ensures we exercised the helper for both certs.
            unrelatedSigned.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task VerifySignatureAsync_MalformedBytes_IsValidFalse_NoException()
    {
        var (sut, _, _) = BuildSut();
        var opts = new MSignVerifyOptions(
            TrustedRoots: Array.Empty<X509Certificate2>(),
            RequireRevocationCheck: false,
            RequireTimestamp: false);
        var malformed = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };

        var result = await sut.VerifySignatureAsync(malformed, opts, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "verification failure is an outcome, never an exception — see contract on VerifySignatureAsync");
        result.Value.IsValid.Should().BeFalse();
        result.Value.ValidationErrors.Should().NotBeEmpty(
            "the parse error must surface in ValidationErrors so callers can audit it");
    }

    [Fact]
    public async Task VerifySignatureAsync_RevocationCheckDisabled_SkippedFlagTrue_NotRevokedTrue()
    {
        var verificationInstant = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);
        var (sut, _, _) = BuildSut(verificationInstant);
        var notBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var notAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var signed = BuildSelfSignedSignedData(
            Encoding.UTF8.GetBytes("hello"),
            "Test Signer",
            notBefore,
            notAfter,
            out var cert);
        using (cert)
        {
            var opts = new MSignVerifyOptions(
                TrustedRoots: new[] { cert },
                RequireRevocationCheck: false,
                RequireTimestamp: false);

            var result = await sut.VerifySignatureAsync(signed, opts, CancellationToken.None);

            result.Value.RevocationCheckSkipped.Should().BeTrue();
            result.Value.NotRevoked.Should().BeTrue("when revocation check is skipped, NotRevoked defaults to true");
        }
    }

    [Fact]
    public async Task VerifySignatureAsync_InvalidResult_IncrementsInvalidCounter()
    {
        var (sut, _, _) = BuildSut();
        using var capture = new VerifyMeterCapture();
        var opts = new MSignVerifyOptions(
            TrustedRoots: Array.Empty<X509Certificate2>(),
            RequireRevocationCheck: false,
            RequireTimestamp: false);

        await sut.VerifySignatureAsync(new byte[] { 0x42 }, opts, CancellationToken.None);

        capture.Results.Should().ContainSingle().Which.Should().Be("invalid");
    }

    [Fact]
    public async Task VerifySignatureAsync_ValidResult_IncrementsValidCounter()
    {
        var verificationInstant = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);
        var (sut, _, _) = BuildSut(verificationInstant);
        using var capture = new VerifyMeterCapture();
        var notBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var notAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var signed = BuildSelfSignedSignedData(
            Encoding.UTF8.GetBytes("hello"),
            "Counter Signer",
            notBefore,
            notAfter,
            out var cert);
        using (cert)
        {
            var opts = new MSignVerifyOptions(
                TrustedRoots: new[] { cert },
                RequireRevocationCheck: false,
                RequireTimestamp: false);

            await sut.VerifySignatureAsync(signed, opts, CancellationToken.None);

            capture.Results.Should().ContainSingle().Which.Should().Be("valid");
        }
    }
}
