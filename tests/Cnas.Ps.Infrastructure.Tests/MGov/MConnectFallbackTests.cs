using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
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
/// R0104 / TOR CF 14.03 — TDD coverage for the MConnect partner-direct fallback path.
/// Verifies that the new <c>CallAsync(serviceCode, requestJson, fallback, ct)</c> overload
/// (a) does NOT touch the fallback when MConnect succeeds, (b) DOES touch the fallback
/// when MConnect fails for an availability reason (timeout, 5xx, network), (c) leaves
/// the fallback alone for partner-business outcomes (4xx), (d) respects both
/// configuration gates (<c>AllowFallback</c> + <c>PartnerHasNda</c>), and (e) increments
/// the appropriate metrics + audits each transition.
/// </summary>
[Collection(CnasMeterCollection.Name)]
public class MConnectFallbackTests
{
    /// <summary>Stable base URL used in every test.</summary>
    private const string BaseUrl = "https://mconnect.example.gov.md";

    /// <summary>Helper — wraps a body fragment in a SOAP 1.2 envelope.</summary>
    private static string SoapEnvelope(string innerXml) =>
        "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\">"
        + "<soap:Body>" + innerXml + "</soap:Body>"
        + "</soap:Envelope>";

    /// <summary>Helper — builds a happy-path CallResponse envelope carrying the supplied JSON.</summary>
    private static string CallResponseEnvelope(string responseJson) =>
        SoapEnvelope(
            "<CallResponse xmlns=\"http://egov.md/MConnect\">"
            + "<ResponseJson>" + System.Security.SecurityElement.Escape(responseJson) + "</ResponseJson>"
            + "</CallResponse>");

