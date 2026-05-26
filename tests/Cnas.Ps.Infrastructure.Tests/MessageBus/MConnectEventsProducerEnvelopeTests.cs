using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.MessageBus;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Services.MessageBus;
using FluentAssertions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.MessageBus;

/// <summary>
/// R0102 / TOR CF 14.02 — pins the seven-field shape of the canonical
/// <see cref="MConnectEnvelope"/> emitted by
/// <see cref="MConnectEnvelopeFactory"/>. The factory is the single producer
/// seam that flows into <c>MConnectEventsProducer</c>, so closing the
/// envelope contract here covers every event path.
/// </summary>
public sealed class MConnectEventsProducerEnvelopeTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private static MConnectEnvelopeFactory NewFactory(string? correlationId = "corr-1")
    {
        var caller = Substitute.For<ICallerContext>();
        caller.CorrelationId.Returns(correlationId);
        return new MConnectEnvelopeFactory(new StubClock(ClockNow), caller);
    }

    /// <summary>Every one of the seven envelope fields is populated.</summary>
    [Fact]
    public void Build_AllSevenFields_Populated()
    {
        var sut = NewFactory();

        var envelope = sut.Build(
            schemaUrn: "urn:cnas:event:application.submitted:v1",
            payloadJson: "{\"applicationId\":7}");

        envelope.MessageId.Should().NotBeNullOrWhiteSpace();
        envelope.CorrelationId.Should().NotBeNullOrWhiteSpace();
        envelope.CausationId.Should().NotBeNullOrWhiteSpace();
        envelope.Timestamp.Should().Be(ClockNow);
        envelope.Source.Should().NotBeNullOrWhiteSpace();
        envelope.Schema.Should().Be("urn:cnas:event:application.submitted:v1");
        envelope.Payload.Should().Be("{\"applicationId\":7}");
    }

    /// <summary>Each call mints a fresh MessageId (uniqueness guarantee).</summary>
    [Fact]
    public void Build_TwoInvocations_MessageIdsDiffer()
    {
        var sut = NewFactory();

        var a = sut.Build("urn:cnas:event:x:v1", "{}");
        var b = sut.Build("urn:cnas:event:x:v1", "{}");

        a.MessageId.Should().NotBe(b.MessageId);
    }

    /// <summary>Explicit CorrelationId is honoured.</summary>
    [Fact]
    public void Build_ExplicitCorrelationId_IsHonoured()
    {
        var sut = NewFactory();

        var envelope = sut.Build(
            schemaUrn: "urn:cnas:event:x:v1",
            payloadJson: "{}",
            correlationId: "my-correlation-id");

        envelope.CorrelationId.Should().Be("my-correlation-id");
    }

    /// <summary>Missing causation id falls back to a fresh GUID rather than empty.</summary>
    [Fact]
    public void Build_NoCausationId_GeneratesFreshGuid()
    {
        var sut = NewFactory();

        var envelope = sut.Build(
            schemaUrn: "urn:cnas:event:x:v1",
            payloadJson: "{}");

        envelope.CausationId.Should().NotBeNullOrWhiteSpace();
        Guid.TryParseExact(envelope.CausationId, "N", out _).Should().BeTrue();
    }

    /// <summary>Schema field carries the canonical URN string verbatim.</summary>
    [Theory]
    [InlineData("urn:cnas:event:application.submitted:v1")]
    [InlineData("urn:cnas:event:decision.recomputed:v2")]
    [InlineData("urn:cnas:event:payment.suspended:v1")]
    public void Build_SchemaUrn_StoredVerbatim(string urn)
    {
        var sut = NewFactory();
        var envelope = sut.Build(urn, "{}");
        envelope.Schema.Should().Be(urn);
    }

    /// <summary>Adapting the envelope to CloudEvents preserves the seven fields.</summary>
    [Fact]
    public void ToCloudEvent_PreservesEnvelopeFields()
    {
        var sut = NewFactory();
        var envelope = sut.Build(
            schemaUrn: "urn:cnas:event:x:v1",
            payloadJson: "{\"k\":1}");

        var ce = sut.ToCloudEvent(envelope);

        ce.Id.Should().Be(envelope.MessageId);
        ce.Source.Should().Be(envelope.Source);
        ce.Type.Should().Be(envelope.Schema);
        ce.TimeUtc.Should().Be(envelope.Timestamp);
        ce.DataJson.Should().Be(envelope.Payload);
        ce.PartitionKey.Should().Be(envelope.CausationId);
    }
}
