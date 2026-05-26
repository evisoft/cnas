using Cnas.Ps.Application.Etl;

namespace Cnas.Ps.Application.Tests.Etl;

/// <summary>
/// R0153 / TOR CF 19.05 — tests for the pure-function
/// <see cref="PeriodSliceBuilder"/>. Each test exercises one slice-merging
/// invariant from the algorithm spec.
/// </summary>
public sealed class PeriodSliceBuilderTests
{
    /// <summary>Stable creation-time anchor used across tests.</summary>
    private static readonly DateTime CreatedBase = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Shared field-name closed-set for AddressCity-only scenarios.</summary>
    private static readonly string[] AddressOnly = { "AddressCity" };

    /// <summary>Shared field-name closed-set for AddressCity + PhoneE164 scenarios.</summary>
    private static readonly string[] AddressAndPhone = { "AddressCity", "PhoneE164" };

    [Fact]
    public void Build_TwoAddressRowsAndOneContactRow_ProducesThreeDistinctSlices()
    {
        // Address rows: A1 covers [2024-01-01 .. 2025-01-01); A2 covers [2025-01-01 .. open).
        // Contact row:  C1 covers [2024-06-01 .. open).
        // Expected boundary set: {2024-01-01, 2024-06-01, 2025-01-01, MaxValue}
        // -> 3 slices: [Jan..Jun24), [Jun24..Jan25), [Jan25..MaxValue).
        var rows = new[]
        {
            new PeriodSliceBuilder.SourceRow(
                SourceId: 1, FieldName: "AddressCity", Value: "Chișinău",
                ValidFromUtc: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidToUtc: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc: CreatedBase),
            new PeriodSliceBuilder.SourceRow(
                SourceId: 2, FieldName: "AddressCity", Value: "Bălți",
                ValidFromUtc: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidToUtc: null,
                CreatedAtUtc: CreatedBase.AddDays(10)),
            new PeriodSliceBuilder.SourceRow(
                SourceId: 3, FieldName: "PhoneE164", Value: "+37360123456",
                ValidFromUtc: new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidToUtc: null,
                CreatedAtUtc: CreatedBase.AddDays(20)),
        };

        var slices = PeriodSliceBuilder.Build(rows, AddressAndPhone);

        slices.Should().HaveCount(3);
        slices[0].PeriodStartUtc.Should().Be(new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        slices[0].PeriodEndUtc.Should().Be(new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        slices[0].ResolvedFields["AddressCity"].Should().Be("Chișinău");
        slices[0].ResolvedFields["PhoneE164"].Should().BeNull();

        slices[1].PeriodStartUtc.Should().Be(new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        slices[1].PeriodEndUtc.Should().Be(new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        slices[1].ResolvedFields["AddressCity"].Should().Be("Chișinău");
        slices[1].ResolvedFields["PhoneE164"].Should().Be("+37360123456");

        slices[2].PeriodStartUtc.Should().Be(new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        slices[2].PeriodEndUtc.Should().Be(PeriodSliceBuilder.OpenEndedSentinel);
        slices[2].ResolvedFields["AddressCity"].Should().Be("Bălți");
        slices[2].ResolvedFields["PhoneE164"].Should().Be("+37360123456");
    }

    [Fact]
    public void Build_SingleSourceRow_ProducesSingleSliceSpanningItsValidity()
    {
        var rows = new[]
        {
            new PeriodSliceBuilder.SourceRow(
                SourceId: 1, FieldName: "AddressCity", Value: "Chișinău",
                ValidFromUtc: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidToUtc: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc: CreatedBase),
        };

        var slices = PeriodSliceBuilder.Build(rows, AddressOnly);

        slices.Should().ContainSingle();
        slices[0].PeriodStartUtc.Should().Be(new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        slices[0].PeriodEndUtc.Should().Be(new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        slices[0].ResolvedFields["AddressCity"].Should().Be("Chișinău");
    }

    [Fact]
    public void Build_OverlappingRowsForSameField_MostRecentlyCreatedWins()
    {
        // Two rows with overlapping intervals on the SAME field — most-recently-created
        // wins inside the overlap, per the algorithm tie-break.
        var rows = new[]
        {
            new PeriodSliceBuilder.SourceRow(
                SourceId: 1, FieldName: "AddressCity", Value: "OldValue",
                ValidFromUtc: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidToUtc: new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc: CreatedBase),
            new PeriodSliceBuilder.SourceRow(
                SourceId: 2, FieldName: "AddressCity", Value: "NewValue",
                ValidFromUtc: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidToUtc: new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc: CreatedBase.AddDays(100)),
        };

        var slices = PeriodSliceBuilder.Build(rows, AddressOnly);

        // 2 slices: [2024-01..2025-01) → OldValue; [2025-01..2026-01) → NewValue.
        slices.Should().HaveCount(2);
        slices[0].ResolvedFields["AddressCity"].Should().Be("OldValue");
        slices[1].ResolvedFields["AddressCity"].Should().Be("NewValue");
    }

    [Fact]
    public void Build_OpenEndedSourceRow_MaterialisesSliceEndAtMaxValueSentinel()
    {
        var rows = new[]
        {
            new PeriodSliceBuilder.SourceRow(
                SourceId: 1, FieldName: "AddressCity", Value: "Chișinău",
                ValidFromUtc: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidToUtc: null,
                CreatedAtUtc: CreatedBase),
        };

        var slices = PeriodSliceBuilder.Build(rows, AddressOnly);

        slices.Should().ContainSingle();
        slices[0].PeriodEndUtc.Should().Be(DateTime.MaxValue);
        slices[0].PeriodEndUtc.Should().Be(PeriodSliceBuilder.OpenEndedSentinel);
    }
}
