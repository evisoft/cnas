using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Core.Tests.ValueObjects;

public class DateRangeUtcTests
{
    private static readonly DateTime StartUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EndUtc = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TryCreate_BothUtc_StartBeforeEnd_Succeeds()
    {
        var result = DateRangeUtc.TryCreate(StartUtc, EndUtc);

        result.IsSuccess.Should().BeTrue();
        result.Value.StartUtc.Should().Be(StartUtc);
        result.Value.EndUtc.Should().Be(EndUtc);
    }

    [Fact]
    public void TryCreate_OpenEnded_Succeeds()
    {
        var result = DateRangeUtc.TryCreate(StartUtc, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.EndUtc.Should().BeNull();
        result.Value.Duration.Should().BeNull();
    }

    [Fact]
    public void TryCreate_StartNotUtc_Fails()
    {
        var local = DateTime.SpecifyKind(StartUtc, DateTimeKind.Local);

        var result = DateRangeUtc.TryCreate(local, EndUtc);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidDateRange);
    }

    [Fact]
    public void TryCreate_StartUnspecified_Fails()
    {
        var unspec = DateTime.SpecifyKind(StartUtc, DateTimeKind.Unspecified);

        var result = DateRangeUtc.TryCreate(unspec, EndUtc);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidDateRange);
    }

    [Fact]
    public void TryCreate_EndNotUtc_Fails()
    {
        var localEnd = DateTime.SpecifyKind(EndUtc, DateTimeKind.Local);

        var result = DateRangeUtc.TryCreate(StartUtc, localEnd);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidDateRange);
    }

    [Fact]
    public void TryCreate_EndBeforeStart_Fails()
    {
        var result = DateRangeUtc.TryCreate(EndUtc, StartUtc);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidDateRange);
    }

    [Fact]
    public void TryCreate_StartEqualsEnd_Fails()
    {
        var result = DateRangeUtc.TryCreate(StartUtc, StartUtc);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidDateRange);
    }

    [Fact]
    public void Contains_StartBoundary_IsIncluded()
    {
        var range = DateRangeUtc.TryCreate(StartUtc, EndUtc).Value;

        range.Contains(StartUtc).Should().BeTrue();
    }

    [Fact]
    public void Contains_EndBoundary_IsExcluded()
    {
        var range = DateRangeUtc.TryCreate(StartUtc, EndUtc).Value;

        range.Contains(EndUtc).Should().BeFalse();
    }

    [Fact]
    public void Contains_InsideRange_IsIncluded()
    {
        var range = DateRangeUtc.TryCreate(StartUtc, EndUtc).Value;
        var midpoint = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        range.Contains(midpoint).Should().BeTrue();
    }

    [Fact]
    public void Contains_BeforeStart_IsExcluded()
    {
        var range = DateRangeUtc.TryCreate(StartUtc, EndUtc).Value;
        var earlier = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        range.Contains(earlier).Should().BeFalse();
    }

    [Fact]
    public void Contains_OpenEnded_IncludesAnyFutureInstant()
    {
        var range = DateRangeUtc.TryCreate(StartUtc, null).Value;
        var far = new DateTime(2099, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        range.Contains(far).Should().BeTrue();
    }

    [Fact]
    public void Contains_NonUtcInstant_ReturnsFalse()
    {
        // Defensive: callers must pass UTC. Non-UTC instants are out of contract.
        var range = DateRangeUtc.TryCreate(StartUtc, EndUtc).Value;
        var local = DateTime.SpecifyKind(new DateTime(2026, 6, 15), DateTimeKind.Local);

        range.Contains(local).Should().BeFalse();
    }

    [Fact]
    public void Overlaps_FullyContained_IsTrue()
    {
        var outer = DateRangeUtc.TryCreate(StartUtc, EndUtc).Value;
        var inner = DateRangeUtc.TryCreate(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)).Value;

        outer.Overlaps(inner).Should().BeTrue();
        inner.Overlaps(outer).Should().BeTrue();
    }

    [Fact]
    public void Overlaps_TouchingAtBoundary_IsFalse()
    {
        // Half-open intervals: [a, b) and [b, c) do NOT overlap.
        var first = DateRangeUtc.TryCreate(
            StartUtc,
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)).Value;
        var second = DateRangeUtc.TryCreate(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndUtc).Value;

        first.Overlaps(second).Should().BeFalse();
    }

    [Fact]
    public void Overlaps_Disjoint_IsFalse()
    {
        var first = DateRangeUtc.TryCreate(
            StartUtc,
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)).Value;
        var second = DateRangeUtc.TryCreate(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndUtc).Value;

        first.Overlaps(second).Should().BeFalse();
    }

    [Fact]
    public void Overlaps_OpenEndedRangeOverlapsAnyLaterRange()
    {
        var open = DateRangeUtc.TryCreate(StartUtc, null).Value;
        var later = DateRangeUtc.TryCreate(
            new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2099, 2, 1, 0, 0, 0, DateTimeKind.Utc)).Value;

        open.Overlaps(later).Should().BeTrue();
        later.Overlaps(open).Should().BeTrue();
    }

    [Fact]
    public void Duration_ClosedRange_ReturnsTimeSpan()
    {
        var range = DateRangeUtc.TryCreate(StartUtc, EndUtc).Value;

        range.Duration.Should().Be(EndUtc - StartUtc);
    }
}
