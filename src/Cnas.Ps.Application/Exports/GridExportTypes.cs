using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Exports;

/// <summary>
/// R0226 / TOR UI 013 — declarative grammar for the universal grid-export pipeline.
/// A <see cref="GridColumn"/> carries the column header (already localised by the
/// caller), the field name used to look up the cell value on each
/// <see cref="GridRow"/>, and the runtime <see cref="GridColumnDataType"/> the
/// renderer uses to format the value (date as ISO, decimal as invariant culture,
/// boolean as locale-appropriate yes/no, ...).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why columns carry the formatting hint, not the row values.</b> Adapters
/// emit cell values as raw .NET primitives (string, int, decimal, DateTime, bool)
/// and the renderer applies the format. This keeps adapters dumb — they project a
/// dictionary from an entity row — and concentrates locale / format logic in one
/// place. A CSV renderer and an XLSX renderer use the same <see cref="DataType"/>
/// to decide how to project the value into their respective wire format
/// (CSV: ISO string; XLSX: native typed cell with number/date format applied).
/// </para>
/// <para>
/// <b>Order matters.</b> The columns are emitted in the order they appear in the
/// list; the adapter is responsible for the canonical column order. The renderer
/// MUST NOT re-sort columns.
/// </para>
/// </remarks>
/// <param name="Header">
/// Already-localised header text shown to the end user (e.g. <c>"Cod"</c> /
/// <c>"Code"</c> / <c>"Код"</c>). Adapters resolve the localised string via the
/// platform's <c>IStringLocalizer</c> before assembling the request.
/// </param>
/// <param name="FieldName">
/// Stable, machine-readable key used to look up the cell value in
/// <see cref="GridRow.Cells"/>. By convention upper-camel-case matching the
/// underlying DTO field name (e.g. <c>"Code"</c>, <c>"CreatedAtUtc"</c>).
/// </param>
/// <param name="DataType">
/// Runtime data-type tag the renderer uses to format the cell value. See
/// <see cref="GridColumnDataType"/> for the closed set.
/// </param>
public sealed record GridColumn(string Header, string FieldName, GridColumnDataType DataType);

/// <summary>
/// R0226 — runtime data-type tag for a <see cref="GridColumn"/>. The renderer
/// consumes this to format the cell value (CSV: ISO-style strings; XLSX: native
/// typed cells; PDF: locale-appropriate text). Closed set — adding a new member
/// is a renderer-contract change.
/// </summary>
/// <remarks>
/// <para>
/// CA1720 (Identifier contains type name) is suppressed on this enum because
/// the member names ARE the canonical TOR UI 013 data-type labels — renaming
/// <c>Integer</c> / <c>Decimal</c> / <c>DateTime</c> / <c>Boolean</c> would
/// invert the natural mapping from the wire spec to the runtime tag and make
/// every call site less readable. The names never appear as parameter names
/// (which is what CA1720 actually targets at runtime).
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Enum members mirror the TOR UI 013 data-type vocabulary verbatim; renaming would obscure the contract.")]
public enum GridColumnDataType
{
    /// <summary>Free-text — emitted verbatim. The default for unknown shapes.</summary>
    Text = 0,

    /// <summary>Whole-number integer; formatted with the invariant culture.</summary>
    Integer = 1,

    /// <summary>Fixed-point decimal; formatted with the invariant culture.</summary>
    Decimal = 2,

    /// <summary>Date only (no time component); formatted as ISO <c>yyyy-MM-dd</c>.</summary>
    Date = 3,

    /// <summary>UTC date + time; formatted as ISO <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    DateTime = 4,

    /// <summary>Boolean; formatted as locale-appropriate yes/no.</summary>
    Boolean = 5,
}

