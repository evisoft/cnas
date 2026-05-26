using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — production implementation of
/// <see cref="IOfflineBatchRequestParser"/>. Reads a UTF-8 CSV with
/// RFC 4180 quoting and one header row, validates each data row against
/// the op-specific schema, and emits per-row seeds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format rules.</b>
/// <list type="bullet">
///   <item><description>UTF-8 encoded text.</description></item>
///   <item><description>RFC 4180 quoting — commas inside quoted cells are part of the cell; double-quotes inside a quoted cell are escaped as <c>""</c>.</description></item>
///   <item><description>Header row is required and must match the op's <see cref="OfflineBatchOpSchema.RequestHeader"/> column-for-column (case-sensitive).</description></item>
///   <item><description>Blank rows are skipped silently.</description></item>
///   <item><description>Max 10,000 data rows per file.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class OfflineBatchRequestParser : IOfflineBatchRequestParser
{
    /// <summary>Stable error code surfaced when the file exceeds the row cap.</summary>
    public const string TooManyRowsCode = "BATCH.TOO_MANY_ROWS";

    /// <summary>Maximum permitted data rows per file (excluding the header).</summary>
    public const int MaxRowsPerFile = 10_000;

    /// <summary>Stable error code stamped on a per-row parse failure.</summary>
    public const string RowParseErrorCode = ErrorCodes.ValidationFailed;

    private readonly IOfflineBatchOpSchemaRegistry _schemas;

    /// <summary>Constructs the parser.</summary>
    /// <param name="schemas">Per-op schema registry.</param>
    public OfflineBatchRequestParser(IOfflineBatchOpSchemaRegistry schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        _schemas = schemas;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<OfflineBatchRowSeed>>> ParseAsync(
        AnnexFourBatchOp opCode,
        Stream requestStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        var schema = _schemas.Get(opCode);

        using var reader = new StreamReader(requestStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        // First row = header.
        var headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (headerLine is null)
        {
            return Result<IReadOnlyList<OfflineBatchRowSeed>>.Failure(
                ErrorCodes.ValidationFailed, "Request file is empty.");
        }
        var headerCells = ParseCsvLine(headerLine);
        if (!HeaderMatches(headerCells, schema.RequestHeader))
        {
            return Result<IReadOnlyList<OfflineBatchRowSeed>>.Failure(
                ErrorCodes.ValidationFailed,
                $"Request header does not match the expected columns: {string.Join(",", schema.RequestHeader)}.");
        }

        var seeds = new List<OfflineBatchRowSeed>();
        int ordinal = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) { break; }
            if (string.IsNullOrWhiteSpace(line)) { continue; }

            ordinal++;
            if (ordinal > MaxRowsPerFile)
            {
                return Result<IReadOnlyList<OfflineBatchRowSeed>>.Failure(
                    TooManyRowsCode,
                    $"Request file exceeds the {MaxRowsPerFile}-row cap.");
            }

            var cells = ParseCsvLine(line);
            OfflineBatchRowSeed seed;
            try
            {
                var json = schema.ParseRequestRow(cells);
                seed = new OfflineBatchRowSeed(ordinal, json, ParseError: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-row parse failure — record a placeholder seed so the
                // engine can surface it as Failed without halting the file.
                seed = new OfflineBatchRowSeed(
                    ordinal,
                    "{}",
                    new OfflineBatchRowParseError(
                        RowParseErrorCode,
                        Sanitise(ex.Message)));
            }
            seeds.Add(seed);
        }

        return Result<IReadOnlyList<OfflineBatchRowSeed>>.Success(seeds);
    }

    /// <summary>Trims and removes any PII-ish digit sequences (defence-in-depth) from an exception message.</summary>
    /// <param name="message">Raw message.</param>
    /// <returns>Sanitised message bounded to 500 chars.</returns>
    private static string Sanitise(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) { return "Row parse failed."; }
        var trimmed = message.Length > 500 ? message[..500] : message;
        // Defence-in-depth — strip any sequence of 8+ digits (would match IDNP / IDNO).
        var sb = new StringBuilder(trimmed.Length);
        int digitRun = 0;
        int runStart = -1;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (char.IsDigit(trimmed[i]))
            {
                if (digitRun == 0) { runStart = i; }
                digitRun++;
            }
            else
            {
                FlushRun(trimmed, runStart, digitRun, sb);
                digitRun = 0;
                sb.Append(trimmed[i]);
            }
        }
        FlushRun(trimmed, runStart, digitRun, sb);
        return sb.ToString();
    }

    /// <summary>Flushes a pending digit run into <paramref name="sb"/> — replacing 8+ digit sequences with a placeholder.</summary>
    private static void FlushRun(string source, int runStart, int digitRun, StringBuilder sb)
    {
        if (digitRun == 0) { return; }
        if (digitRun >= 8)
        {
            sb.Append("[REDACTED]");
        }
        else
        {
            sb.Append(source, runStart, digitRun);
        }
    }

    /// <summary>Checks that the CSV header cells exactly match the expected schema columns (case-sensitive).</summary>
    /// <param name="cells">Parsed header cells.</param>
    /// <param name="expected">Expected header columns.</param>
    /// <returns><c>true</c> on a column-for-column match.</returns>
    private static bool HeaderMatches(IReadOnlyList<string> cells, IReadOnlyList<string> expected)
    {
        if (cells.Count != expected.Count) { return false; }
        for (int i = 0; i < cells.Count; i++)
        {
            if (!string.Equals(cells[i].Trim(), expected[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// RFC 4180 CSV line parser. Splits on unquoted commas; handles quoted
    /// cells (commas + escaped <c>""</c> sequences). Never throws — malformed
    /// runs simply terminate the cell.
    /// </summary>
    /// <param name="line">Raw text of one CSV line.</param>
    /// <returns>Cells of the line (in column order).</returns>
    public static IReadOnlyList<string> ParseCsvLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        // Escaped quote.
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                switch (c)
                {
                    case ',':
                        cells.Add(sb.ToString());
                        sb.Clear();
                        break;
                    case '"':
                        inQuotes = true;
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
        }
        cells.Add(sb.ToString());
        return cells;
    }

    /// <summary>Pre-computed set of characters that force RFC 4180 quoting on a CSV cell.</summary>
    private static readonly SearchValues<char> CsvQuotingTriggers =
        SearchValues.Create(",\"\r\n");

    /// <summary>
    /// Escapes a single cell value for RFC 4180 CSV output. Wraps in
    /// double-quotes and doubles inner quotes when the cell contains a
    /// comma, quote, or line break.
    /// </summary>
    /// <param name="cell">Raw cell value.</param>
    /// <returns>Escaped cell suitable for concatenation with comma separators.</returns>
    public static string EscapeCsvCell(string? cell)
    {
        if (cell is null) { return string.Empty; }
        var needsQuoting = cell.AsSpan().IndexOfAny(CsvQuotingTriggers) >= 0;
        if (!needsQuoting) { return cell; }
        var escaped = cell.Replace("\"", "\"\"", StringComparison.Ordinal);
        return string.Concat("\"", escaped, "\"");
    }

    /// <summary>Joins the supplied cells into a single CSV row, applying RFC 4180 escaping.</summary>
    /// <param name="cells">Cells in column order.</param>
    /// <returns>The CSV row as a string (without a trailing newline).</returns>
    public static string FormatCsvLine(IEnumerable<string> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        return string.Join(",", cells.Select(EscapeCsvCell));
    }

    /// <summary>Stable column-header strings shared with the response-CSV builder for inspection in tests.</summary>
    /// <param name="opCode">Op code.</param>
    /// <returns>Expected request-header cells.</returns>
    public IReadOnlyList<string> RequestHeaderFor(AnnexFourBatchOp opCode)
        => _schemas.Get(opCode).RequestHeader;

    /// <summary>Returns the schema response-header for the supplied op.</summary>
    /// <param name="opCode">Op code.</param>
    /// <returns>Expected response-header cells.</returns>
    public IReadOnlyList<string> ResponseHeaderFor(AnnexFourBatchOp opCode)
        => _schemas.Get(opCode).ResponseHeader;

    /// <summary>Stable culture used to format numeric / date cells in the parser and response-builder.</summary>
    public static CultureInfo CellCulture => CultureInfo.InvariantCulture;
}
