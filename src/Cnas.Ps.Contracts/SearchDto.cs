namespace Cnas.Ps.Contracts;

/// <summary>
/// QBE/global search input for UC03 / UC12. Honours UI 009-012: full-text, wildcard masks
/// (<c>1003600043*</c>, <c>*ESCU</c>, <c>*ASIGUR*</c>), and field-level filters.
/// </summary>
/// <param name="Query">Free-text query — applied as a full-text match.</param>
/// <param name="Filters">Per-field equality filters. Keys are entity field names.</param>
/// <param name="Mask">Wildcard mask (UI 012) applied to the lookup column where supplied.</param>
/// <param name="SortBy">Field to sort by; null defaults to relevance.</param>
/// <param name="SortDescending">True for descending order.</param>
/// <param name="Page">Pagination request.</param>
public sealed record SearchRequest(
    string? Query,
    IReadOnlyDictionary<string, string>? Filters,
    string? Mask,
    string? SortBy,
    bool SortDescending,
    PageRequest Page);

/// <summary>A row in a search-result grid. Generic shape — concrete UCs project entity columns.</summary>
public sealed record SearchRow(string Id, IReadOnlyDictionary<string, string> Columns);

/// <summary>Available export formats for grid data per UI 013.</summary>
public enum ExportFormat
{
    /// <summary>Comma-separated values.</summary>
    Csv = 0,

    /// <summary>OpenXML Workbook (.xlsx).</summary>
    Xlsx = 1,

    /// <summary>Portable Document Format.</summary>
    Pdf = 2,
}
