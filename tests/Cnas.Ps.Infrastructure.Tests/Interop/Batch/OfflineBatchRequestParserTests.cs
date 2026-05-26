using System.Text;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Interop.Batch;

namespace Cnas.Ps.Infrastructure.Tests.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — tests for <see cref="OfflineBatchRequestParser"/>.
/// </summary>
public sealed class OfflineBatchRequestParserTests
{
    /// <summary>R1710 — happy path: well-formed CSV maps to one seed per data row.</summary>
    [Fact]
    public async Task Parse_GetInsuredPersonStatus_HappyPath()
    {
        var parser = new OfflineBatchRequestParser(new OfflineBatchOpSchemaRegistry());
        var csv = "Idnp\n2000123456782\n1234567890123\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var result = await parser.ParseAsync(AnnexFourBatchOp.GetInsuredPersonStatus, stream);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].RowOrdinal.Should().Be(1);
        result.Value[0].ParseError.Should().BeNull();
        result.Value[0].RequestPayloadJson.Should().Contain("2000123456782");
    }

    /// <summary>R1710 — header mismatch surfaces as Result.Failure (file-level).</summary>
    [Fact]
    public async Task Parse_WrongHeader_FailsAtFileLevel()
    {
        var parser = new OfflineBatchRequestParser(new OfflineBatchOpSchemaRegistry());
        var csv = "NotIdnp\n2000123456782\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var result = await parser.ParseAsync(AnnexFourBatchOp.GetInsuredPersonStatus, stream);

        result.IsFailure.Should().BeTrue();
    }

    /// <summary>R1710 — exceeding the 10,000-row cap surfaces with the dedicated code.</summary>
    [Fact]
    public async Task Parse_OverRowCap_SurfacesTooManyRows()
    {
        var parser = new OfflineBatchRequestParser(new OfflineBatchOpSchemaRegistry());
        var sb = new StringBuilder();
        sb.AppendLine("Idnp");
        for (int i = 0; i <= OfflineBatchRequestParser.MaxRowsPerFile; i++)
        {
            sb.AppendLine("2000123456782");
        }
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        var result = await parser.ParseAsync(AnnexFourBatchOp.GetInsuredPersonStatus, stream);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OfflineBatchRequestParser.TooManyRowsCode);
    }

    /// <summary>R1710 — CSV escape: quoted cell with commas round-trips correctly.</summary>
    [Fact]
    public void ParseCsvLine_QuotedCellWithCommas_Splits()
    {
        var cells = OfflineBatchRequestParser.ParseCsvLine("\"one,two\",three");
        cells.Should().HaveCount(2);
        cells[0].Should().Be("one,two");
        cells[1].Should().Be("three");
    }
}
