using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — registry mapping each <see cref="AnnexFourBatchOp"/>
/// to its per-op CSV schema. Centralises the wire vocabulary of the offline
/// surface in one location so parser + response-builder stay in sync.
/// </summary>
public interface IOfflineBatchOpSchemaRegistry
{
    /// <summary>Returns the schema for the supplied op code.</summary>
    /// <param name="opCode">Stable enum value of the targeted Annex-4 op.</param>
    /// <returns>Op-specific schema.</returns>
    OfflineBatchOpSchema Get(AnnexFourBatchOp opCode);
}

/// <summary>
/// R1710 / TOR INT 002 — per-op CSV schema definition. Carries the request
/// + response header rows plus the per-row parse / serialise hooks.
/// </summary>
/// <param name="OpCode">Stable enum value the schema applies to.</param>
/// <param name="RequestHeader">CSV header columns of the request file.</param>
/// <param name="ResponseHeader">
/// CSV header columns of the response file. The first three columns are
/// always <c>RowOrdinal</c>, <c>Status</c>, <c>ErrorCode</c>; the remainder
/// are op-specific.
/// </param>
/// <param name="ParseRequestRow">
/// Delegate that takes the parsed cells of one CSV row (excluding the
/// header) and returns a JSON-encoded shape mirroring the synchronous op's
/// input DTO. Throws when the cell shape is invalid — the parser wraps the
/// exception in an <see cref="OfflineBatchRowParseError"/>.
/// </param>
/// <param name="SerializeResponseRow">
/// Delegate that takes the JSON-encoded shape returned by the op and
/// renders it into op-specific CSV cells (excluding the prefix three
/// columns).
/// </param>
public sealed record OfflineBatchOpSchema(
    AnnexFourBatchOp OpCode,
    IReadOnlyList<string> RequestHeader,
    IReadOnlyList<string> ResponseHeader,
    Func<IReadOnlyList<string>, string> ParseRequestRow,
    Func<string?, IReadOnlyList<string>> SerializeResponseRow);
