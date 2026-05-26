using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reporting;

/// <summary>
/// R0529 / TOR CF 03.14 — strategy interface for a single report-export format.
/// The <c>IReportExportSelector</c> implementation looks up the exporter whose
/// <see cref="Format"/> matches the caller-requested
/// <see cref="ReportExportFormat"/> and delegates the byte-level rendering to
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One exporter per format.</b> DI registration is by interface — every
/// concrete <see cref="IReportExporter"/> is registered with the same service
/// type and the selector discriminates on <see cref="Format"/>. New formats
/// are added by adding a new registration in the composition root — no
/// changes to the selector or the controller are required.
/// </para>
/// <para>
/// <b>Stateless and thread-safe.</b> Exporters MUST be safe to invoke
/// concurrently — the DI lifetime is <c>Scoped</c> to match the surrounding
/// controller scope, but the implementations themselves carry no per-call
/// state.
/// </para>
/// <para>
/// <b>Pure projection.</b> Implementations MUST NOT call into the database,
/// the outbound HTTP surface, or any other I/O sink — the input is a
/// materialised matrix and the exporter only transforms it to bytes. This
/// keeps the surface deterministic, easy to unit-test, and free of audit
/// concerns (the controller emits a single audit row for the export action,
/// not one per exporter dispatch).
/// </para>
/// </remarks>
public interface IReportExporter
{
    /// <summary>The export format this exporter handles.</summary>
    ReportExportFormat Format { get; }

    /// <summary>
    /// Renders <paramref name="input"/> to the exporter's wire format.
    /// </summary>
    /// <param name="input">Validated input — caller has already enforced the column and row caps.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the rendered bytes plus the MIME type and file extension the
    /// caller writes onto the HTTP response. On placeholder-state exporters
    /// (e.g. DOCX without the OpenXML SDK loaded) a
    /// <see cref="Result{T}.Failure"/> carrying the corresponding
    /// <see cref="ErrorCodes"/> code.
    /// </returns>
    Task<Result<ReportExportResultDto>> ExportAsync(
        ReportExportInputDto input,
        CancellationToken ct);
}

/// <summary>
/// R0529 / TOR CF 03.14 — top-level façade that routes a
/// <see cref="ReportExportFormat"/> request to the matching
/// <see cref="IReportExporter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dispatch logic.</b> The implementation receives every registered
/// <c>IReportExporter</c> through <c>IEnumerable&lt;IReportExporter&gt;</c>
/// and picks the one whose <see cref="IReportExporter.Format"/> matches the
/// requested format. When no exporter is registered for the requested
/// format the selector returns
/// <see cref="ErrorCodes.ExportFormatNotSupported"/>; the controller maps
/// that to HTTP 501 with the format name in the ProblemDetails extension
/// bag.
/// </para>
/// <para>
/// <b>No DOS guard here.</b> The selector trusts the caller to have already
/// validated the row count via <c>ReportExportInputValidator</c>; the
/// validator's 100_000-row ceiling is the single source of truth for the
/// pipeline budget.
/// </para>
/// </remarks>
public interface IReportExportSelector
{
    /// <summary>
    /// Renders <paramref name="input"/> in the requested <paramref name="format"/>.
    /// </summary>
    /// <param name="format">Desired output format.</param>
    /// <param name="input">Validated input.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the rendered bytes envelope. On failure a
    /// <see cref="Result{T}.Failure"/> carrying
    /// <see cref="ErrorCodes.ExportFormatNotSupported"/> (no exporter for the
    /// requested format) or whatever stable code the underlying exporter
    /// surfaced.
    /// </returns>
    Task<Result<ReportExportResultDto>> ExportAsync(
        ReportExportFormat format,
        ReportExportInputDto input,
        CancellationToken ct);
}
