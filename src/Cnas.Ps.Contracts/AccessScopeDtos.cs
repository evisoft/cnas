using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0671 / TOR CF 18.06 — read-only descriptor of the caller's effective access scope.
/// Returned by <c>GET /api/profile/access-scope</c> so the UI can render banners,
/// pre-fill region selectors, and signal "you are seeing a scoped view" to the
/// operator. Mirrors <c>Cnas.Ps.Application.Abstractions.IAccessScope</c> 1:1 (the
/// cref is a plain string because Contracts may not reference Application); carries
/// no surrogate ids — the scope is derived from the caller's roles every request and
/// is not persisted as its own entity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sensitivity.</b> Each allow-list is tagged <c>Internal</c> because exposing
/// the exact set of regions / subdivisions / categories assigned to a user reveals
/// internal CNAS structure — non-public but not user-confidential. The flag
/// <see cref="IsUnscoped"/> is tagged <c>Public</c> because it only signals "this
/// caller has national scope or not", which is already implicit in the role list
/// the same controller can see.
/// </para>
/// </remarks>
/// <param name="AllowedRegions">Set of region codes the caller may see; empty = no
/// restriction by region.</param>
/// <param name="AllowedSubdivisionCodes">Set of subdivision codes
/// (<c>CnasBranch.Code</c> values) the caller may see; empty = no restriction.</param>
/// <param name="AllowedDocumentCategories">Set of <c>DocumentKind</c> enum names the
/// caller may see; empty = no restriction.</param>
/// <param name="AllowedWorkflowCategories">Set of workflow-category codes the caller
/// may see; empty = no restriction.</param>
/// <param name="IsUnscoped"><c>true</c> when all four allow-lists are empty (national
/// scope); <c>false</c> when at least one dimension is narrowed.</param>
public sealed record AccessScopeDescriptorDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyCollection<string> AllowedRegions,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyCollection<string> AllowedSubdivisionCodes,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyCollection<string> AllowedDocumentCategories,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyCollection<string> AllowedWorkflowCategories,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsUnscoped);
