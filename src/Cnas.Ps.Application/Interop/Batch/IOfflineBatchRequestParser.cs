using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — parses an uploaded request CSV into per-row
/// <see cref="OfflineBatchRowSeed"/> records. The parser is format-aware:
/// for each op it knows the expected header columns and the per-row schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure model.</b> The parser distinguishes file-level failures (bad
/// format / bad header / too many rows) — surfaced as a
/// <see cref="Result"/> failure — from row-level failures (a single row's
/// CSV cells do not match the schema). Row-level failures ship as
/// <see cref="OfflineBatchRowSeed"/> entries with <c>ParseError</c>
/// populated so the engine can record them as <c>Failed</c> without
/// halting the entire batch.
/// </para>
/// </remarks>
public interface IOfflineBatchRequestParser
{
    /// <summary>
    /// Parses the supplied stream into a list of per-row seeds. The stream
    /// is read to completion but never closed by this method.
    /// </summary>
    /// <param name="opCode">Stable enum value of the targeted Annex-4 op.</param>
    /// <param name="requestStream">Readable stream over the request CSV bytes.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>List of seeds on success; <c>Result.Failure</c> on file-level errors.</returns>
    Task<Result<IReadOnlyList<OfflineBatchRowSeed>>> ParseAsync(
        AnnexFourBatchOp opCode,
        Stream requestStream,
        CancellationToken cancellationToken = default);
}
