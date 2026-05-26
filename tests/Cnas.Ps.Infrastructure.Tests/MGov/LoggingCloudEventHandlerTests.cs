using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.MessageBus;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Unit tests for <see cref="LoggingCloudEventHandler"/>. Verifies the catch-all behaviour
/// of the default handler: accepts every event type, consults the R0103 deduper as the
/// first action on every receipt, and produces no exception on either branch.
/// </summary>
public class LoggingCloudEventHandlerTests
{
    /// <summary>Test double that records how it was invoked and returns a canned outcome.</summary>
    private sealed class FakeDeduper : IIntegrationEventDeduper
    {
        public bool ReturnAlreadyProcessed { get; set; }

        public int TryClaimInvocationCount { get; private set; }

        public string? LastMessageId { get; private set; }

        public Task<Result<IntegrationEventDedupOutcomeDto>> TryClaimAsync(string messageId, string source, string type, CancellationToken ct = default)
        {
            TryClaimInvocationCount++;
            LastMessageId = messageId;
            DateTime? earlier = ReturnAlreadyProcessed ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null;
            return Task.FromResult(Result<IntegrationEventDedupOutcomeDto>.Success(
                new IntegrationEventDedupOutcomeDto(ReturnAlreadyProcessed, messageId, earlier)));
        }

        public Task<Result> MarkFailedAsync(string messageId, string failureReason, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<bool>> IsKnownAsync(string messageId, CancellationToken ct = default)
            => Task.FromResult(Result<bool>.Success(ReturnAlreadyProcessed));
    }

    private static CloudEventEnvelope SampleEnvelope() => new(
        Id: "00000000-0000-0000-0000-000000000001",
        Source: "cnas-ps",
        Type: "md.cnas.ps.test.v1",
        TimeUtc: new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc),
        PartitionKey: null,
        DataContentType: "application/json",
        DataJson: "{\"x\":1}");

    [Fact]
    public void CanHandle_AnyType_ReturnsTrue()
    {
        var sut = new LoggingCloudEventHandler(NullLogger<LoggingCloudEventHandler>.Instance, new FakeDeduper());

        sut.CanHandle("md.cnas.ps.decision.issued.v1").Should().BeTrue();
        sut.CanHandle("RSP.citizen.updated.v2").Should().BeTrue();
        sut.CanHandle(string.Empty).Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_FirstObservation_ClaimsAndReturns()
    {
        var deduper = new FakeDeduper { ReturnAlreadyProcessed = false };
        var sut = new LoggingCloudEventHandler(NullLogger<LoggingCloudEventHandler>.Instance, deduper);

        var act = async () => await sut.HandleAsync(SampleEnvelope(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        deduper.TryClaimInvocationCount.Should().Be(1);
        deduper.LastMessageId.Should().Be("00000000-0000-0000-0000-000000000001");
    }

    [Fact]
    public async Task HandleAsync_DuplicateMessageId_ShortCircuits()
    {
        var deduper = new FakeDeduper { ReturnAlreadyProcessed = true };
        var sut = new LoggingCloudEventHandler(NullLogger<LoggingCloudEventHandler>.Instance, deduper);

        var act = async () => await sut.HandleAsync(SampleEnvelope(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        deduper.TryClaimInvocationCount.Should().Be(1);
    }
}
