using System.Collections.Frozen;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.WorkflowAcl;

namespace Cnas.Ps.Infrastructure.AccessScope;

/// <summary>
/// R0671 / TOR CF 18.06 — convention-based <see cref="IAccessScope"/> implementation
/// that derives the per-request scope envelope from the caller's role / group
/// membership. Built per-request from the supplied <see cref="ICallerContext"/> via
/// <see cref="FromRoles(IReadOnlyCollection{string})"/>; immutable after construction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Role conventions.</b> The factory recognises four prefixes on the caller's role
/// strings and maps each to one of the four scope dimensions:
/// </para>
/// <list type="bullet">
///   <item><description><c>geo:&lt;region-code&gt;</c> → adds the trailing token to
///   <see cref="AllowedRegions"/> (e.g. <c>geo:CHIS</c>).</description></item>
///   <item><description><c>sub:&lt;branch-code&gt;</c> → adds the trailing token to
///   <see cref="AllowedSubdivisionCodes"/> (e.g. <c>sub:CHISINAU-CENTRU</c>).</description></item>
///   <item><description><c>cat:document.&lt;document-kind&gt;</c> → adds the trailing
///   token (the <see cref="Cnas.Ps.Core.Domain.DocumentKind"/> enum name) to
///   <see cref="AllowedDocumentCategories"/>.</description></item>
///   <item><description><c>cat:workflow.&lt;category&gt;</c> → adds the trailing token
///   to <see cref="AllowedWorkflowCategories"/>.</description></item>
/// </list>
/// <para>
/// Roles that do not match any prefix are ignored by the scope builder — they remain
/// in <see cref="ICallerContext.Roles"/> for the controller-level RBAC gates.
/// </para>
/// <para>
/// <b>Super-admin escape hatch.</b> Holders of <see cref="WorkflowAclConstants.SuperAdminRole"/>
/// (<c>cnas-tech-admin</c>) or the <c>cnas-admin</c> role bypass scoping entirely —
/// the resulting envelope has every allow-list empty AND <see cref="IsUnscoped"/> = true.
/// This is the "national administrator" hot path; the security guarantee composes with
/// the same break-glass override that the workflow ACL uses.
/// </para>
/// <para>
/// <b>Anonymous fallback.</b> When the caller has NO scoping-prefixed roles, the
/// resulting envelope is also fully unscoped. This is intentional: anonymous /
/// public callers reach the access-scope filter only through code paths that have
/// already been gated; granting them an empty scope keeps the filter a no-op on
/// those paths. Authenticated staff who genuinely have no scope assigned must
/// receive at least one scoping role before they appear on any registry — that is a
/// user-administration responsibility, not the scope envelope's.
/// </para>
/// </remarks>
public sealed class RolesBasedAccessScope : IAccessScope
{
    /// <summary>The literal prefix matched against the caller's role strings to
    /// extract region codes.</summary>
    internal const string RegionPrefix = "geo:";

    /// <summary>The literal prefix matched to extract subdivision codes.</summary>
    internal const string SubdivisionPrefix = "sub:";

    /// <summary>The literal prefix matched to extract document-category labels.</summary>
    internal const string DocumentCategoryPrefix = "cat:document.";

    /// <summary>The literal prefix matched to extract workflow-category labels.</summary>
    internal const string WorkflowCategoryPrefix = "cat:workflow.";

    /// <summary>The second role recognised as an unconditional-bypass national admin.
    /// Pairs with <see cref="WorkflowAclConstants.SuperAdminRole"/>.</summary>
    internal const string NationalAdminRole = "cnas-admin";

    /// <summary>Empty frozen set reused for every dimension that ends up unrestricted.
    /// Avoids allocating a fresh empty set per request.</summary>
    private static readonly FrozenSet<string> EmptySet = FrozenSet<string>.Empty;

    /// <summary>Initialises a new envelope from the four pre-canonicalised allow-lists.</summary>
    /// <param name="regions">Region codes; empty = no restriction.</param>
    /// <param name="subdivisions">Subdivision codes; empty = no restriction.</param>
    /// <param name="documentCategories">Document categories; empty = no restriction.</param>
    /// <param name="workflowCategories">Workflow categories; empty = no restriction.</param>
    public RolesBasedAccessScope(
        IReadOnlyCollection<string> regions,
        IReadOnlyCollection<string> subdivisions,
        IReadOnlyCollection<string> documentCategories,
        IReadOnlyCollection<string> workflowCategories)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(subdivisions);
        ArgumentNullException.ThrowIfNull(documentCategories);
        ArgumentNullException.ThrowIfNull(workflowCategories);

