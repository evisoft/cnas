using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.PublicServices;

/// <summary>
/// R0500 / TOR CF 01.02 / UC01 — anonymous-accessible KPI snapshot service.
/// Surfaces depersonalised system-wide counts (contributors, insured persons,
/// pending applications, decisions issued in the last 30 days) and the
/// most-recent Treasury-feed import completion timestamp.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cached.</b> The service recomputes the snapshot at most once every
/// 5 minutes; subsequent calls within the window return the cached
/// <see cref="PublicKpiSnapshotDto"/> instance. The cache is process-local
/// and freshly populated on cold-start; no warm-up dependency.
/// </para>
/// <para>
/// <b>Depersonalised.</b> Every field is an aggregate count or a
/// system-level UTC timestamp — no row contents, no per-citizen
/// attributes, no PII risk.
/// </para>
/// <para>
/// <b>Read-replica routed.</b> Queries flow through
/// <c>IReadOnlyCnasDbContext</c> so analytical fan-out cannot push
/// load onto the primary backend.
/// </para>
/// </remarks>
public interface IPublicKpiService
{
    /// <summary>
    /// Returns the current KPI snapshot. The first call after process start
    /// (or after the cache window elapses) recomputes against the DB; later
    /// calls within the window return the cached result.
    /// </summary>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>A successful <see cref="PublicKpiSnapshotDto"/> result.</returns>
    Task<Result<PublicKpiSnapshotDto>> GetCurrentAsync(CancellationToken cancellationToken = default);
}
