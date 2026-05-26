using System.Linq;
using System.Security.Claims;

namespace Cnas.Ps.Application.Search;

/// <summary>
/// R0526 / TOR CF 03.10 — applies row-level visibility scoping to a search
/// query <see cref="IQueryable{T}"/> based on the calling principal's roles +
/// claims and the ABAC rule set bound to the supplied search domain. The
/// filter is invoked by every per-domain projector inside the unified search
/// service so a non-privileged caller only ever sees the rows their region /
/// directorate / group permits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layering.</b> The filter is composable: callers pass the per-domain
/// <see cref="IQueryable{T}"/> AFTER they have applied the full-text predicate
/// and BEFORE they apply paging. The filter narrows the row set; callers
/// remain responsible for sorting + paging.
/// </para>
/// <para>
/// <b>Super-role bypass.</b> Implementations MUST grant unconditional access
/// to callers carrying the configured super-role (e.g. <c>cnas-admin</c> or a
/// dedicated security-officer role). The bypass is the operational escape
/// hatch when row-level rules are mis-configured.
/// </para>
/// <para>
/// <b>Default deny.</b> When no ABAC rule set exists for the supplied domain
/// AND the caller is not a super-role, the filter MUST collapse the query to
/// an empty result set. Secure-by-default — a missing rule never grants
/// access.
/// </para>
/// </remarks>
public interface ISearchRowLevelFilter
{
    /// <summary>
    /// Applies the per-domain row-level scope on the supplied query and
    /// returns the narrowed projection. The original <paramref name="query"/>
    /// is never mutated.
    /// </summary>
    /// <typeparam name="T">The entity type being searched.</typeparam>
    /// <param name="query">The pre-filtered query (full-text predicate already applied).</param>
    /// <param name="user">The calling principal whose roles + claims drive the scope.</param>
    /// <param name="domain">
    /// Stable lower-kebab-case domain code from
    /// <c>Cnas.Ps.Contracts.GlobalSearchDomains</c>. Used to look up the
    /// ABAC rule set keyed by <c>SEARCH.{DOMAIN}</c>.
    /// </param>
    /// <returns>The scoped query.</returns>
    IQueryable<T> ApplyRowLevelScope<T>(
        IQueryable<T> query,
        ClaimsPrincipal user,
        string domain);
}
