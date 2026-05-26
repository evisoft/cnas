using System.Collections.Generic;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Search;

/// <summary>
/// R0501 / TOR CF 01.04 — metadata-driven catalogue of the search criteria the
/// system exposes per domain. The UI consumes it to render a generic
/// query-by-example form without hard-coding per-domain field lists; the
/// service layer consumes it as the canonical set of fields a saved search
/// (R0524) may reference.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a catalogue, not an attribute scan.</b> The naive alternative — scan
/// entity properties for a marker attribute — couples the search surface to
/// the persistence model and forbids exposing a "computed" criterion (e.g.
/// <c>dateRange</c>) that does not correspond to a single column. A registry
/// of descriptors keeps the search contract independent of the underlying
/// schema and lets the catalogue evolve through code review.
/// </para>
/// <para>
/// <b>Lifetime.</b> Implementations should be stateless and registered as
/// singletons — the descriptor list is computed once at startup and never
/// mutates within a process.
/// </para>
/// </remarks>
public interface ISearchCriteriaCatalog
{
    /// <summary>
    /// Returns the criteria descriptors for the supplied domain code. An
    /// unknown domain yields an empty list (the controller maps that to 404).
    /// </summary>
    /// <param name="domain">
    /// Stable lower-kebab-case domain code from
    /// <see cref="GlobalSearchDomains"/> (e.g. <c>"applications"</c>,
    /// <c>"contributors"</c>).
    /// </param>
    /// <returns>
    /// The descriptor list — non-null, never <see langword="null"/>; an
    /// unknown domain returns the empty list.
    /// </returns>
    IReadOnlyList<SearchCriterionDescriptor> GetCriteriaFor(string domain);
}
