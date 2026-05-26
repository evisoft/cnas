using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Treasury.Feed;

namespace Cnas.Ps.Infrastructure.Tests.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — tests for the in-memory implementation of
/// <c>ITreasuryFeedSource</c>.
/// </summary>
public sealed class InMemoryTreasuryFeedSourceTests
{
    /// <summary>Seeded date returns the bytes + a deterministic hash.</summary>
    [Fact]
    public async Task FetchAsync_SeededDate_ReturnsContentAndHash()
    {
        var src = new InMemoryTreasuryFeedSource();
        var bytes = TreasuryFeedTestHelpers.BuildCsv(
            ("TR-1", "2026-05-22", "1000000000003", "Test Payer", "100.00", "MD12", "ref"));
        var date = new DateOnly(2026, 5, 22);
        src.Seed(date, bytes);

        var result = await src.FetchAsync(date);

        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().BeEquivalentTo(bytes);
        result.Value.HashSha256.Should().Be(TreasuryFeedTestHelpers.Sha256Hex(bytes));
        result.Value.SizeBytes.Should().Be(bytes.LongLength);
        result.Value.SourceKind.Should().Be(TreasuryFeedSourceKind.InMemoryTest);
        result.Value.SourceReference.Should().Be("in-memory-fixture:2026-05-22");
    }

    /// <summary>Unseeded date returns NotFound.</summary>
    [Fact]
    public async Task FetchAsync_UnseededDate_ReturnsNotFound()
    {
        var src = new InMemoryTreasuryFeedSource();
        var result = await src.FetchAsync(new DateOnly(2026, 5, 22));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
