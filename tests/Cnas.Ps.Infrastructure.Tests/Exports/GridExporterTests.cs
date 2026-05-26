using Cnas.Ps.Application.Exports;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Exports;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Tests.Observability;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.Exports;

/// <summary>
/// R0226 / TOR UI 013 — orchestration tests for <see cref="GridExporter"/>. Covers
/// the row-cap guard, the renderer-lookup table, and the
/// <c>cnas.grid_export.requested</c> counter emission.
/// </summary>
[Collection(CnasMeterCollection.Name)]
public sealed class GridExporterTests
{
    /// <summary>Builds a sut wired with the supplied renderer set + the default row cap.</summary>
    private static GridExporter BuildSut(params IGridExportRenderer[] renderers) =>
        new(renderers, NullLogger<GridExporter>.Instance);

    /// <summary>Builds a sut with a custom row cap (used to exercise the cap-failure path).</summary>
    private static GridExporter BuildSut(int maxRows, params IGridExportRenderer[] renderers) =>
        new(renderers, NullLogger<GridExporter>.Instance, maxExportRows: maxRows);

    /// <summary>Builds a 3-row request matching a single text column.</summary>
    private static GridExportRequest ThreeRowRequest() =>
        new(
            GridName: "Solicitants",
            Columns: new GridColumn[] { new("Code", "Code", GridColumnDataType.Text) },
            Rows: new GridRow[]
            {
                new(new Dictionary<string, object?> { ["Code"] = "A" }),
                new(new Dictionary<string, object?> { ["Code"] = "B" }),
                new(new Dictionary<string, object?> { ["Code"] = "C" }),
            });

    [Fact]
    public async Task ExportAsync_CsvFormat_DelegatesToCsvRenderer()
    {
        var csv = new CsvGridExportRenderer();
        var sut = BuildSut(csv);

        var result = await sut.ExportAsync(ThreeRowRequest(), ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        result.Value.ContentType.Should().StartWith("text/csv");
        result.Value.SuggestedFileName.Should().EndWith(".csv");
    }

    [Fact]
    public async Task ExportAsync_RowsExceedCap_ReturnsExportTooLarge()
    {
        // 5 rows, cap = 4 → must fail with EXPORT_TOO_LARGE without invoking the renderer.
        var rows = Enumerable.Range(0, 5).Select(i =>
            new GridRow(new Dictionary<string, object?> { ["Code"] = $"R{i}" })).ToList();
        var request = ThreeRowRequest() with { Rows = rows };
        var sut = BuildSut(maxRows: 4, new CsvGridExportRenderer());

        var result = await sut.ExportAsync(request, ExportFormat.Csv);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ExportTooLarge);
    }

    [Fact]
    public async Task ExportAsync_RowsAtCap_StillSucceeds()
    {
        // Boundary: exactly at the cap is allowed (the cap is "max permitted").
        var rows = Enumerable.Range(0, 4).Select(i =>
            new GridRow(new Dictionary<string, object?> { ["Code"] = $"R{i}" })).ToList();
        var request = ThreeRowRequest() with { Rows = rows };
        var sut = BuildSut(maxRows: 4, new CsvGridExportRenderer());

        var result = await sut.ExportAsync(request, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_NoRendererRegisteredForFormat_ReturnsFormatNotSupported()
    {
        // The exporter only knows about CSV — asking for XLSX must fail.
        var sut = BuildSut(new CsvGridExportRenderer());

        var result = await sut.ExportAsync(ThreeRowRequest(), ExportFormat.Xlsx);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ExportFormatNotSupported);
    }

    [Fact]
    public async Task ExportAsync_IncrementsRequestedCounter_WithGridAndFormatTags()
    {
        // The counter MUST fire on every call regardless of outcome; verify on the
        // happy path and assert the bounded-cardinality tags.
        using var capture = new MetricCapture("cnas.grid_export.requested");
        var sut = BuildSut(new CsvGridExportRenderer());

        var result = await sut.ExportAsync(ThreeRowRequest(), ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        capture.TotalIncrement.Should().Be(1, "exactly one export was requested.");
        capture.Measurements.Should().HaveCount(1);
        var tags = capture.Measurements[0].Tags;
        tags.Should().Contain(t => t.Key == "grid" && (string?)t.Value == "Solicitants");
        tags.Should().Contain(t => t.Key == "format" && (string?)t.Value == "csv");
    }

    /// <summary>
    /// MeterListener-based capture for a single instrument name on
    /// <see cref="CnasMeter.MeterName"/>. Disposes the listener at the end of the test
    /// so the next test starts from a clean slate.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measurement> _measurements = new();
        private readonly object _gate = new();

        public IReadOnlyList<Measurement> Measurements
        {
            get { lock (_gate) return _measurements.ToList(); }
        }

        public long TotalIncrement
        {
            get { lock (_gate) return _measurements.Sum(m => m.Value); }
        }

        public MetricCapture(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName
                        && instrument.Name == instrumentName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (_gate)
                {
                    _measurements.Add(new Measurement(value, tags.ToArray()));
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();

        public sealed record Measurement(long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags);
    }
}
