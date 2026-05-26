using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ExternalSources;

/// <summary>
/// R0203 / TOR CF 20.06 — pluggable adapter that fetches data from a single
/// external source-system (RSP, RSUD, SFS, …) for a given as-of date. The
/// implementation owns the upstream protocol (MConnect SOAP, REST, SFTP, …)
/// and is responsible for applying the records locally — the ingestion service
/// only orchestrates lifecycle + audit + metrics.
/// </summary>
/// <remarks>
/// <para>
/// <b>Discovery.</b> Connectors are registered as
/// <c>IEnumerable&lt;IExternalSourceConnector&gt;</c>; the ingestion service
/// picks the right one by matching <see cref="SourceCode"/>. If no concrete
/// connector matches, the in-memory placeholder is used as a fallback so the
/// chart starts safely without per-source configuration.
/// </para>
/// <para>
/// <b>No throw.</b> Implementations MUST return a typed
/// <see cref="Result{T}"/> rather than throwing on configuration / upstream
/// failures — the discipline rules require failure to be a value, not an
/// exception (see <c>HttpsTreasuryFeedSource</c> for the canonical pattern).
/// </para>
/// </remarks>
public interface IExternalSourceConnector
{
    /// <summary>
    /// Upper-case source-system code this connector serves. Matched against
    /// <c>ExternalSourceIngestionRun.SourceCode</c> at dispatch time. Examples:
    /// <c>RSP</c>, <c>RSUD</c>, <c>SFS</c>.
    /// </summary>
    string SourceCode { get; }

    /// <summary>
    /// Pulls records from the upstream source covering <paramref name="asOfDate"/>
    /// and applies them locally. Returns the outcome envelope on success or a
    /// stable failure code (e.g. <c>EXT_SRC.RSP_NOT_CONFIGURED</c>) on
    /// configuration / upstream failure.
    /// </summary>
    /// <param name="asOfDate">Calendar date the ingestion should target.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated <see cref="ExternalSourceFetchOutcomeDto"/>; on
    /// failure a typed <see cref="Result{T}"/> with a stable error code.
    /// </returns>
    Task<Result<ExternalSourceFetchOutcomeDto>> FetchAsync(
        DateOnly asOfDate,
        CancellationToken cancellationToken = default);
}
