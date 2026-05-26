using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using CsvHelper;
using CsvHelper.Configuration;

namespace Cnas.Ps.Infrastructure.Services.Reporting;

/// <summary>
/// R0529 / TOR CF 03.14 — CSV implementation of <see cref="IReportExporter"/>
/// backed by <c>CsvHelper</c> (already pinned in <c>Directory.Packages.props</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire format.</b> RFC 4180 with a UTF-8 BOM and CRLF line endings — the
/// same convention used by the existing <c>PublicCatalogCsvWriter</c> so
/// downstream tooling (Excel on Windows, Power BI on macOS) can detect the
/// encoding without a separate manifest.
/// </para>
/// <para>
/// <b>Stateless + thread-safe.</b> Each <see cref="ExportAsync"/> call
/// allocates a fresh <see cref="MemoryStream"/> + <see cref="CsvWriter"/>;
/// concurrent invocations therefore never share writer state.
/// </para>
/// </remarks>
public sealed class CsvReportExporter : IReportExporter
{
    /// <summary>Canonical MIME for the .csv wire format.</summary>
    internal const string CsvMimeType = "text/csv; charset=utf-8";

    /// <summary>Dotted file extension for the .csv wire format.</summary>
    internal const string CsvExtension = ".csv";

    /// <inheritdoc />
    public ReportExportFormat Format => ReportExportFormat.Csv;

    /// <inheritdoc />
    public Task<Result<ReportExportResultDto>> ExportAsync(
        ReportExportInputDto input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream();
        // UTF-8 with BOM so Windows Excel auto-detects the encoding.
        using (var writer = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true))
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                NewLine = "\r\n",
                HasHeaderRecord = false,
            };
            using var csv = new CsvWriter(writer, config);

            // Header row.
            foreach (var column in input.Columns)
            {
                csv.WriteField(column.Header);
            }
            csv.NextRecord();

            // Data rows.
            foreach (var row in input.Rows)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var cell in row)
                {
                    csv.WriteField(cell ?? string.Empty);
                }
                csv.NextRecord();
            }
            writer.Flush();
        }

        var resultValue = new ReportExportResultDto(
            Bytes: ms.ToArray(),
            ContentType: CsvMimeType,
            Format: ReportExportFormat.Csv,
            FileExtension: CsvExtension);
        return Task.FromResult(Result<ReportExportResultDto>.Success(resultValue));
    }
}
