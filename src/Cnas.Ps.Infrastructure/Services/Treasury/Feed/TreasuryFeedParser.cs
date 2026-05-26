using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — default UTF-8 CSV implementation of
/// <see cref="ITreasuryFeedParser"/>. Reads the header row, maps column
/// indices case-insensitively, then yields one
/// <see cref="TreasuryFeedParsedRow"/> per data line. Bad rows surface with
/// <c>ParseError</c> populated; structural failures (missing header / too
/// many rows) surface as a top-level <c>Result.Failure</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>CSV dialect.</b> RFC-4180-ish: comma separator, optional <c>"</c>
/// quoting, doubled quotes to escape, LF or CRLF line endings. The Treasury's
/// real feed is expected to come without embedded commas inside fields, but
/// the parser tolerates them when quoted.
/// </para>
/// <para>
/// <b>Bounded.</b> Refuses files with more than
/// <see cref="MaxRowsPerFile"/> data rows so a runaway upload cannot exhaust
/// memory.
/// </para>
/// </remarks>
public sealed class TreasuryFeedParser : ITreasuryFeedParser
{
    /// <summary>Maximum permitted data-row count per feed file.</summary>
    public const int MaxRowsPerFile = 100_000;

    /// <summary>Required CSV column names (case-insensitive lookup).</summary>
    public static readonly IReadOnlyList<string> RequiredColumns = new[]
    {
        "ReceiptNumber",
        "ReceiptDate",
        "PayerIdno",
        "PayerName",
        "AmountMdl",
        "TreasuryCode",
        "Reference",
    };

    /// <summary>ReceiptNumber regex — uppercase letters, digits, dashes; 3..64 chars.</summary>
    public const string ReceiptNumberRegex = "^[A-Z0-9-]{3,64}$";

    /// <summary>13-digit IDNO regex.</summary>
    public const string IdnoRegex = "^[0-9]{13}$";

    /// <summary>Stable error code for a malformed ReceiptNumber row.</summary>
    public const string BadReceiptNumberCode = "BAD_RECEIPT_NUMBER";

    /// <summary>Stable error code for an unparseable ReceiptDate row.</summary>
    public const string BadReceiptDateCode = "BAD_RECEIPT_DATE";

    /// <summary>Stable error code for a malformed PayerIdno row.</summary>
    public const string BadPayerIdnoCode = "BAD_PAYER_IDNO";

    /// <summary>Stable error code for a missing / too-long PayerName.</summary>
    public const string BadPayerNameCode = "BAD_PAYER_NAME";

    /// <summary>Stable error code for an out-of-range AmountMdl row.</summary>
    public const string BadAmountCode = "BAD_AMOUNT";

    /// <summary>Stable error code for a missing / too-long TreasuryCode.</summary>
    public const string BadTreasuryCodeCode = "BAD_TREASURY_CODE";

    /// <summary>Stable error code for a row whose column count does not match the header.</summary>
    public const string ColumnCountMismatchCode = "COLUMN_COUNT_MISMATCH";

    private static readonly Regex CompiledReceiptNumber = new(
        ReceiptNumberRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CompiledIdno = new(
        IdnoRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<TreasuryFeedParsedRow>>> ParseAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var reader = new StreamReader(
            content,
            encoding: Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return Result<IReadOnlyList<TreasuryFeedParsedRow>>.Failure(
                ErrorCodes.ValidationFailed,
                ITreasuryFeedImporter.MissingHeaderCode);
        }

        var headerFields = SplitCsvLine(headerLine);
        var columnIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headerFields.Count; i++)
        {
            // Whitespace-trim the header tokens so a feed produced with extra
            // padding (common when hand-edited) still matches the required
            // column names.
            columnIndexes[headerFields[i].Trim()] = i;
        }
        foreach (var required in RequiredColumns)
        {
            if (!columnIndexes.ContainsKey(required))
            {
                return Result<IReadOnlyList<TreasuryFeedParsedRow>>.Failure(
                    ErrorCodes.ValidationFailed,
                    ITreasuryFeedImporter.MissingHeaderCode);
            }
        }

        var rows = new List<TreasuryFeedParsedRow>();
        int ordinal = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                // Skip blank lines — they often appear at end-of-file.
                continue;
            }
            ordinal++;

