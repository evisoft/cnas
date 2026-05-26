using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.AccessScope;

/// <summary>
/// R0671 / TOR CF 18.06 — row-level access-scope predicate splicer. Every list-style
/// service consults this filter BEFORE handing the queryable to the query-budget guard
/// so the budget evaluates the SCOPED row count, not the unscoped one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateless &amp; allocation-free on the empty path.</b> When the supplied
/// <see cref="IAccessScope"/> has an empty allow-list for the dimension being filtered
/// (or is fully unscoped), every <c>Apply…</c> method returns the source
/// <see cref="IQueryable{T}"/> unchanged — no <c>Where</c> is layered on, no closure is
/// allocated. The hot path for the national administrator stays free of overhead.
/// </para>
/// <para>
/// <b>NULL data semantics.</b> Every predicate is "scoped column is NULL OR scoped
/// column is in the allow-list". Rows whose scoped column is itself NULL — typically
/// national / unmarked data, or rows that pre-date the scoping migration — are
/// VISIBLE to every scoped caller. See <see cref="IAccessScope"/> remarks for the
/// design rationale; the test suite asserts this explicitly.
/// </para>
/// <para>
/// <b>Composition order.</b> Filters are designed to be chained — splice the access
/// scope first, then any other filters (free-text, date range, QBE), then the budget
/// guard, then materialisation. The scope is a SECURITY boundary; it must run before
/// any other gate that observes row counts so an unprivileged caller cannot bypass
/// the cap by being "lucky" with a permissive QBE predicate.
/// </para>
/// </remarks>
public interface IAccessScopeFilter
{
    /// <summary>
    /// Narrows a <see cref="Solicitant"/> queryable to the regions the
    /// <paramref name="scope"/> permits. Returns <paramref name="source"/> unchanged
    /// when the scope's <see cref="IAccessScope.AllowedRegions"/> set is empty.
    /// </summary>
    /// <param name="source">The unscoped source queryable.</param>
    /// <param name="scope">The caller's effective access-scope envelope.</param>
    /// <returns>The filtered queryable, ready for downstream chaining.</returns>
    IQueryable<Solicitant> ApplyToSolicitants(IQueryable<Solicitant> source, IAccessScope scope);

    /// <summary>
    /// Narrows a <see cref="ServiceApplication"/> queryable to the subdivisions the
    /// <paramref name="scope"/> permits. Returns <paramref name="source"/> unchanged
    /// when the scope's <see cref="IAccessScope.AllowedSubdivisionCodes"/> set is empty.
    /// </summary>
    /// <param name="source">The unscoped source queryable.</param>
    /// <param name="scope">The caller's effective access-scope envelope.</param>
    /// <returns>The filtered queryable, ready for downstream chaining.</returns>
    IQueryable<ServiceApplication> ApplyToServiceApplications(IQueryable<ServiceApplication> source, IAccessScope scope);

    /// <summary>
    /// Narrows a <see cref="WorkflowTask"/> queryable to the workflow categories the
    /// <paramref name="scope"/> permits. Joins via the parent
    /// <see cref="WorkflowTask.NodeCode"/> → <see cref="WorkflowDefinition.CategoryCode"/>
    /// path; the join is materialised through a sub-query so the predicate stays
    /// translatable. Returns <paramref name="source"/> unchanged when the scope's
    /// <see cref="IAccessScope.AllowedWorkflowCategories"/> set is empty.
    /// </summary>
    /// <param name="source">The unscoped source queryable.</param>
    /// <param name="scope">The caller's effective access-scope envelope.</param>
    /// <param name="definitions">
    /// The full <see cref="WorkflowDefinition"/> queryable used to resolve the
    /// category of each task's anchor. Supplied by the caller so the filter does
    /// not need its own <c>ICnasDbContext</c> dependency.
    /// </param>
    /// <returns>The filtered queryable, ready for downstream chaining.</returns>
    IQueryable<WorkflowTask> ApplyToWorkflowTasks(
        IQueryable<WorkflowTask> source,
        IAccessScope scope,
        IQueryable<WorkflowDefinition> definitions);

    /// <summary>
    /// Narrows a <see cref="Document"/> queryable to the document categories the
    /// <paramref name="scope"/> permits. The predicate matches each row's
    /// <see cref="Document.Kind"/> against the scope's allow-list (interpreted as
    /// <see cref="DocumentKind"/> enum names). Returns <paramref name="source"/>
    /// unchanged when the scope's <see cref="IAccessScope.AllowedDocumentCategories"/>
    /// set is empty.
    /// </summary>
    /// <param name="source">The unscoped source queryable.</param>
    /// <param name="scope">The caller's effective access-scope envelope.</param>
    /// <returns>The filtered queryable, ready for downstream chaining.</returns>
    IQueryable<Document> ApplyToDocuments(IQueryable<Document> source, IAccessScope scope);
}