/// <summary>
/// R0226 — a single materialised grid row. Cells are looked up by
/// <see cref="GridColumn.FieldName"/>; missing keys render as empty strings.
/// </summary>
/// <remarks>
/// The cell value type is <c>object?</c> because the renderer applies the
/// per-column <see cref="GridColumnDataType"/> formatting. Adapters MUST supply
/// values whose runtime type is compatible with the column's data type (string
/// for <see cref="GridColumnDataType.Text"/>; <see cref="int"/> / <see cref="long"/>
/// for <see cref="GridColumnDataType.Integer"/>; <see cref="decimal"/> for
/// <see cref="GridColumnDataType.Decimal"/>; <see cref="System.DateTime"/> for
/// <see cref="GridColumnDataType.Date"/> / <see cref="GridColumnDataType.DateTime"/>;
/// <see cref="bool"/> for <see cref="GridColumnDataType.Boolean"/>). The CSV
/// renderer falls back to <c>Convert.ToString(InvariantCulture)</c> on a type
/// mismatch — defense in depth rather than a contract.
/// </remarks>
/// <param name="Cells">
/// Map from field name to raw cell value. Use an
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/> so adapters can return shared
/// instances safely (the exporter never mutates the map).
/// </param>
public sealed record GridRow(IReadOnlyDictionary<string, object?> Cells);

// R0226 — the supported-formats enum lives in Cnas.Ps.Contracts.ExportFormat
// (shared with Reports / Search subsystems). See Cnas.Ps.Contracts.SearchDto
// for the canonical declaration. Re-using it keeps the wire vocabulary
// consistent across CNAS export surfaces (reports, search exports, grids).

/// <summary>
/// R0226 — provider-agnostic envelope handed to <see cref="IGridExporter"/>.
/// Carries the rows + columns to render plus presentation metadata
/// (<see cref="Title"/>, <see cref="FooterNote"/>, <see cref="Language"/>).
/// </summary>
/// <param name="GridName">
/// Stable, machine-readable grid identifier (e.g. <c>"Solicitants"</c>,
/// <c>"Cereri"</c>). Used as a metric tag on the <c>cnas.grid_export.requested</c>
/// counter so operators can chart per-registry export volume. Cardinality is
/// bounded by the number of opted-in grids (≤ 12 in practice).
/// </param>
/// <param name="Columns">
/// Ordered column definitions. The renderer emits columns in this exact order.
/// </param>
/// <param name="Rows">
/// Materialised rows. The caller is responsible for paging / filtering — the
/// exporter renders whatever it is given (subject only to the row-count cap).
/// </param>
/// <param name="Title">
/// Optional document title — rendered as the first sheet name / PDF heading.
/// Ignored by the CSV renderer (CSV has no metadata channel).
/// </param>
/// <param name="FooterNote">
/// Optional footer text — typically a "generated at … by …" line. Ignored by
/// the CSV renderer.
/// </param>
/// <param name="Language">
/// ISO-639-1 language code (<c>"ro"</c> / <c>"en"</c> / <c>"ru"</c>). The
/// renderer uses this to format <see cref="GridColumnDataType.Boolean"/> cells
/// (Da/Nu / Yes/No / Да/Нет) and to localise its hardcoded headings.
/// </param>
public sealed record GridExportRequest(
    string GridName,
    IReadOnlyList<GridColumn> Columns,
    IReadOnlyList<GridRow> Rows,
    string? Title = null,
    string? FooterNote = null,
    string Language = "ro");

/// <summary>
/// R0226 — successful export payload returned by <see cref="IGridExporter"/>.
/// </summary>
/// <param name="Content">Raw bytes; the controller copies these into the file response.</param>
/// <param name="ContentType">
/// MIME type appropriate for the format
/// (e.g. <c>text/csv; charset=utf-8</c>,
/// <c>application/vnd.openxmlformats-officedocument.spreadsheetml.sheet</c>,
/// <c>application/pdf</c>).
/// </param>
/// <param name="SuggestedFileName">
/// File name carried in the <c>Content-Disposition</c> header — already includes
/// the timestamp suffix (e.g. <c>Solicitants-20260521-1042.csv</c>).
/// </param>
public sealed record GridExportResult(byte[] Content, string ContentType, string SuggestedFileName);