        AllowedRegions = regions;
        AllowedSubdivisionCodes = subdivisions;
        AllowedDocumentCategories = documentCategories;
        AllowedWorkflowCategories = workflowCategories;
        // Pre-compute the unscoped flag once so the per-request hot path doesn't
        // recount four collections on every check.
        IsUnscoped = regions.Count == 0
            && subdivisions.Count == 0
            && documentCategories.Count == 0
            && workflowCategories.Count == 0;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> AllowedRegions { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> AllowedSubdivisionCodes { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> AllowedDocumentCategories { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> AllowedWorkflowCategories { get; }

    /// <inheritdoc />
    public bool IsUnscoped { get; }

    /// <summary>
    /// Singleton unscoped envelope reused for anonymous callers + super-admins so
    /// the hot path doesn't allocate a fresh instance per request.
    /// </summary>
    public static IAccessScope Unscoped { get; } = new RolesBasedAccessScope(
        EmptySet, EmptySet, EmptySet, EmptySet);

    /// <summary>
    /// Builds a fresh <see cref="RolesBasedAccessScope"/> from the supplied caller
    /// role set. Recognises the four <c>geo: / sub: / cat:document. / cat:workflow.</c>
    /// prefixes and the two super-admin role names; everything else is ignored.
    /// </summary>
    /// <param name="roles">The caller's role set, typically
    /// <see cref="ICallerContext.Roles"/>.</param>
    /// <returns>An immutable scope envelope ready to be used for the request.</returns>
    public static IAccessScope FromRoles(IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        // Super-admin / national-admin short-circuit: the singleton unscoped instance
        // avoids the per-request enumeration + four list allocations.
        foreach (var role in roles)
        {
            if (string.Equals(role, WorkflowAclConstants.SuperAdminRole, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, NationalAdminRole, StringComparison.OrdinalIgnoreCase))
            {
                return Unscoped;
            }
        }

        // Tally each dimension into a HashSet so duplicate role assignments collapse.
        var regions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subdivisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var documentCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workflowCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in roles)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }
            // Document-category and workflow-category share the "cat:" prefix — match the
            // more specific (workflow / document.*) discriminator FIRST so e.g.
            // "cat:document.medical" is not mis-routed into AllowedWorkflowCategories
            // (which would happen if we tested "cat:" alone first).
            if (TryStrip(role, DocumentCategoryPrefix, out var docCat))
            {
                documentCategories.Add(docCat);
            }
            else if (TryStrip(role, WorkflowCategoryPrefix, out var wfCat))
            {
                workflowCategories.Add(wfCat);
            }
            else if (TryStrip(role, RegionPrefix, out var region))
            {
                regions.Add(region);
            }
            else if (TryStrip(role, SubdivisionPrefix, out var sub))
            {
                subdivisions.Add(sub);
            }
        }

        return new RolesBasedAccessScope(
            regions.Count == 0 ? EmptySet : regions.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            subdivisions.Count == 0 ? EmptySet : subdivisions.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            documentCategories.Count == 0 ? EmptySet : documentCategories.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            workflowCategories.Count == 0 ? EmptySet : workflowCategories.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Case-insensitive prefix-strip helper. Returns <c>true</c> + the trailing token
    /// when <paramref name="role"/> begins with <paramref name="prefix"/>; otherwise
    /// <c>false</c> + an empty token.
    /// </summary>
    /// <param name="role">The role string.</param>
    /// <param name="prefix">The prefix to match.</param>
    /// <param name="value">Receives the trailing token on a match.</param>
    /// <returns><c>true</c> if the prefix matches and the trailing token is non-empty.</returns>
    private static bool TryStrip(string role, string prefix, out string value)
    {
        if (role.Length > prefix.Length
            && role.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = role[prefix.Length..];
            return value.Length > 0;
        }
        value = string.Empty;
        return false;
    }
}
