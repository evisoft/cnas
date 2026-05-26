using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Dashboard;

/// <summary>
/// R0530 / R0531 / CF 04.01-04.02 — strategy that produces one or more
/// <see cref="KpiWidget"/> rows for a specific tile category on a per-caller
/// dashboard composition. The dashboard service composes the registered set of
/// producers into the snapshot consumed by <c>GET /api/dashboard/widgets</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One producer, one category.</b> Each implementation declares the canonical
/// <see cref="Category"/> it owns. The dashboard service's role-aware merge groups
/// every produced widget under the producer's category — producers MUST NOT mix
/// categories in their output (a violation would defeat the per-bucket grouping the
/// UI relies on).
/// </para>
/// <para>
/// <b>Role gating.</b> The dashboard service filters producers whose
/// <see cref="SupportedRoles"/> intersect the caller's role set BEFORE invoking
/// <see cref="ProduceAsync(long, CancellationToken)"/>. Producers that should run for
/// every authenticated caller advertise the wildcard <c>"*"</c> single-element set;
/// producers that only run for a subset (admin, decider) list the explicit role codes.
/// </para>
/// <para>
/// <b>Failure semantics.</b> A producer MUST NOT throw on a missing precondition
/// (e.g. user has no tasks). It returns an empty list instead. Genuine infrastructure
/// failures (DB unavailable, cancellation) propagate via the <see cref="Result{T}"/>
/// failure branch and the composing service surfaces a degraded snapshot rather than
/// failing the whole dashboard read.
/// </para>
/// </remarks>
public interface IDashboardTileProducer
{
    /// <summary>
    /// Canonical tile category this producer owns. The dashboard service uses this
    /// value to tag every <see cref="KpiWidget"/> the producer returns and to group
    /// the snapshot by category for the UI.
    /// </summary>
    DashboardCategory Category { get; }

    /// <summary>
    /// Set of role codes (case-insensitive) for which this producer should run. A
    /// single-element list containing <c>"*"</c> means "every authenticated caller";
    /// otherwise the dashboard service runs the producer only when the caller's roles
    /// intersect this set.
    /// </summary>
    IReadOnlyCollection<string> SupportedRoles { get; }

    /// <summary>
    /// Computes the widgets this producer contributes to the dashboard snapshot for
    /// the supplied caller. Implementations MUST return an empty list when the caller
    /// has no relevant data (never throw) and propagate infrastructure errors via the
    /// <see cref="Result{T}.Failure(string, string)"/> branch.
    /// </summary>
    /// <param name="userId">Internal primary key of the authenticated caller (raw long; Sqid
    /// encoding happens only at the API boundary per CLAUDE.md RULE 3).</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A success result wrapping the (possibly empty) list of widgets
    /// produced for this caller. The widgets' <see cref="KpiWidget.Category"/> field
    /// is set to <see cref="Category"/> by the composing dashboard service.</returns>
    Task<Result<IReadOnlyList<KpiWidget>>> ProduceAsync(
        long userId,
        CancellationToken cancellationToken = default);
}
