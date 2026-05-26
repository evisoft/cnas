using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.AccessScope;

/// <summary>
/// R0671 / TOR CF 18.06 — reference implementation of <see cref="IAccessScopeFilter"/>.
/// Splices a <c>Where</c> predicate onto each supported queryable when the corresponding
/// allow-list is non-empty; otherwise returns the source unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>NULL-tolerance.</b> Every predicate is "scoped column is NULL OR scoped column is
/// in the allow-list". This preserves the design choice documented on
/// <see cref="IAccessScope"/>: national / unmarked rows remain universally visible.
/// </para>
/// <para>
/// <b>Provider-independence.</b> The predicates rely only on
/// <c>List&lt;string&gt;.Contains</c> + string equality, which EF Core translates to
/// <c>IN (…)</c> on every relational provider and which the in-memory provider
/// executes verbatim. No <c>EF.Functions</c> dependency.
/// </para>
/// <para>
/// <b>Lifetime.</b> Stateless — register as a singleton. The filter has no per-request
/// state; the scope envelope arrives as a method argument.
/// </para>
/// </remarks>
public sealed class AccessScopeFilter : IAccessScopeFilter
{
    /// <inheritdoc />
    public IQueryable<Solicitant> ApplyToSolicitants(IQueryable<Solicitant> source, IAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedRegions.Count == 0)
        {
            return source;
        }
        // Materialise into a plain List<string> + capture by closure so EF translates
        // the IN clause cleanly across providers; HashSet capture trips InMemory.
        var allowed = scope.AllowedRegions.ToList();
        return source.Where(s => s.RegionCode == null || allowed.Contains(s.RegionCode));
    }

    /// <inheritdoc />
    public IQueryable<ServiceApplication> ApplyToServiceApplications(IQueryable<ServiceApplication> source, IAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedSubdivisionCodes.Count == 0)
        {
            return source;
        }
        var allowed = scope.AllowedSubdivisionCodes.ToList();
        return source.Where(a => a.SubdivisionCode == null || allowed.Contains(a.SubdivisionCode));
    }

    /// <inheritdoc />
    public IQueryable<WorkflowTask> ApplyToWorkflowTasks(
        IQueryable<WorkflowTask> source,
        IAccessScope scope,
        IQueryable<WorkflowDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definitions);
        if (scope.AllowedWorkflowCategories.Count == 0)
        {
            return source;
        }
        var allowed = scope.AllowedWorkflowCategories.ToList();
        // The task's NodeCode anchors to a WorkflowDefinition.Code on the CURRENT
        // revision. We keep visible:
        //   1. Tasks with no anchor (NodeCode == null) — legacy / unanchored data.
        //   2. Tasks whose current workflow definition has CategoryCode == null —
        //      uncategorised national workflows.
        //   3. Tasks whose current workflow definition has CategoryCode in the allow-list.
        // (Tasks whose definition has a CategoryCode that is NOT in the allow-list are
        // hidden.)
        return source.Where(t =>
            t.NodeCode == null
            || definitions.Any(d =>
                d.IsCurrent
                && d.Code == t.NodeCode!
                && (d.CategoryCode == null || allowed.Contains(d.CategoryCode!))));
    }

    /// <inheritdoc />
    public IQueryable<Document> ApplyToDocuments(IQueryable<Document> source, IAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedDocumentCategories.Count == 0)
        {
            return source;
        }
        // Materialise the allow-list as the underlying integer values of the
        // DocumentKind enum so the IN clause compares integers, not strings —
        // matches the on-disk storage of Document.Kind (HasConversion<int>).
        var allowedKinds = new List<int>(scope.AllowedDocumentCategories.Count);
        foreach (var name in scope.AllowedDocumentCategories)
        {
            if (Enum.TryParse<DocumentKind>(name, ignoreCase: true, out var kind))
            {
                allowedKinds.Add((int)kind);
            }
        }
        if (allowedKinds.Count == 0)
        {
            // The allow-list mentioned categories, but none parsed as known DocumentKind
            // values (operator typo / version skew). Deny by emitting an
            // always-false predicate so the caller does NOT see every document on
            // accidental misconfiguration — fail closed on this dimension.
            return source.Where(_ => false);
        }
        return source.Where(d => allowedKinds.Contains((int)d.Kind));
    }
}
