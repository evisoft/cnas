using System.Text;
using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services.Treasury.Feed;

namespace Cnas.Ps.Infrastructure.Tests.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — tests for <see cref="TreasuryFeedParser"/>.
/// </summary>
public sealed class TreasuryFeedParserTests
{
    /// <summary>A minimal CSV with one happy row parses into one populated record.</summary>
    [Fact]
    public async Task ParseAsync_MinimalCsv_ProducesOneRow()
    {
        var parser = new TreasuryFeedParser();
        var bytes = TreasuryFeedTestHelpers.BuildCsv(
            ("TR-1", "2026-05-22", "1000000000003", "Test Payer", "100.00", "MD12", "ref"));
        using var ms = new MemoryStream(bytes);

        var result = await parser.ParseAsync(ms);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        var row = result.Value[0];
        row.ParseError.Should().BeNull();
        row.ReceiptNumber.Should().Be("TR-1");
        row.ReceiptDate.Should().Be(new DateOnly(2026, 5, 22));
        row.PayerIdno.Should().Be("1000000000003");
        row.AmountMdl.Should().Be(100.00m);
        row.TreasuryCode.Should().Be("MD12");
        row.Reference.Should().Be("ref");
    }

    /// <summary>
    /// A row whose ReceiptNumber fails the regex is surfaced with
    /// ParseError populated but does NOT halt the parse — the next row is
    /// still emitted.
    /// </summary>
    [Fact]
    public async Task ParseAsync_BadReceiptNumber_RecordsParseErrorWithoutHalting()
    {
        var parser = new TreasuryFeedParser();
        var bytes = TreasuryFeedTestHelpers.BuildCsv(
            ("bad lowercase", "2026-05-22", "1000000000003", "Test Payer", "100.00", "MD12", "ref"),
            ("TR-OK", "2026-05-22", "1000000000003", "Test Payer", "50.00", "MD12", "ok"));
        using var ms = new MemoryStream(bytes);

        var result = await parser.ParseAsync(ms);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].ParseError.Should().NotBeNull();
        result.Value[0].ParseErrorCode.Should().Be(TreasuryFeedParser.BadReceiptNumberCode);
        result.Value[1].ParseError.Should().BeNull();
        result.Value[1].ReceiptNumber.Should().Be("TR-OK");
    }

    /// <summary>A file with > 100_000 data rows is rejected.</summary>
    [Fact]
    public async Task ParseAsync_TooManyRows_IsRejected()
    {
        var parser = new TreasuryFeedParser();
        var sb = new StringBuilder();
        sb.AppendLine("ReceiptNumber,ReceiptDate,PayerIdno,PayerName,AmountMdl,TreasuryCode,Reference");
        for (int i = 0; i < TreasuryFeedParser.MaxRowsPerFile + 1; i++)
        {
            sb.AppendLine($"TR-{i:D6},2026-05-22,1000000000003,Payer,1.00,MD12,r");
        }
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));

        var result = await parser.ParseAsync(ms);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Be(ITreasuryFeedImporter.TooManyRowsCode);
    }

    /// <summary>A file missing a required header column is rejected.</summary>
    [Fact]
    public async Task ParseAsync_MissingHeader_IsRejected()
    {
        var parser = new TreasuryFeedParser();
        // Drop the "TreasuryCode" column from the header.
        var sb = new StringBuilder();
        sb.AppendLine("ReceiptNumber,ReceiptDate,PayerIdno,PayerName,AmountMdl,Reference");
        sb.AppendLine("TR-1,2026-05-22,1000000000003,Payer,1.00,r");
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));

        var result = await parser.ParseAsync(ms);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Be(ITreasuryFeedImporter.MissingHeaderCode);
    }
}
