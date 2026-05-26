namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// R0671 / TOR CF 18.06 — granular row-level access-scope envelope carried per
/// authenticated request. Pairs with <see cref="ICallerContext"/> to narrow every
/// list-style query down to the geographies / CNAS subdivisions / document
/// categories / workflow categories the caller has been granted via their role +
/// group membership.
/// </summary>
/// <remarks>
/// <para>
/// <b>Convention.</b> Each allow-list is interpreted as "show me ONLY rows whose
/// scoped column is in this set". An EMPTY allow-list means "no narrowing in this
/// dimension" — the caller is unrestricted on that axis. This convention lets the
/// national administrator carry an envelope with all four allow-lists empty (and
/// <see cref="IsUnscoped"/> = <c>true</c>) and skip every filter without any
/// special-case branch in the filter implementation.
/// </para>
/// <para>
/// <b>NULL data semantics.</b> Rows whose scoped column is itself <c>NULL</c> (e.g.
/// a Solicitant with no <c>RegionCode</c> set) are deliberately VISIBLE to every
/// scoped caller — "national / unmarked" data is universally accessible. This is
/// the safe default for the back-fill: existing rows that pre-date the scoping
/// columns must not vanish from every staff user's grid simply because they have
/// no region tag yet. Operators back-fill region codes lazily as data is touched.
/// </para>
/// <para>
/// <b>Stable string vocabulary.</b> The lists carry plain strings — region codes
/// like <c>"CHIS"</c>, subdivision <c>CnasBranch.Code</c> values like
/// <c>"CHISINAU-CENTRU"</c>, <c>DocumentKind</c> enum names (e.g. <c>"Decision"</c>),
/// and workflow-category codes (e.g. <c>"pension"</c>). The vocabulary is stable
/// across releases — adding to it is non-breaking; renaming is a breaking change.
/// </para>
/// <para>
/// <b>Composition with R0126 ACL.</b> The access scope is the ROW-LEVEL filter; the
/// R0126 <c>IWorkflowAclService</c> is the per-task ACTION-LEVEL gate. The two
/// compose AND-wise: a scoped caller sees only their region's rows AND, within those
/// rows, can only ACT on tasks whose workflow + step ACL permits them.
/// </para>
/// </remarks>
public interface IAccessScope
{
    /// <summary>
    /// Set of region codes the caller may see (e.g. <c>"CHIS"</c>, <c>"BLT"</c>).
    /// <see cref="System.Collections.Generic.IReadOnlyCollection{T}.Count"/> = 0 means
    /// "no restriction by region" — the caller can see every region. Compared with
    /// <see cref="System.StringComparer.OrdinalIgnoreCase"/> at filter time.
    /// </summary>
    IReadOnlyCollection<string> AllowedRegions { get; }

    /// <summary>
    /// Set of CNAS subdivision codes (<see cref="Cnas.Ps.Core.Domain.CnasBranch.Code"/>
    /// values, e.g. <c>"CHISINAU-CENTRU"</c>, <c>"BALTI"</c>) the caller may see. Empty
    /// set = no restriction by subdivision.
    /// </summary>
    IReadOnlyCollection<string> AllowedSubdivisionCodes { get; }

    /// <summary>
    /// Set of <see cref="Cnas.Ps.Core.Domain.DocumentKind"/> values (as their enum
    /// names, e.g. <c>"Decision"</c>, <c>"Attachment"</c>) the caller may see. Empty
    /// set = no restriction by document category.
    /// </summary>
    IReadOnlyCollection<string> AllowedDocumentCategories { get; }

    /// <summary>
    /// Set of workflow-category codes (matching
    /// <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition.CategoryCode"/>, e.g.
    /// <c>"pension"</c>, <c>"indemnization"</c>) the caller may see. Empty set =
    /// no restriction by workflow category.
    /// </summary>
    IReadOnlyCollection<string> AllowedWorkflowCategories { get; }

    /// <summary>
    /// <c>true</c> when all four allow-lists are empty — the caller is a national
    /// administrator and no scoping predicate is applied. Computed property; equivalent
    /// to <c>AllowedRegions.Count == 0 &amp;&amp; AllowedSubdivisionCodes.Count == 0
    /// &amp;&amp; AllowedDocumentCategories.Count == 0 &amp;&amp;
    /// AllowedWorkflowCategories.Count == 0</c>. Implementations should pre-compute
    /// this so the per-request hot path doesn't recount on every check.
    /// </summary>
    bool IsUnscoped { get; }
}
