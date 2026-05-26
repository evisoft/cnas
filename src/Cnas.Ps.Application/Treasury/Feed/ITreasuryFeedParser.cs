using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — parser that converts a Treasury feed file into
/// per-row records. The default implementation reads UTF-8 CSV with a
/// header line; future iterations may swap in fixed-width or XML parsers
/// without disturbing the importer.
/// </summary>
public interface ITreasuryFeedParser
{
    /// <summary>
    /// Parses the supplied stream into ordered, 1-based per-row records.
    /// Bad rows are surfaced as <see cref="TreasuryFeedParsedRow"/> instances
    /// whose <see cref="TreasuryFeedParsedRow.ParseError"/> is populated; the
    /// importer records them as <c>Failed</c> without halting the run.
    /// </summary>
    /// <param name="content">Buffered feed-file stream (UTF-8 CSV with header).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// On success the ordered row list (may contain ParseError entries);
    /// <see cref="ErrorCodes.ValidationFailed"/> with stable
    /// <c>TREASURY_FEED.MISSING_HEADER</c> or
    /// <c>TREASURY_FEED.TOO_MANY_ROWS</c> on a structural failure.
    /// </returns>
    Task<Result<IReadOnlyList<TreasuryFeedParsedRow>>> ParseAsync(
        Stream content,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R1810 / TOR BP 1.2-I — one parsed row from the feed. Either every field
/// is populated (<see cref="ParseError"/> = null) OR <see cref="ParseError"/>
/// carries a sanitised description while the field values may be missing /
/// partially populated.
/// </summary>
/// <param name="RowOrdinal">1-based position of the row inside the file (excludes the header).</param>
/// <param name="ReceiptNumber">Treasury reference number — required, ≤ 64 chars, <c>[A-Z0-9-]{3,64}</c>.</param>
/// <param name="ReceiptDate">Treasury receipt date.</param>
/// <param name="PayerIdno">13-digit IDNO of the payer.</param>
/// <param name="PayerName">Payer display name; bounded to 256 chars.</param>
/// <param name="AmountMdl">Amount received in MDL; (0, 100_000_000].</param>
/// <param name="TreasuryCode">Treasury-side account code; ≤ 32 chars.</param>
/// <param name="Reference">Optional reference text; ≤ 256 chars.</param>
/// <param name="ParseError">When non-null the row is malformed; the importer records it as Failed.</param>
/// <param name="ParseErrorCode">Stable error code categorising the parse failure (e.g. <c>BAD_AMOUNT</c>).</param>
public sealed record TreasuryFeedParsedRow(
    int RowOrdinal,
    string? ReceiptNumber,
    DateOnly? ReceiptDate,
    string? PayerIdno,
    string? PayerName,
    decimal? AmountMdl,
    string? TreasuryCode,
    string? Reference,
    string? ParseError,
    string? ParseErrorCode);
