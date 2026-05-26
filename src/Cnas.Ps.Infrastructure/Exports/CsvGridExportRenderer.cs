using System.Buffers;
using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Exports;

/// <summary>
/// R0226 / TOR UI 013 — CSV implementation of <see cref="IGridExportRenderer"/>.
/// Emits a UTF-8 file with byte-order mark (so Excel detects the encoding
/// without a manual import-wizard step) and RFC 4180 quoting for cells
/// containing commas, double quotes, CR, or LF.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a bespoke writer.</b> CsvHelper exists in the codebase but a full
/// library is overkill for the linear schema we have here — the formatting
/// rules are mechanical and a header-driven, in-memory writer that follows the
/// established <c>PublicCatalogCsvWriter</c> shape from R0505 keeps the
/// dependency surface flat and the diagnostics easier.
/// </para>
/// <para>
/// <b>Thread-safe + stateless.</b> No instance state — DI-registered as a
/// singleton. Multiple concurrent requests share one renderer.
/// </para>
/// </remarks>
public sealed class CsvGridExportRenderer : IGridExportRenderer
{
    /// <summary>UTF-8 BOM bytes (Excel uses these to detect the encoding).</summary>
    private static readonly byte[] s_utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };

    /// <summary>
    /// Cached "needs RFC 4180 quoting" character set. Created once and reused
    /// on every cell to avoid per-call allocation (CA1870).
    /// </summary>
    private static readonly SearchValues<char> s_quoteTriggers = SearchValues.Create(",\"\r\n");

    /// <summary>Clock abstraction used to stamp the suggested filename.</summary>
    private readonly ICnasTimeProvider _clock;

    /// <summary>
    /// Builds a CSV renderer with the supplied clock. Use the parameterless
    /// constructor in tests / contexts that don't need a deterministic
    /// timestamp — the default <see cref="SystemTimeProvider"/> is wired
    /// in production via DI.
    /// </summary>
    /// <param name="clock">Clock used to stamp the suggested filename.</param>
    public CsvGridExportRenderer(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>
    /// Convenience parameterless constructor that wires
    /// <see cref="SystemTimeProvider"/>. Used by unit tests that don't care
    /// about the filename timestamp value.
    /// </summary>
    public CsvGridExportRenderer() : this(new SystemTimeProvider()) { }

    /// <inheritdoc />
    public ExportFormat Format => ExportFormat.Csv;

    /// <inheritdoc />
    public Task<Result<GridExportResult>> RenderAsync(
        GridExportRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var sb = new StringBuilder();

        // Header row — header text is already localised by the caller (adapter).
        for (int i = 0; i < request.Columns.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(Quote(request.Columns[i].Header));
        }
        sb.AppendLine();

        // Data rows — per-cell projection driven by the column's data type.
        foreach (var row in request.Rows)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < request.Columns.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                var column = request.Columns[i];
                row.Cells.TryGetValue(column.FieldName, out var raw);
                sb.Append(Quote(FormatCell(raw, column.DataType, request.Language)));
            }
            sb.AppendLine();
        }

        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var combined = new byte[s_utf8Bom.Length + body.Length];
        Buffer.BlockCopy(s_utf8Bom, 0, combined, 0, s_utf8Bom.Length);
        Buffer.BlockCopy(body, 0, combined, s_utf8Bom.Length, body.Length);

        var fileName = SuggestFileName(request.GridName, _clock.UtcNow);
        var resultValue = new GridExportResult(
            Content: combined,
            ContentType: "text/csv; charset=utf-8",
            SuggestedFileName: fileName);
        return Task.FromResult(Result<GridExportResult>.Success(resultValue));
    }

    /// <summary>
    /// Projects a raw cell value to the string form appropriate for the
    /// requested <see cref="GridColumnDataType"/>. Locale-dependent only for
    /// <see cref="GridColumnDataType.Boolean"/> (yes/no localisation per TOR
    /// UI 013); all other types use the invariant culture.
    /// </summary>
    /// <param name="raw">Cell value from <see cref="GridRow.Cells"/>; nullable.</param>
    /// <param name="type">Column data type.</param>
    /// <param name="language">Caller-supplied language code (already normalised upstream).</param>
    /// <returns>The formatted cell string; empty when the raw value is null.</returns>
    internal static string FormatCell(object? raw, GridColumnDataType type, string language)
    {
        if (raw is null)
        {
            return string.Empty;
        }
        switch (type)
        {
            case GridColumnDataType.Date:
                if (raw is DateTime d)
                {
                    return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                if (raw is DateTimeOffset dto)
                {
                    return dto.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                break;

            case GridColumnDataType.DateTime:
                if (raw is DateTime dt)
                {
                    var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
                    return utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                }
                if (raw is DateTimeOffset dto2)
                {
                    return dto2.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                }
                break;

            case GridColumnDataType.Boolean:
                if (raw is bool b)
                {
                    return LocaliseBool(b, language);
                }
                break;

            case GridColumnDataType.Integer:
                return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;

            case GridColumnDataType.Decimal:
                if (raw is decimal dec)
                {
                    return dec.ToString(CultureInfo.InvariantCulture);
                }
                if (raw is double dbl)
                {
                    return dbl.ToString(CultureInfo.InvariantCulture);
                }
                return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;

            case GridColumnDataType.Text:
            default:
                break;
        }
        return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// Resolves the locale-appropriate yes/no pair for a boolean cell. RO is
    /// the default; unknown locales fall back to RO so a forgotten code does
    /// not surface as raw <c>"True"</c> / <c>"False"</c>.
    /// </summary>
    /// <param name="value">Boolean cell value.</param>
    /// <param name="language">Caller-supplied language code.</param>
    /// <returns>Localised yes/no string.</returns>
    internal static string LocaliseBool(bool value, string language)
    {
        return (language?.ToLowerInvariant(), value) switch
        {
            ("en", true)  => "Yes",
            ("en", false) => "No",
            ("ru", true)  => "Да",
            ("ru", false) => "Нет",
            (_,    true)  => "Da",
            (_,    false) => "Nu",
        };
    }

    /// <summary>
    /// Applies RFC 4180 quoting to <paramref name="value"/>: wraps the cell in
    /// double quotes when it contains a comma, double quote, CR, or LF;
    /// embedded double quotes are escaped by doubling them.
    /// </summary>
    /// <param name="value">Cell value (never null — caller substitutes empty string).</param>
    /// <returns>The quoted (or unquoted, when safe) cell string.</returns>
    internal static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var needsQuoting = value.AsSpan().ContainsAny(s_quoteTriggers);
        if (!needsQuoting)
        {
            return value;
        }
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    /// <summary>
    /// Builds the suggested filename: <c>{GridName}-yyyyMMdd-HHmm.csv</c> using
    /// the supplied UTC instant. Filename timestamps are stable across locales
    /// (no locale-dependent month names).
    /// </summary>
    /// <param name="gridName">Grid identifier from the request.</param>
    /// <param name="nowUtc">Clock-supplied current UTC instant.</param>
    /// <returns>Suggested filename.</returns>
    internal static string SuggestFileName(string gridName, DateTime nowUtc)
    {
        var stamp = nowUtc.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return $"{gridName}-{stamp}.csv";
    }
}
