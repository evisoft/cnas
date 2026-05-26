using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Exports;

/// <summary>
/// R0226 / TOR UI 013 — adapter that turns a list of registry items into the
/// generic <see cref="GridColumn"/> / <see cref="GridRow"/> grammar consumed by
/// <see cref="IGridExporter"/>.
/// </summary>
/// <typeparam name="TItem">
/// The list-item DTO produced by the registry's list service (e.g.
/// <c>SolicitantListItem</c>, <c>CerereListItem</c>, ...).
/// </typeparam>
/// <remarks>
/// <para>
/// <b>One adapter per registry.</b> Each adapter is the single source of truth
/// for which columns appear in the export of that registry, in which order, and
/// with which data types. The same adapter is consumed by the CSV, XLSX, and
/// PDF renderers — there is no per-format adapter.
/// </para>
/// <para>
/// <b>Sqid invariant.</b> Adapters receive <see cref="ISqidService"/> so they
/// can encode raw long ids into Sqid strings before placing them in the
/// <see cref="GridRow.Cells"/> map. Raw <see cref="long"/> / <see cref="int"/>
/// primary keys MUST NOT appear in the exported file (CLAUDE.md RULE 3).
/// </para>
/// <para>
/// <b>Per-call permission context.</b> Some adapters mask sensitive columns
/// based on the caller's permissions (e.g. <c>SolicitantGridAdapter</c> masks
/// the display-name column when the caller lacks the
/// <c>Solicitant.ViewPii</c> permission). Permission resolution lives in the
/// adapter, not the renderer — the renderer never sees the caller-context
/// abstraction (<c>Cnas.Ps.Application.Abstractions.ICallerContext</c>).
/// </para>
/// </remarks>
public interface IGridExportSourceAdapter<TItem>
{
    /// <summary>
    /// The ordered column definitions for this registry. Already localised to
    /// the request language by the adapter.
    /// </summary>
    /// <param name="language">ISO-639-1 language code (<c>ro</c>/<c>en</c>/<c>ru</c>).</param>
    /// <returns>Immutable column list.</returns>
    IReadOnlyList<GridColumn> Columns(string language);

    /// <summary>
    /// Projects a single list-item DTO into a <see cref="GridRow"/> whose cells
    /// match the schema returned by <see cref="Columns"/>.
    /// </summary>
    /// <param name="item">Source DTO.</param>
    /// <param name="sqids">
    /// Sqid encoder; the adapter uses it to convert raw long primary keys into
    /// the Sqid strings emitted into the file (RULE 3).
    /// </param>
    /// <param name="canViewPii">
    /// True when the caller has the PII-viewing permission for this registry;
    /// false otherwise. Adapters mask sensitive columns (e.g. display name)
    /// when this is false.
    /// </param>
    /// <returns>The materialised grid row.</returns>
    GridRow ToRow(TItem item, ISqidService sqids, bool canViewPii);
}
