using Cnas.Ps.Application.Exports;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Exports;

/// <summary>
/// R0226 / TOR UI 013 — production implementation of <see cref="IGridExporter"/>.
/// Routes export requests to a registered <see cref="IGridExportRenderer"/>,
/// enforces a per-call row cap, and emits a
/// <c>cnas.grid_export.requested</c> counter on every call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Renderer table.</b> The collection of <see cref="IGridExportRenderer"/>
/// implementations is reduced to a dictionary keyed by
/// <see cref="IGridExportRenderer.Format"/> at construction time so dispatch
/// is O(1) per call. A duplicate registration loses to the FIRST entry — DI
/// composition is responsible for keeping the set unique.
/// </para>
/// <para>
/// <b>Row cap.</b> The <see cref="DefaultMaxExportRows"/> default of 50 000
/// mirrors the spec from R0226. The cap is checked BEFORE the renderer is
/// invoked so a too-large request never causes the renderer to allocate. The
/// cap is inclusive — a request with exactly <see cref="DefaultMaxExportRows"/>
/// rows still succeeds.
/// </para>
/// </remarks>
public sealed class GridExporter : IGridExporter
{
    /// <summary>Default per-call row cap.</summary>
    public const int DefaultMaxExportRows = 50_000;

    /// <summary>Per-format renderer lookup, built at construction time.</summary>
    private readonly IReadOnlyDictionary<ExportFormat, IGridExportRenderer> _renderers;

    /// <summary>Configured per-call row cap.</summary>
    private readonly int _maxExportRows;

    /// <summary>Structured logger.</summary>
    private readonly ILogger<GridExporter> _logger;

    /// <summary>
    /// Constructs the exporter. The DI container resolves every
    /// <see cref="IGridExportRenderer"/> registration into the
    /// <paramref name="renderers"/> collection automatically.
    /// </summary>
    /// <param name="renderers">All registered renderers (one per supported format).</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="maxExportRows">Override for the row cap; default 50 000.</param>
    public GridExporter(
        IEnumerable<IGridExportRenderer> renderers,
        ILogger<GridExporter> logger,
        int maxExportRows = DefaultMaxExportRows)
    {
        ArgumentNullException.ThrowIfNull(renderers);
        ArgumentNullException.ThrowIfNull(logger);
        if (maxExportRows < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxExportRows),
                maxExportRows,
                "MaxExportRows must be at least 1.");
        }

        var table = new Dictionary<ExportFormat, IGridExportRenderer>();
        foreach (var renderer in renderers)
        {
            // First-in wins so a misconfigured composition root cannot silently
            // shadow the production renderer; the duplicate is logged below.
            if (!table.TryAdd(renderer.Format, renderer))
            {
                logger.LogWarning(
                    "Duplicate IGridExportRenderer registration for format {Format}; keeping the first.",
                    renderer.Format);
            }
        }
        _renderers = table;
        _maxExportRows = maxExportRows;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<GridExportResult>> ExportAsync(
        GridExportRequest request,
        ExportFormat format,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Counter — emit BEFORE the row-cap / renderer-lookup gates so the
        //    observable rate reflects user demand, not the gates' refusal
        //    pattern. The tags are bounded-cardinality (grid name from a
        //    closed registry list, format from the ExportFormat enum).
        CnasMeter.GridExportRequested.Add(
            1,
            new KeyValuePair<string, object?>("grid", request.GridName),
            new KeyValuePair<string, object?>("format", FormatTag(format)));

        // 2. Row cap — defense-in-depth. Reject before the renderer allocates.
        if (request.Rows.Count > _maxExportRows)
        {
            _logger.LogWarning(
                "Grid export rejected: row count {RowCount} exceeds cap {Cap} for grid {Grid}.",
                request.Rows.Count, _maxExportRows, request.GridName);
            return Result<GridExportResult>.Failure(
                ErrorCodes.ExportTooLarge,
                $"Row count {request.Rows.Count} exceeds the configured cap of {_maxExportRows}.");
        }

        // 3. Renderer dispatch — fail with EXPORT_FORMAT_NOT_SUPPORTED when no
        //    renderer is registered for the requested format. Distinct from a
        //    generic NotImplemented so the API layer can map to HTTP 501.
        if (!_renderers.TryGetValue(format, out var renderer))
        {
            _logger.LogWarning(
                "Grid export rejected: no renderer registered for format {Format} (grid {Grid}).",
                format, request.GridName);
            return Result<GridExportResult>.Failure(
                ErrorCodes.ExportFormatNotSupported,
                $"No renderer is registered for export format '{FormatTag(format)}'.");
        }

        return await renderer.RenderAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps an <see cref="ExportFormat"/> to its short, lowercased tag used in
    /// metric tags and ProblemDetails extensions.
    /// </summary>
    /// <param name="format">Format value.</param>
    /// <returns>Short tag — <c>csv</c> / <c>xlsx</c> / <c>pdf</c>.</returns>
    internal static string FormatTag(ExportFormat format) =>
        format switch
        {
            ExportFormat.Csv  => "csv",
            ExportFormat.Xlsx => "xlsx",
            ExportFormat.Pdf  => "pdf",
            _ => format.ToString().ToLowerInvariant(),
        };
}
