using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Exports;

/// <summary>
/// R0226 / TOR UI 013 — canonical wiring of the universal grid-export pipeline
/// for the Solicitant registry. Accepts the same filter envelope as
/// <c>ISolicitantService.ListAsync</c>, consults the same query-budget guard,
/// projects the result through <see cref="SolicitantGridAdapter"/>, and routes
/// the bytes through <see cref="IGridExporter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated service per registry.</b> Each registry has its own
/// query-budget registry name, its own filter DTO, its own permission gate, and
/// its own list projection. Hiding these behind a single generic
/// <see cref="IGridExporter"/> method would either re-invent generics in
/// runtime code (slow + error-prone) or smuggle the registry-specific
/// vocabulary into the generic façade. One adapter + one orchestrator per
/// registry keeps each call site explicit.
/// </para>
/// <para>
/// <b>Result-envelope plumbing.</b> Both the budget failure
/// (<see cref="ErrorCodes.QueryTooBroad"/>) and the row-cap failure
/// (<see cref="ErrorCodes.ExportTooLarge"/>) plus the renderer-missing failure
/// (<see cref="ErrorCodes.ExportFormatNotSupported"/>) bubble up to the
/// controller as plain <see cref="Result{T}"/> failures so the API layer can
/// map each code to the appropriate HTTP status without re-inspecting the
/// service internals.
/// </para>
/// <para>
/// <b>Sqid invariant.</b> Inbound filters never carry ids — the
/// <see cref="SolicitantListQueryInput"/> shape is filter-only. Outbound rows
/// are encoded through the adapter so the exported file never contains raw
/// numeric primary keys (RULE 3).
/// </para>
/// </remarks>
public interface ISolicitantGridExportService
{
    /// <summary>
    /// Exports the filtered + sorted Solicitant list to the requested
    /// <paramref name="format"/>.
    /// </summary>
    /// <param name="input">Same filter envelope used by the list endpoint.</param>
    /// <param name="format">Output format.</param>
    /// <param name="language">ISO-639-1 language code; default <c>"ro"</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the rendered bytes + MIME type + filename. On failure one of:
    /// <list type="bullet">
    ///   <item><see cref="ErrorCodes.QueryTooBroad"/> — budget guard refused; verdict on <see cref="LastBudgetVerdict"/>.</item>
    ///   <item><see cref="ErrorCodes.ExportTooLarge"/> — row cap exceeded.</item>
    ///   <item><see cref="ErrorCodes.ExportFormatNotSupported"/> — no renderer for <paramref name="format"/>.</item>
    /// </list>
    /// </returns>
    Task<Result<GridExportResult>> ExportAsync(
        SolicitantListQueryInput input,
        ExportFormat format,
        string? language = "ro",
        CancellationToken ct = default);

    /// <summary>
    /// Most-recent budget verdict produced by <see cref="ExportAsync"/> on this
    /// service instance. <c>null</c> when no export call has been made yet, or
    /// when the most-recent call returned before reaching the budget guard.
    /// The controller reads this to populate <c>extensions["budget"]</c> on a
    /// 422 ProblemDetails response.
    /// </summary>
    QueryBudgetVerdict? LastBudgetVerdict { get; }
}
