namespace Cnas.Ps.Application.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — one parsed row produced by
/// <see cref="IOfflineBatchRequestParser"/>. Each seed maps 1:1 to a
/// resulting <c>OfflineBatchRow</c> persisted by the submission service.
/// </summary>
/// <param name="RowOrdinal">1-based position in the request CSV.</param>
/// <param name="RequestPayloadJson">
/// JSON snapshot of the op's input DTO (mirrors the synchronous shape). When
/// the row's CSV cells failed to parse against the op schema this is
/// the literal string <c>"{}"</c>.
/// </param>
/// <param name="ParseError">
/// Non-null when the row could not be parsed against the op schema. The
/// processor honours the parse error by setting the row's status to
/// <c>Failed</c> and stamping the supplied error code + description without
/// invoking the synchronous interop API.
/// </param>
public sealed record OfflineBatchRowSeed(
    int RowOrdinal,
    string RequestPayloadJson,
    OfflineBatchRowParseError? ParseError);

/// <summary>
/// R1710 / TOR INT 002 — placeholder error captured by the parser when a
/// row's CSV cells fail to parse against the op's input schema.
/// </summary>
/// <param name="ErrorCode">Stable error code to surface on the failed row (e.g. <c>VALIDATION_FAILED</c>).</param>
/// <param name="ErrorDescription">Short, PII-free description of the parser failure.</param>
public sealed record OfflineBatchRowParseError(
    string ErrorCode,
    string ErrorDescription);
