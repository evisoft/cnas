using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services.Reporting;

namespace Cnas.Ps.Infrastructure.Tests.Reporting;

/// <summary>
/// R0529 / TOR CF 03.14 — dispatch tests for <see cref="ReportExportSelector"/>.
/// Asserts the selector picks the registered exporter whose <c>Format</c>
/// matches the caller-requested format and returns
/// <see cref="ErrorCodes.ExportFormatNotSupported"/> when no exporter is
/// registered for the requested format.
/// </summary>
public sealed class ReportExportSelectorTests
{
    /// <summary>Baseline columns shared across selector tests.</summary>
    private static readonly ReportExportColumnDto[] BaselineColumns =
    [
        new("Code"),
    ];

    /// <summary>Baseline rows shared across selector tests.</summary>
    private static readonly IReadOnlyList<string>[] BaselineRows =
    [
        ["X"],
    ];

    /// <summary>The selector routes a Csv request to the Csv exporter.</summary>
    [Fact]
    public async Task ExportAsync_CsvFormat_RoutesToCsvExporter()
    {
        var sut = new ReportExportSelector(
            new IReportExporter[] { new CsvReportExporter(), new XlsxReportExporter() });
        var input = new ReportExportInputDto("R1", BaselineColumns, BaselineRows);

        var result = await sut.ExportAsync(ReportExportFormat.Csv, input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Format.Should().Be(ReportExportFormat.Csv);
        result.Value.ContentType.Should().Be("text/csv; charset=utf-8");
    }

    /// <summary>Unregistered format yields EXPORT_FORMAT_NOT_SUPPORTED.</summary>
    [Fact]
    public async Task ExportAsync_UnregisteredFormat_ReturnsFormatNotSupported()
    {
        // Only Csv registered; ask for Pdf — must fail with the stable code.
        var sut = new ReportExportSelector(new IReportExporter[] { new CsvReportExporter() });
        var input = new ReportExportInputDto("R1", BaselineColumns, BaselineRows);

        var result = await sut.ExportAsync(ReportExportFormat.Pdf, input, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ExportFormatNotSupported);
    }

    /// <summary>Two exporters claiming the same format throw at construction time.</summary>
    [Fact]
    public void Ctor_DuplicateFormat_Throws()
    {
        var dupe = () => new ReportExportSelector(
            new IReportExporter[] { new CsvReportExporter(), new CsvReportExporter() });

        dupe.Should().Throw<System.InvalidOperationException>();
    }
}