            if (ordinal > MaxRowsPerFile)
            {
                return Result<IReadOnlyList<TreasuryFeedParsedRow>>.Failure(
                    ErrorCodes.ValidationFailed,
                    ITreasuryFeedImporter.TooManyRowsCode);
            }

            rows.Add(ParseDataLine(line, ordinal, columnIndexes, headerFields.Count));
        }

        return Result<IReadOnlyList<TreasuryFeedParsedRow>>.Success(rows);
    }

    /// <summary>
    /// Parses a single data line into a populated or ParseError row record.
    /// </summary>
    /// <param name="line">Raw CSV line (without the trailing newline).</param>
    /// <param name="ordinal">1-based row position.</param>
    /// <param name="columnIndexes">Header lookup table.</param>
    /// <param name="expectedFieldCount">Field count derived from the header.</param>
    /// <returns>Populated record or ParseError record.</returns>
    private static TreasuryFeedParsedRow ParseDataLine(
        string line,
        int ordinal,
        Dictionary<string, int> columnIndexes,
        int expectedFieldCount)
    {
        var fields = SplitCsvLine(line);
        if (fields.Count != expectedFieldCount)
        {
            return new TreasuryFeedParsedRow(
                RowOrdinal: ordinal,
                ReceiptNumber: null,
                ReceiptDate: null,
                PayerIdno: null,
                PayerName: null,
                AmountMdl: null,
                TreasuryCode: null,
                Reference: null,
                ParseError: $"Row has {fields.Count} columns, expected {expectedFieldCount}.",
                ParseErrorCode: ColumnCountMismatchCode);
        }

        // Lookup helper — every field is trimmed inside the parsers so a
        // hand-edited file with extra padding still validates.
        string? Get(string column)
        {
            return columnIndexes.TryGetValue(column, out var idx) ? fields[idx]?.Trim() : null;
        }

        var receiptNumber = Get("ReceiptNumber");
        var receiptDateRaw = Get("ReceiptDate");
        var payerIdno = Get("PayerIdno");
        var payerName = Get("PayerName");
        var amountRaw = Get("AmountMdl");
        var treasuryCode = Get("TreasuryCode");
        var reference = Get("Reference");

        if (string.IsNullOrEmpty(receiptNumber) || !CompiledReceiptNumber.IsMatch(receiptNumber))
        {
            return BadRow(ordinal, BadReceiptNumberCode, "ReceiptNumber must match ^[A-Z0-9-]{3,64}$.",
                receiptNumber, receiptDateRaw, payerIdno, payerName, amountRaw, treasuryCode, reference);
        }

        if (string.IsNullOrEmpty(receiptDateRaw)
            || !DateOnly.TryParseExact(receiptDateRaw, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var receiptDate))
        {
            return BadRow(ordinal, BadReceiptDateCode, "ReceiptDate must be ISO yyyy-MM-dd.",
                receiptNumber, receiptDateRaw, payerIdno, payerName, amountRaw, treasuryCode, reference);
        }

        if (string.IsNullOrEmpty(payerIdno) || !CompiledIdno.IsMatch(payerIdno))
        {
            return BadRow(ordinal, BadPayerIdnoCode, "PayerIdno must be 13 digits.",
                receiptNumber, receiptDateRaw, payerIdno, payerName, amountRaw, treasuryCode, reference);
        }

        if (string.IsNullOrEmpty(payerName) || payerName.Length > 256)
        {
            return BadRow(ordinal, BadPayerNameCode, "PayerName is required and must be ≤ 256 chars.",
                receiptNumber, receiptDateRaw, payerIdno, payerName, amountRaw, treasuryCode, reference);
        }

        if (string.IsNullOrEmpty(amountRaw)
            || !decimal.TryParse(amountRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0m
            || amount > 100_000_000m
            || decimal.Round(amount, 2) != amount)
        {
            return BadRow(ordinal, BadAmountCode, "AmountMdl must be > 0, ≤ 100_000_000, with ≤ 2 decimals.",
                receiptNumber, receiptDateRaw, payerIdno, payerName, amountRaw, treasuryCode, reference);
        }

        if (string.IsNullOrEmpty(treasuryCode) || treasuryCode.Length > 32)
        {
            return BadRow(ordinal, BadTreasuryCodeCode, "TreasuryCode is required and must be ≤ 32 chars.",
                receiptNumber, receiptDateRaw, payerIdno, payerName, amountRaw, treasuryCode, reference);
        }

        // Reference is optional — silently truncate the empty string to null
        // so the importer doesn't carry meaningless padding into the receipt.
        if (string.IsNullOrEmpty(reference))
        {
            reference = null;
        }
        if (reference is not null && reference.Length > 256)
        {
            // Reference too long — flag as failed; it's typically a payload error.
            return BadRow(ordinal, "BAD_REFERENCE", "Reference must be ≤ 256 chars.",
                receiptNumber, receiptDateRaw, payerIdno, payerName, amountRaw, treasuryCode, reference);
        }

        return new TreasuryFeedParsedRow(
            RowOrdinal: ordinal,
            ReceiptNumber: receiptNumber,
            ReceiptDate: receiptDate,
            PayerIdno: payerIdno,
            PayerName: payerName,
            AmountMdl: amount,
            TreasuryCode: treasuryCode,
            Reference: reference,
            ParseError: null,
            ParseErrorCode: null);
    }

    /// <summary>
    /// Builds a ParseError row carrying the offending raw values for forensic
    /// replay. Does NOT include PayerName / PayerIdno in the error description
    /// — only the structural code is surfaced.
    /// </summary>
    /// <param name="ordinal">Row position.</param>
    /// <param name="code">Stable error code.</param>
    /// <param name="description">Sanitised description.</param>
    /// <param name="receiptNumber">Raw receipt number.</param>
    /// <param name="receiptDateRaw">Raw receipt-date string.</param>
    /// <param name="payerIdno">Raw payer IDNO.</param>
    /// <param name="payerName">Raw payer name.</param>
    /// <param name="amountRaw">Raw amount string.</param>
    /// <param name="treasuryCode">Raw treasury code.</param>
    /// <param name="reference">Raw reference.</param>
    /// <returns>Populated ParseError row.</returns>
    private static TreasuryFeedParsedRow BadRow(
        int ordinal,
        string code,
        string description,
        string? receiptNumber,
        string? receiptDateRaw,
        string? payerIdno,
        string? payerName,
        string? amountRaw,
        string? treasuryCode,
        string? reference)
    {
        // The retained fields below are used by the importer to serialise the
        // forensic-replay JSON. None of them is logged or echoed to operators
        // outside the admin surface.
        _ = receiptDateRaw;
        _ = amountRaw;
        return new TreasuryFeedParsedRow(
            RowOrdinal: ordinal,
            ReceiptNumber: receiptNumber,
            ReceiptDate: null,
            PayerIdno: payerIdno,
            PayerName: payerName,
            AmountMdl: null,
            TreasuryCode: treasuryCode,
            Reference: reference,
            ParseError: description,
            ParseErrorCode: code);
    }

    /// <summary>
    /// Splits a single CSV line into fields, honouring double-quote quoting
    /// and the doubled-quote escape sequence. Intentionally small — the
    /// feed dialect is line-oriented and never carries embedded newlines.
    /// </summary>
    /// <param name="line">Raw CSV line (no trailing newline).</param>
    /// <returns>Field list with quote-escape stripping applied.</returns>
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    // Doubled-quote escape: `""` → literal `"`.
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
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
                if (c == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
