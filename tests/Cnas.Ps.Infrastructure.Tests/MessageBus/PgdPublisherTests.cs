using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
using Cnas.Ps.Application.MessageBus;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Services.MessageBus;
using Cnas.Ps.Infrastructure.Tests.MGov;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.MessageBus;

/// <summary>
/// R0117 / CF 14.11 — unit tests for <see cref="PgdPublisher"/>. Exercises the
/// configured-happy path, the unconfigured-skip path, the upstream-failure path, and
/// the metric emissions.
/// </summary>
public sealed class PgdPublisherTests
{
    private const string BaseUrl = "https://date.example.gov.md";

    private static PgdDatasetPublishInputDto SampleInput(string code = "stat.beneficiaries") => new(
        DatasetCode: code,
        Title: "Beneficiar count",
        Description: "Aggregated counts by region.",
        PayloadJson: "{\"rows\":[]}",
        ContentType: "application/json");

    private static (PgdPublisher Publisher, CapturingHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? baseUrl = BaseUrl)
    {
        var handler = new CapturingHandler(respond);
        var http = new HttpClient(handler);
        var opts = Options.Create(new PgdPublisherOptions
        {
            BaseUrl = baseUrl ?? string.Empty,
            ApiKey = string.Empty,
            SystemCode = "CNAS-PS",
        });
        return (new PgdPublisher(http, opts, NullLogger<PgdPublisher>.Instance), handler);
    }

    [Fact]
    public async Task PublishAsync_ConfiguredHappyPath_ReturnsAcceptedWithReferenceId()
    {
        var (sut, handler) = Build(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("X-Pgd-Reference-Id", "REF-ABC123");
            return response;
        });

        var result = await sut.PublishAsync(SampleInput());

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(PgdPublishStatus.Accepted);
        result.Value.PgdReferenceId.Should().Be("REF-ABC123");
        result.Value.FailureReason.Should().BeNull();
        handler.Last.Method.Should().Be(HttpMethod.Post);
        handler.Last.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/api/datasets/stat.beneficiaries");
    }

    [Fact]
    public async Task PublishAsync_BlankBaseUrl_ReturnsPgdNotConfigured()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");

        var result = await sut.PublishAsync(SampleInput());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PgdNotConfigured);
        // No HTTP traffic — the safety guard refused to attempt the call.
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_Upstream500_ReturnsPgdPublishFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.PublishAsync(SampleInput());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PgdPublishFailed);
    }

    [Fact]
    public async Task PublishAsync_SuccessAndFailure_EmitOutcomeMetrics()
    {
        // Snapshot both counters: every call increments PgdPublishAttempted and
        // PgdPublishOutcome regardless of result.
        var observedAttempted = 0L;
        var observedOutcomeStatuses = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CnasMeter.MeterName &&
                (instr.Name == "cnas.pgd.publish.attempted" || instr.Name == "cnas.pgd.publish.outcome"))
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<long>((instr, value, tags, _) =>
        {
            if (instr.Name == "cnas.pgd.publish.attempted")
            {
                Interlocked.Add(ref observedAttempted, value);
            }
            else if (instr.Name == "cnas.pgd.publish.outcome")
            {
                foreach (var t in tags)
                {
                    if (t.Key == "status" && t.Value is string s)
                    {
                        lock (observedOutcomeStatuses) { observedOutcomeStatuses.Add(s); }
                    }
                }
            }
        });
        listener.Start();

        var (ok, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await ok.PublishAsync(SampleInput("ok"));

        var (bad, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await bad.PublishAsync(SampleInput("bad"));

        observedAttempted.Should().BeGreaterThanOrEqualTo(2);
        observedOutcomeStatuses.Should().Contain("accepted");
        observedOutcomeStatuses.Should().Contain("rejected");
    }
}
