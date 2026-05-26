using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Exports;

/// <summary>
/// R0226 / TOR UI 013 — input row shape consumed by
/// <see cref="SolicitantGridAdapter"/>. The Solicitant list façade
/// (<c>SolicitantService</c>) projects database rows onto this DTO before
/// handing them to the exporter; the adapter then redacts PII (when needed)
/// and maps the projection to the universal <see cref="GridRow"/> grammar.
/// </summary>
/// <param name="Id">Internal database id; encoded to Sqid by the adapter for the export.</param>
/// <param name="NationalIdHash">
/// Deterministic HMAC-SHA256 of the canonicalised IDNP / IDNO. Already a hash —
/// the adapter further redacts it for the export by emitting only the first
/// eight characters followed by an ellipsis so an exported spreadsheet cannot
/// be used to bootstrap a rainbow-table lookup against the full hash.
/// </param>
/// <param name="DisplayName">
/// Solicitant display name (full name for natural persons, denumire for legal
/// persons). PII — replaced with <c>"[masked]"</c> when the caller lacks
/// the PII-viewing permission.
/// </param>
/// <param name="Kind">
/// Classification (<c>NaturalPerson</c> / <c>LegalPerson</c>). Not PII.
/// </param>
/// <param name="CreatedAtUtc">Registration timestamp (UTC).</param>
/// <param name="IsActive">
/// Soft-delete flag — rendered as <c>"Active"</c> / <c>"Inactive"</c> in the
/// export's <c>Status</c> column.
/// </param>
public sealed record SolicitantGridRow(
    long Id,
    string NationalIdHash,
    string DisplayName,
    string Kind,
    System.DateTime CreatedAtUtc,
    bool IsActive);

/// <summary>
/// R0226 / TOR UI 013 — canonical <see cref="IGridExportSourceAdapter{T}"/>
/// implementation for the Solicitant registry. Emits six columns in the order
/// the UI's list view shows them: <c>Code</c>, <c>NationalIdHash</c>,
/// <c>Name</c>, <c>Kind</c>, <c>CreatedAtUtc</c>, <c>Status</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>PII redaction.</b> The Solicitant registry's most-sensitive columns are
/// <see cref="SolicitantGridRow.DisplayName"/> (a person's name) and the
/// IDNP/IDNO hash. The adapter takes a <c>canViewPii</c> boolean from the
/// caller — wired from <c>ICallerContext.Roles</c> in
/// <c>SolicitantGridExportService</c> — and emits <c>"[masked]"</c> for the
/// name column when the caller is a baseline <c>cnas-user</c>. The hash column
/// is always truncated to the first eight characters
/// (<c>"abcd1234…"</c>) even when PII access is granted, so the exported file
/// cannot be repurposed as a deterministic-hash dictionary.
/// </para>
/// <para>
/// <b>Sqid invariant.</b> The <c>Code</c> column carries the Sqid-encoded
/// database id — never the raw <see cref="long"/>. Mirrors the wire DTO
/// (<c>SolicitantListItem.Id</c>).
/// </para>
/// <para>
/// <b>Locale-aware headers.</b> Headers are looked up from a small in-class
/// table keyed by ISO-639-1 code (<c>ro</c>/<c>en</c>/<c>ru</c>). Unknown
/// codes fall back to RO.
/// </para>
/// </remarks>
public sealed class SolicitantGridAdapter : IGridExportSourceAdapter<SolicitantGridRow>
{
    /// <summary>Sentinel value emitted when the display name is masked.</summary>
    public const string MaskedSentinel = "[masked]";

    /// <summary>Sentinel suffix appended to the truncated national-id hash.</summary>
    public const string HashEllipsis = "…";

    /// <summary>Number of hash characters emitted before the ellipsis.</summary>
    public const int HashVisibleChars = 8;

    /// <inheritdoc />
    public IReadOnlyList<GridColumn> Columns(string language)
    {
        var lang = NormaliseLanguage(language);
        return new GridColumn[]
        {
            new(Header: Header(lang, "Code"),            FieldName: "Code",         DataType: GridColumnDataType.Text),
            new(Header: Header(lang, "NationalIdHash"),  FieldName: "NationalIdHash", DataType: GridColumnDataType.Text),
            new(Header: Header(lang, "Name"),            FieldName: "Name",         DataType: GridColumnDataType.Text),
            new(Header: Header(lang, "Kind"),            FieldName: "Kind",         DataType: GridColumnDataType.Text),
            new(Header: Header(lang, "CreatedAtUtc"),    FieldName: "CreatedAtUtc", DataType: GridColumnDataType.DateTime),
            new(Header: Header(lang, "Status"),          FieldName: "Status",       DataType: GridColumnDataType.Text),
        };
    }