    /// <summary>
    /// Builds an <see cref="MConnectClient"/> wired to a <see cref="CapturingHandler"/>,
    /// the supplied <see cref="MGovOptions"/>, and an optional audit-service substitute.
    /// </summary>
    private static (MConnectClient client, CapturingHandler handler, IAuditService audit) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        bool allowFallback,
        IReadOnlyDictionary<string, bool>? ndaByCode = null)
    {
        var handler = new CapturingHandler(respond);
        var http = new HttpClient(handler);
        var opts = Options.Create(new MGovOptions
        {
            MConnectBaseUrl = BaseUrl,
            AllowFallback = allowFallback,
            PartnerHasNdaByCode = ndaByCode is null
                ? new Dictionary<string, bool>(StringComparer.Ordinal)
                : new Dictionary<string, bool>(ndaByCode, StringComparer.Ordinal),
        });
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
            Arg.Any<string>(),
            Arg.Any<AuditSeverity>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>()).Returns(Result.Success());
        var client = new MConnectClient(
            http,
            opts,
            NullLogger<MConnectClient>.Instance,
            new TestClock(),
            audit);
        return (client, handler, audit);
    }

    /// <summary>
    /// MeterListener-based capture that records (partner, reason) tag tuples for every
    /// measurement on <c>cnas.mconnect.fallback_invoked</c> and the partner tag for
    /// <c>cnas.mconnect.fallback_failed</c>. Disposes the listener at end-of-test.
    /// </summary>
    private sealed class FallbackMeterCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<(string Partner, string Reason)> _invoked = new();
        private readonly List<string> _failed = new();
        private readonly object _gate = new();

        public IReadOnlyList<(string Partner, string Reason)> Invoked
        {
            get { lock (_gate) return _invoked.ToArray(); }
        }
        public IReadOnlyList<string> Failed
        {
            get { lock (_gate) return _failed.ToArray(); }
        }

        public FallbackMeterCapture()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName
                        && (instrument.Name == "cnas.mconnect.fallback_invoked"
                            || instrument.Name == "cnas.mconnect.fallback_failed"))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                string? partner = null;
                string? reason = null;
                foreach (var t in tags)
                {
                    if (t.Key == "partner" && t.Value is string p) partner = p;
                    if (t.Key == "reason" && t.Value is string r) reason = r;
                }
                lock (_gate)
                {
                    if (instrument.Name == "cnas.mconnect.fallback_invoked" && partner is not null && reason is not null)
                    {
                        _invoked.Add((partner, reason));
                    }
                    if (instrument.Name == "cnas.mconnect.fallback_failed" && partner is not null)
                    {
                        _failed.Add(partner);
                    }
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task CallAsync_HappyPath_FallbackNotInvoked_NoAuditNoCounter()
    {
        var (sut, _, audit) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CallResponseEnvelope("{\"ok\":true}"), Encoding.UTF8, "text/xml"),
        }, allowFallback: true, ndaByCode: new Dictionary<string, bool> { ["RSP"] = true });
        using var capture = new FallbackMeterCapture();
        var fallbackCalled = 0;
        var fallback = new MConnectFallback(
            PartnerSystemCode: "RSP",
            DirectInvoke: _ =>
            {
                fallbackCalled++;
                return Task.FromResult(Result<string>.Success("{\"fallback\":true}"));
            },
            PartnerHasNda: true);

        var result = await sut.CallAsync("RSP.GetPerson", "{}", fallback, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("{\"ok\":true}");
        fallbackCalled.Should().Be(0, "MConnect succeeded; the fallback closure must not be invoked");
        capture.Invoked.Should().BeEmpty();
        await audit.DidNotReceive().RecordAsync(
            Arg.Is<string>(s => s == "MCONNECT.FALLBACK_INVOKED"),
            Arg.Any<AuditSeverity>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CallAsync_Timeout_FallbackAllowed_InvokesFallback_AndAudits()
    {
        var (sut, _, audit) = Build(_ => throw new TaskCanceledException(),
            allowFallback: true,
            ndaByCode: new Dictionary<string, bool> { ["RSP"] = true });
        using var capture = new FallbackMeterCapture();
        var fallbackCalled = 0;
        var fallback = new MConnectFallback(
            PartnerSystemCode: "RSP",
            DirectInvoke: _ =>
            {
                fallbackCalled++;
                return Task.FromResult(Result<string>.Success("{\"fallback\":true}"));
            },
            PartnerHasNda: true);

        var result = await sut.CallAsync("RSP.GetPerson", "{}", fallback, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the fallback succeeded");
        result.Value.Should().Be("{\"fallback\":true}");
        fallbackCalled.Should().Be(1);
        capture.Invoked.Should().ContainSingle().Which.Should().Be(("RSP", "Timeout"));
        await audit.Received(1).RecordAsync(
            Arg.Is<string>(s => s == "MCONNECT.FALLBACK_INVOKED"),
            Arg.Is<AuditSeverity>(a => a == AuditSeverity.Notice),
            Arg.Is<string>(s => s == "system"),
            Arg.Is<string?>(s => s == "MConnect"),
            Arg.Is<long?>(v => v == null),
            Arg.Is<string>(d => d.Contains("\"partner\":\"RSP\"") && d.Contains("\"reason\":\"Timeout\"")),
            Arg.Is<string?>(s => s == null),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CallAsync_Http5xx_FallbackAllowed_InvokesFallback()
    {
        var (sut, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream-down"),
        }, allowFallback: true, ndaByCode: new Dictionary<string, bool> { ["SFS"] = true });
        using var capture = new FallbackMeterCapture();
        var fallbackCalled = 0;
        var fallback = new MConnectFallback(
            PartnerSystemCode: "SFS",
            DirectInvoke: _ =>
            {
                fallbackCalled++;
                return Task.FromResult(Result<string>.Success("{\"direct\":true}"));
            },
            PartnerHasNda: true);

        var result = await sut.CallAsync("SFS.GetTaxStatus", "{}", fallback, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fallbackCalled.Should().Be(1);
        capture.Invoked.Should().ContainSingle().Which.Should().Be(("SFS", "Http5xx"));
    }

    [Fact]
    public async Task CallAsync_Http404_FallbackNotInvoked_BusinessOutcomePassThrough()
    {
        var (sut, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("person-not-found"),
        }, allowFallback: true, ndaByCode: new Dictionary<string, bool> { ["RSP"] = true });
        using var capture = new FallbackMeterCapture();
        var fallbackCalled = 0;
        var fallback = new MConnectFallback(
            PartnerSystemCode: "RSP",
            DirectInvoke: _ =>
            {
                fallbackCalled++;
                return Task.FromResult(Result<string>.Success("{\"fallback\":true}"));
            },
            PartnerHasNda: true);

        var result = await sut.CallAsync("RSP.GetPerson", "{}", fallback, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
        fallbackCalled.Should().Be(0,
            "404 is a partner business outcome — falling through would reproduce the same result");
        capture.Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task CallAsync_Timeout_AllowFallbackFalse_NoFallback()
    {
        var (sut, _, _) = Build(_ => throw new TaskCanceledException(),
            allowFallback: false,
            ndaByCode: new Dictionary<string, bool> { ["RSP"] = true });
        using var capture = new FallbackMeterCapture();
        var fallbackCalled = 0;
        var fallback = new MConnectFallback(
            PartnerSystemCode: "RSP",
            DirectInvoke: _ =>
            {
                fallbackCalled++;
                return Task.FromResult(Result<string>.Success("{\"fallback\":true}"));
            },
            PartnerHasNda: true);

        var result = await sut.CallAsync("RSP.GetPerson", "{}", fallback, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
        fallbackCalled.Should().Be(0);
        capture.Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task CallAsync_Timeout_PartnerHasNdaFalse_NoFallback()
    {
        var (sut, _, _) = Build(_ => throw new TaskCanceledException(),
            allowFallback: true,
            ndaByCode: new Dictionary<string, bool> { ["RSP"] = false });
        using var capture = new FallbackMeterCapture();
        var fallbackCalled = 0;
        var fallback = new MConnectFallback(
            PartnerSystemCode: "RSP",
            DirectInvoke: _ =>
            {
                fallbackCalled++;
                return Task.FromResult(Result<string>.Success("{\"fallback\":true}"));
            },
            PartnerHasNda: false);

        var result = await sut.CallAsync("RSP.GetPerson", "{}", fallback, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
        fallbackCalled.Should().Be(0,
            "the partner has not signed an NDA permitting direct API calls — the fallback must not be invoked");
        capture.Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task CallAsync_Timeout_FallbackThrows_ReturnsMConnectFallbackFailed_AndIncrementsCounter()
    {
        var (sut, _, _) = Build(_ => throw new TaskCanceledException(),
            allowFallback: true,
            ndaByCode: new Dictionary<string, bool> { ["RSP"] = true });
        using var capture = new FallbackMeterCapture();
        var fallback = new MConnectFallback(
            PartnerSystemCode: "RSP",
            DirectInvoke: _ => throw new InvalidOperationException("partner-API-down"),
            PartnerHasNda: true);

        var result = await sut.CallAsync("RSP.GetPerson", "{}", fallback, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFallbackFailed);
        result.ErrorMessage.Should().Contain("partner-API-down");
        capture.Invoked.Should().ContainSingle().Which.Partner.Should().Be("RSP");
        capture.Failed.Should().ContainSingle().Which.Should().Be("RSP");
    }

    [Fact]
    public async Task CallAsync_LegacyOverload_StillCompiles_AndDoesNotInvokeFallback()
    {
        // Regression — the 12 typed facades (RSP / SFS / ...) call the legacy three-arg
        // overload. Confirm that overload still works and does not require fallback wiring.
        var (sut, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CallResponseEnvelope("{\"ok\":true}"), Encoding.UTF8, "text/xml"),
        }, allowFallback: true);

        var result = await sut.CallAsync("RSP.GetPerson", "{}", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("{\"ok\":true}");
    }
}
