using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Exports;

/// <summary>
/// R0226 / TOR UI 013 — strategy interface for a single export format. The
/// <see cref="IGridExporter"/> implementation looks up the renderer whose
/// <see cref="Format"/> matches the caller-requested
/// <see cref="ExportFormat"/> and delegates the byte-level rendering to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One renderer per format.</b> DI registration is by interface — every
/// concrete <see cref="IGridExportRenderer"/> is registered with the same
/// service type and the exporter discriminates on <see cref="Format"/>. This is
/// the pattern used elsewhere in the codebase
/// (e.g. <c>IDocxTemplate</c> / <c>DocumentGenerationService</c>) so consumers
/// can add a new format by adding a new registration in the composition root.
/// </para>
/// <para>
/// <b>Stateless and thread-safe.</b> Renderers MUST be safe to invoke
/// concurrently — the DI lifetime is <c>Singleton</c>.
/// </para>
/// </remarks>
public interface IGridExportRenderer
{
    /// <summary>The export format this renderer handles.</summary>
    ExportFormat Format { get; }

    /// <summary>
    /// Serialises <paramref name="request"/> to the renderer's wire format and
    /// returns the bytes ready for download.
    /// </summary>
    /// <param name="request">The grid contents + presentation metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the rendered bytes plus the MIME type the renderer wants used
    /// on the HTTP response. On placeholder-state renderers (when the
    /// underlying library is intentionally unavailable) a
    /// <see cref="Result{T}.Failure"/> with code
    /// <see cref="ErrorCodes.ExportFormatNotSupported"/>.
    /// </returns>
    Task<Result<GridExportResult>> RenderAsync(
        GridExportRequest request,
        CancellationToken ct = default);
}
