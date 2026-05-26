using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Exports;

/// <summary>
/// R0226 / TOR UI 013 — universal grid-export façade. Every list / grid page in
/// the SI PS portal eventually calls this service to produce a CSV, XLSX, or PDF
/// file of the currently-filtered set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Top-level dispatch.</b> The implementation routes the request to the
/// registered <see cref="IGridExportRenderer"/> for the requested format. When
/// no renderer is registered (e.g. PDF in an environment without the QuestPDF
/// dependency) the call returns
/// <see cref="ErrorCodes.ExportFormatNotSupported"/>; the controller maps that
/// to HTTP 501.
/// </para>
/// <para>
/// <b>Row-count guard.</b> Implementations MUST enforce a per-call row cap so a
/// runaway list cannot exhaust memory or block the request thread. Exceeding the
/// cap returns <see cref="ErrorCodes.ExportTooLarge"/>; the controller maps that
/// to HTTP 422 with the actual row count surfaced for the caller.
/// </para>
/// <para>
/// <b>Side-channel — no PII in metrics.</b> Implementations emit a
/// <c>cnas.grid_export.requested</c> counter tagged with the grid name and
/// format. Cardinality is bounded: grid names come from a closed allow-list
/// (<see cref="GridExportRequest.GridName"/>) and formats from the
/// <see cref="ExportFormat"/> enum.
/// </para>
/// </remarks>
public interface IGridExporter
{
    /// <summary>
    /// Renders <paramref name="request"/> as a byte stream in the requested
    /// <paramref name="format"/>.
    /// </summary>
    /// <param name="request">Grid contents + presentation metadata.</param>
    /// <param name="format">Output format. Maps to a registered renderer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the rendered bytes + MIME type + suggested filename. On
    /// failure a <see cref="Result{T}.Failure"/> whose code is one of
    /// <see cref="ErrorCodes.ExportTooLarge"/> (row cap exceeded) or
    /// <see cref="ErrorCodes.ExportFormatNotSupported"/> (no renderer for the
    /// requested format).
    /// </returns>
    Task<Result<GridExportResult>> ExportAsync(
        GridExportRequest request,
        ExportFormat format,
        CancellationToken ct = default);
}
