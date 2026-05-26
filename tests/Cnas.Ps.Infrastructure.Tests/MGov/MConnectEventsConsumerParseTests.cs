using System;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure.MGov;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Unit tests for <see cref="MConnectEventsConsumer.ParseEnvelope"/>. The parser sits in
/// the hot path of every inbound CloudEvent; any exception escaping it tears down the
/// WebSocket connection and triggers a reconnect storm.
/// </summary>
/// <remarks>
/// <para>
/// Previously a malformed <c>time</c> attribute used <c>DateTime.Parse</c> which throws
/// <see cref="FormatException"/> on non-ISO input. Only <see cref="System.Text.Json.JsonException"/>
/// is caught upstream, so the FormatException would escape, abort the receive loop, and
/// force a reconnect — effectively a DoS lever for a hostile producer. The fix switches
/// to <c>DateTime.TryParse</c> and these tests pin the new contract.
/// </para>
/// </remarks>
public class MConnectEventsConsumerParseTests
{
    /// <summary>
    /// Sanity baseline: a well-formed CloudEvent with a proper ISO-8601 <c>time</c>
    /// parses cleanly and emits the expected <see cref="CloudEventEnvelope"/>.
    /// </summary>
    [Fact]
    public void ParseEnvelope_HappyPath_PopulatesAllFields()
    {
        var json = """
        {
            "id": "evt-1",
            "source": "cnas-ps",
            "type": "md.cnas.ps.test.v1",
            "time": "2026-05-19T08:00:00Z",
            "datacontenttype": "application/json",
            "data": { "x": 1 }
        }
        """;

        var envelope = MConnectEventsConsumer.ParseEnvelope(Encoding.UTF8.GetBytes(json));

        envelope.Id.Should().Be("evt-1");
        envelope.Source.Should().Be("cnas-ps");
        envelope.Type.Should().Be("md.cnas.ps.test.v1");
        envelope.TimeUtc.Should().Be(new DateTime(2026, 5, 19, 8, 0, 0, DateTimeKind.Utc));
        envelope.DataContentType.Should().Be("application/json");
    }

    /// <summary>
    /// Regression test: a malformed <c>time</c> attribute must NOT throw. The previous
    /// implementation called <c>DateTime.Parse</c> which raised <see cref="FormatException"/>,
    /// escaping the receive loop. The contract now: drop the time silently
    /// (default <see cref="DateTime"/>) and continue. The rest of the envelope is
    /// preserved so handlers still get the id / source / type for routing + dedup.
    /// </summary>
    [Fact]
    public void ParseEnvelope_MalformedTime_DoesNotThrowAndDefaultsTime()
    {
        var json = """
        {
            "id": "evt-bad-time",
            "source": "cnas-ps",
            "type": "md.cnas.ps.test.v1",
            "time": "not-a-real-iso-instant",
            "data": { "x": 1 }
        }
        """;

        var act = () => MConnectEventsConsumer.ParseEnvelope(Encoding.UTF8.GetBytes(json));

        // The act under test: no exception escapes. Previously this raised FormatException.
        var envelope = act.Should().NotThrow().Subject;
        envelope.Id.Should().Be("evt-bad-time");
        envelope.Source.Should().Be("cnas-ps");
        envelope.Type.Should().Be("md.cnas.ps.test.v1");
        // Time falls back to the default sentinel — handlers can detect "absent" via Kind/Year.
        envelope.TimeUtc.Should().Be(default(DateTime));
    }

    /// <summary>
    /// Boundary: an empty <c>time</c> attribute is treated as absent (same default as
    /// a missing key). Re-pinned alongside the malformed test so both paths into
    /// "time is unusable" produce the same observable result.
    /// </summary>
    [Fact]
    public void ParseEnvelope_EmptyTime_DefaultsTime()
    {
        var json = """
        {
            "id": "evt-empty-time",
            "source": "cnas-ps",
            "type": "md.cnas.ps.test.v1",
            "time": ""
        }
        """;

        var envelope = MConnectEventsConsumer.ParseEnvelope(Encoding.UTF8.GetBytes(json));

        envelope.TimeUtc.Should().Be(default(DateTime));
    }
}