    /// <inheritdoc />
    public GridRow ToRow(SolicitantGridRow item, ISqidService sqids, bool canViewPii)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sqids);

        // RULE 3 — raw long never leaves the system; encode to Sqid for the Code column.
        var code = sqids.Encode(item.Id);

        // Defense-in-depth: even with PII access the deterministic hash is
        // truncated so the exported file is not a useful precomputed table.
        var hash = TruncateHash(item.NationalIdHash);

        // PII gate — mask the human-readable display name for baseline users.
        var name = canViewPii ? item.DisplayName : MaskedSentinel;

        // Status surfaces the soft-delete flag in a locale-agnostic way; the
        // renderer doesn't translate the value because the underlying TOR
        // glossary keeps these tokens in English across all locales.
        var status = item.IsActive ? "Active" : "Inactive";

        var cells = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Code"]           = code,
            ["NationalIdHash"] = hash,
            ["Name"]           = name,
            ["Kind"]           = item.Kind,
            ["CreatedAtUtc"]   = item.CreatedAtUtc,
            ["Status"]         = status,
        };
        return new GridRow(cells);
    }

    /// <summary>
    /// Truncates the supplied deterministic hash to the first
    /// <see cref="HashVisibleChars"/> characters followed by an ellipsis. A
    /// shorter input is returned as-is (no padding). Empty / null hashes
    /// surface as an empty string so the export shows a blank cell rather
    /// than the ellipsis sentinel.
    /// </summary>
    /// <param name="hash">Raw base64-encoded hash; nullable for defensive callers.</param>
    /// <returns>The truncated hash.</returns>
    private static string TruncateHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return string.Empty;
        }
        if (hash.Length <= HashVisibleChars)
        {
            return hash;
        }
        return string.Concat(hash.AsSpan(0, HashVisibleChars), HashEllipsis);
    }

    /// <summary>
    /// Resolves a column key to a localised header. Unknown languages fall
    /// back to RO; unknown keys fall back to the key itself so a forgotten
    /// translation surfaces as a visible breadcrumb rather than a blank cell.
    /// </summary>
    /// <param name="language">Already-normalised ISO-639-1 code.</param>
    /// <param name="key">Column key (matches <see cref="GridColumn.FieldName"/>).</param>
    /// <returns>The localised header.</returns>
    private static string Header(string language, string key)
    {
        return (language, key) switch
        {
            ("ro", "Code")           => "Cod",
            ("ro", "NationalIdHash") => "Hash IDNP/IDNO",
            ("ro", "Name")           => "Nume",
            ("ro", "Kind")           => "Tip",
            ("ro", "CreatedAtUtc")   => "Creat la (UTC)",
            ("ro", "Status")         => "Stare",

            ("en", "Code")           => "Code",
            ("en", "NationalIdHash") => "National ID Hash",
            ("en", "Name")           => "Name",
            ("en", "Kind")           => "Kind",
            ("en", "CreatedAtUtc")   => "Created (UTC)",
            ("en", "Status")         => "Status",

            ("ru", "Code")           => "Код",
            ("ru", "NationalIdHash") => "Хеш IDNP/IDNO",
            ("ru", "Name")           => "Имя",
            ("ru", "Kind")           => "Тип",
            ("ru", "CreatedAtUtc")   => "Создано (UTC)",
            ("ru", "Status")         => "Статус",

            _ => key,
        };
    }

    /// <summary>
    /// Normalises a caller-supplied language code to one of the supported set
    /// (<c>"ro"</c>/<c>"en"</c>/<c>"ru"</c>). Unknown / null codes resolve to
    /// the default <c>"ro"</c>.
    /// </summary>
    /// <param name="value">Caller-supplied language code.</param>
    /// <returns>One of <c>"ro"</c>, <c>"en"</c>, <c>"ru"</c>.</returns>
    private static string NormaliseLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "ro";
        }
        var lower = value.Trim().ToLowerInvariant();
        return lower switch
        {
            "ro" or "en" or "ru" => lower,
            _ => "ro",
        };
    }
}
