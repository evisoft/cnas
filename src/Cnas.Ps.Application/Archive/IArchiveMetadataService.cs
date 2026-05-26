using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Archive;

/// <summary>
/// R0332 / TOR CF 12.02 — electronic-archive metadata summariser. Produces a
/// single depersonalised <see cref="ArchiveSummaryDto"/> capturing total-active
/// / total-archived / last-updated counts across the five register types the
/// tabbed archive UI surfaces: Contributors, Insured Persons, Decisions,
/// Dossiers, Documents.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated service.</b> The Web UI at <c>/archive</c> needs a
/// header strip of chips per tab BEFORE the user picks a tab. Issuing five
/// separate HTTP calls (one per controller) would round-trip the network
/// five times for what is fundamentally a single dashboard widget; the
/// summariser instead issues five read-replica COUNT queries in parallel and
/// returns the consolidated payload.
/// </para>
/// <para>
/// <b>Read-replica routing.</b> The implementation MUST consume
/// <c>IReadOnlyCnasDbContext</c> (R0026 / PSR 006) — the summary is a pure
/// read aggregation with no write side, so the streaming-replication replica
/// is the correct target.
/// </para>
/// </remarks>
public interface IArchiveMetadataService
{
    /// <summary>
    /// Computes the per-tab summary for the archive UI. Counts are computed
    /// against the read-replica context so the call adds no write-side load.
    /// </summary>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// A successful <see cref="ArchiveSummaryDto"/> with one
    /// <see cref="ArchiveTabSummaryDto"/> per register. The service never
    /// fails for business reasons — only an underlying I/O exception would
    /// bubble through as <c>Result.Failure</c>.
    /// </returns>
    Task<Result<ArchiveSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken = default);
}
