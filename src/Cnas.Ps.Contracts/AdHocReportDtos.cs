using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0580 / TOR CF 09.02 — well-known entity-set discriminators recognised by
/// the ad-hoc report builder. Stable strings; renaming is a breaking change.
/// </summary>
public static class AdHocReportEntitySets
{
    /// <summary>The <c>ServiceApplication</c> registry (citizen applications).</summary>
    public const string Applications = "Applications";

    /// <summary>The <c>Contributor</c> registry (payer accounts).</summary>
    public const string Contributors = "Contributors";

    /// <summary>The <c>Dossier</c> registry (per-application case files).</summary>
    public const string Dossiers = "Dossiers";

    /// <summary>The decision-bearing <c>ServiceApplication</c> rows (Approved / Rejected).</summary>
    public const string Decisions = "Decisions";

    /// <summary>All known entity-set discriminators.</summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> All =
        new System.Collections.Generic.HashSet<string>(
            new[] { Applications, Contributors, Dossiers, Decisions },
            System.StringComparer.Ordinal);
}

/// <summary>
/// R0580 / TOR CF 09.02 — supported filter operators on an ad-hoc report
/// column. Stable strings; the validator accepts case-insensitive input but
/// the canonical form is upper-case.
/// </summary>
public static class AdHocReportOperators
{
    /// <summary>Exact equality (<c>=</c>).</summary>
    public const string Eq = "EQ";

    /// <summary>Inequality (<c>&lt;&gt;</c>).</summary>
    public const string Ne = "NE";

    /// <summary>Greater than or equal (<c>&gt;=</c>).</summary>
    public const string Gte = "GTE";

    /// <summary>Less than or equal (<c>&lt;=</c>).</summary>
    public const string Lte = "LTE";

    /// <summary>Substring match against a string column.</summary>
    public const string Contains = "CONTAINS";

    /// <summary>All known operators.</summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> All =
        new System.Collections.Generic.HashSet<string>(
            new[] { Eq, Ne, Gte, Lte, Contains },
            System.StringComparer.Ordinal);
}

/// <summary>
/// R0580 / TOR CF 09.02 — one filter condition on an ad-hoc report. The
/// value is supplied as a stable string and parsed against the underlying
/// column type by the builder.
/// </summary>
/// <param name="Field">Column name from the entity-set schema.</param>
/// <param name="Operator">One of <see cref="AdHocReportOperators"/>.</param>
/// <param name="Value">Stringified comparison value.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record AdHocReportFilterDto(
    string Field,
    string Operator,
    string Value);

/// <summary>
/// R0580 / TOR CF 09.02 — request body for <c>POST /api/reports/adhoc</c>.
/// </summary>
/// <param name="EntitySet">Entity-set discriminator (see <see cref="AdHocReportEntitySets"/>).</param>
/// <param name="Columns">Ordered list of output columns; must be non-empty and ≤ 20.</param>
/// <param name="Filters">Filter envelope (AND-combined); may be empty.</param>
/// <param name="OrderBy">Optional column to sort by; must appear in <paramref name="Columns"/>.</param>
/// <param name="Descending">When <c>true</c>, sort descending (default ascending).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record AdHocReportSpecDto(
    string EntitySet,
    IReadOnlyList<string> Columns,
    IReadOnlyList<AdHocReportFilterDto> Filters,
    string? OrderBy,
    bool Descending);

/// <summary>
/// R0580 / TOR CF 09.02 — response body for <c>POST /api/reports/adhoc</c>.
/// </summary>
/// <param name="Columns">Echoes back the requested column list in order.</param>
/// <param name="Rows">Materialised rows; each row is a column-name → value map.</param>
/// <param name="RowCount">Total rows materialised (equal to <c>Rows.Count</c>).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record AdHocReportResultDto(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount);
