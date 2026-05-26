using System;
using System.Text.Json;
using Cnas.Ps.Infrastructure.MGov;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Unit tests for <see cref="MCabinetPublisher.IsoUtcDateTimeConverter"/>. The converter
/// is the single point of contact between CNAS-internal <see cref="DateTime"/> values
/// and the wire format consumed by MCabinet.
/// </summary>
/// <remarks>
/// <para>
/// The bug being pinned: an <see cref="DateTimeKind.Unspecified"/>-kind input used to
/// be routed through <c>ToUniversalTime()</c>, which assumes the value is in local
/// time and shifts it by the server's local offset. CNAS stores UTC everywhere
/// (CLAUDE.md cross-cutting — UTC Everywhere), so an Unspecified value is *already*
/// the UTC wall-clock; the converter must accept it as-is.
/// </para>
/// </remarks>
public class IsoUtcDateTimeConverterTests
{
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new MCabinetPublisher.IsoUtcDateTimeConverter());
        return opts;
    }

    /// <summary>
    /// A UTC-kind value serialises to the canonical Zulu form with no offset shift.
    /// Baseline for the Unspecified/Local branches below.
    /// </summary>
    [Fact]
    public void Write_UtcKind_EmitsZuluString()
    {
        var value = new DateTime(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(value, Options);

        json.Should().Be("\"2026-05-19T10:00:00Z\"");
    }

    /// <summary>
    /// Core regression: an Unspecified-kind value must NOT be shifted by the server's
    /// local timezone offset. CNAS treats Unspecified as "already UTC wall-clock",
    /// matching the rest of the codebase's UTC-everywhere convention.
    /// </summary>
    [Fact]
    public void Write_UnspecifiedKind_DoesNotShiftValue()
    {
        // 2026-05-19T10:00:00 with NO kind — historically this was treated as local
        // and shifted; the fix preserves the wall-clock value and stamps it Utc.
        var value = new DateTime(2026, 5, 19, 10, 0, 0, DateTimeKind.Unspecified);

        var json = JsonSerializer.Serialize(value, Options);

        json.Should().Be("\"2026-05-19T10:00:00Z\"",
            "an Unspecified-kind value is already the UTC wall-clock in CNAS — "
            + "the converter must NOT apply a local-to-UTC shift.");
    }

    /// <summary>
    /// Local-kind values still go through <c>ToUniversalTime()</c> — this branch
    /// represents code that genuinely carries a local instant and needs converting.
    /// We assert the converter emits a Z-suffixed string after the local-to-UTC shift.
    /// </summary>
    [Fact]
    public void Write_LocalKind_ConvertsToUtcAndEmitsZuluString()
    {
        var local = new DateTime(2026, 5, 19, 10, 0, 0, DateTimeKind.Local);
        var expectedUtc = local.ToUniversalTime();

        var json = JsonSerializer.Serialize(local, Options);

        json.Should().Be($"\"{expectedUtc:yyyy-MM-ddTHH:mm:ss}Z\"");
    }

    /// <summary>
    /// Round-trip check: serialise an Unspecified-kind value and re-read it. The Read
    /// path stamps Utc on the result so the round-trip preserves both the wall-clock
    /// and the Kind. This is what the "no timezone shift" guarantee looks like in
    /// practice from a caller's perspective.
    /// </summary>
    [Fact]
    public void RoundTrip_UnspecifiedKind_PreservesWallClock()
    {
        var value = new DateTime(2026, 5, 19, 10, 0, 0, DateTimeKind.Unspecified);

        var json = JsonSerializer.Serialize(value, Options);
        var read = JsonSerializer.Deserialize<DateTime>(json, Options);

        read.Should().Be(new DateTime(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc));
        read.Kind.Should().Be(DateTimeKind.Utc);
    }
}
