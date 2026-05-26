using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;

namespace Cnas.Ps.Infrastructure.Services.Reporting;

/// <summary>
/// R0529 / TOR CF 03.14 — façade implementation of
/// <see cref="IReportExportSelector"/>. Receives every registered
/// <see cref="IReportExporter"/> through
/// <c>IEnumerable&lt;IReportExporter&gt;</c> and dispatches by
/// <see cref="IReportExporter.Format"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Telemetry.</b> Each successful dispatch increments the
/// <c>cnas.report_export.generated</c> counter (tag <c>format</c>) and
/// records the rendered byte-length on the
/// <c>cnas.report_export.size_bytes</c> counter (sum-style, tag
/// <c>format</c>). Counter cardinality is bounded by the
/// <see cref="ReportExportFormat"/> enum (4 today) so the time-series
/// cost is fixed.
/// </para>
/// </remarks>
public sealed class ReportExportSelector : IReportExportSelector
{
    /// <summary>Map from format to exporter, built once per selector instance.</summary>
    private readonly Dictionary<ReportExportFormat, IReportExporter> _exporters;

    /// <summary>
    /// Builds the selector by indexing every registered exporter by its
    /// <see cref="IReportExporter.Format"/> property. The dictionary is
    /// constructed eagerly so dispatch is O(1) and the constructor surfaces
    /// duplicate-format misconfiguration immediately.
    /// </summary>
    /// <param name="exporters">Every registered exporter in DI.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exporters"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when two exporters claim the same format — surfaces composition
    /// errors at startup.
    /// </exception>
    public ReportExportSelector(IEnumerable<IReportExporter> exporters)
    {
        ArgumentNullException.ThrowIfNull(exporters);

        _exporters = [];
        foreach (var exporter in exporters)
        {
            if (!_exporters.TryAdd(exporter.Format, exporter))
            {
                throw new InvalidOperationException(
                    $"Duplicate IReportExporter registration for format '{exporter.Format}'.");
            }
        }
    }

    /// <inheritdoc />
    public async Task<Result<ReportExportResultDto>> ExportAsync(
        ReportExportFormat format,
        ReportExportInputDto input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        if (!_exporters.TryGetValue(format, out var exporter))
        {
            return Result<ReportExportResultDto>.Failure(
                ErrorCodes.ExportFormatNotSupported,
                $"No exporter registered for format '{format}'.");
        }

        var result = await exporter.ExportAsync(input, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            CnasMeter.ReportExportGenerated.Add(1, new KeyValuePair<string, object?>("format", format.ToString()));
            CnasMeter.ReportExportSizeBytes.Add(result.Value.Bytes.LongLength,
                new KeyValuePair<string, object?>("format", format.ToString()));
        }
        return result;
    }
}
