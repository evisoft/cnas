using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure.AccessScope;

namespace Cnas.Ps.Infrastructure.Tests.AccessScope;

/// <summary>
/// R0671 / TOR CF 18.06 — unit tests for <see cref="RolesBasedAccessScope.FromRoles"/>.
/// Exercises the four prefix conventions (<c>geo:</c>, <c>sub:</c>, <c>cat:document.</c>,
/// <c>cat:workflow.</c>), the two super-admin escape hatches (<c>cnas-tech-admin</c>,
/// <c>cnas-admin</c>), and the "no scoping roles → unscoped" fallback.
/// </summary>
public sealed class RolesBasedAccessScopeTests
{
    /// <summary>Single-region role set used by the canonical positive case.</summary>
    private static readonly string[] GeoOnly = ["geo:CHIS"];

    /// <summary>Expected allow-list output for <see cref="GeoOnly"/>.</summary>
    private static readonly string[] ExpectedGeoOnly = ["CHIS"];

    /// <summary>Roles for the super-admin override test (extra <c>cnas-tech-admin</c>).</summary>
    private static readonly string[] GeoPlusTechAdmin = ["geo:CHIS", "cnas-tech-admin"];

    /// <summary>Roles for the no-scoping-roles fallback test.</summary>
    private static readonly string[] NonScopingRoles = ["cnas-user", "cnas-clerk"];

    /// <summary>Roles exercising every prefix in one call.</summary>
    private static readonly string[] AllPrefixes =
    [
        "geo:CHIS", "geo:BLT", "sub:CHISINAU-CENTRU",
        "cat:document.Decision", "cat:workflow.pension", "cnas-clerk",
    ];

    /// <summary>Expected region allow-list for the all-prefix test.</summary>
    private static readonly string[] ExpectedAllRegions = ["CHIS", "BLT"];

    /// <summary>Expected subdivision allow-list for the all-prefix test.</summary>
    private static readonly string[] ExpectedAllSubdivisions = ["CHISINAU-CENTRU"];

    /// <summary>Expected document-category allow-list for the all-prefix test.</summary>
    private static readonly string[] ExpectedAllDocumentCategories = ["Decision"];

    /// <summary>Expected workflow-category allow-list for the all-prefix test.</summary>
    private static readonly string[] ExpectedAllWorkflowCategories = ["pension"];

    /// <summary>Roles for the second super-admin alias (<c>cnas-admin</c>).</summary>
    private static readonly string[] GeoPlusNationalAdmin = ["geo:CHIS", "cnas-admin"];

    /// <summary>
    /// A user carrying <c>geo:CHIS</c> ends up with exactly one allowed region and
    /// IsUnscoped = false. This is the canonical positive case for the region axis.
    /// </summary>
    [Fact]
    public void FromRoles_WithGeoPrefix_PopulatesRegionsAndMarksScoped()
    {
        var scope = RolesBasedAccessScope.FromRoles(GeoOnly);

        scope.AllowedRegions.Should().BeEquivalentTo(ExpectedGeoOnly);
        scope.AllowedSubdivisionCodes.Should().BeEmpty();
        scope.AllowedDocumentCategories.Should().BeEmpty();
        scope.AllowedWorkflowCategories.Should().BeEmpty();
        scope.IsUnscoped.Should().BeFalse();
    }

    /// <summary>
    /// The <c>cnas-tech-admin</c> role short-circuits the entire builder and returns the
    /// unscoped singleton REGARDLESS of any other scoping roles the user also carries.
    /// This is the break-glass invariant; the workflow ACL service uses the same rule.
    /// </summary>
    [Fact]
    public void FromRoles_WithSuperAdmin_IsUnscopedEvenWithScopingRoles()
    {
        var scope = RolesBasedAccessScope.FromRoles(GeoPlusTechAdmin);

        scope.IsUnscoped.Should().BeTrue();
        scope.AllowedRegions.Should().BeEmpty();
    }

    /// <summary>
    /// A user with NO scoping-prefixed roles ends up fully unscoped — IsUnscoped = true.
    /// Anonymous + freshly-provisioned staff land here; the filter becomes a no-op.
    /// </summary>
    [Fact]
    public void FromRoles_WithNoScopingRoles_IsUnscoped()
    {
        var scope = RolesBasedAccessScope.FromRoles(NonScopingRoles);

        scope.IsUnscoped.Should().BeTrue();
    }

    /// <summary>
    /// All four scoping prefixes accumulate in their respective allow-lists in a single
    /// call. Asserts the prefix discriminator routes each role to the right bucket and
    /// that the workflow / document discriminator pair (which share the <c>cat:</c>
    /// prefix) does not cross-pollute.
    /// </summary>
    [Fact]
    public void FromRoles_WithAllFourPrefixes_PopulatesEachDimensionExactly()
    {
        var scope = RolesBasedAccessScope.FromRoles(AllPrefixes);

        scope.AllowedRegions.Should().BeEquivalentTo(ExpectedAllRegions);
        scope.AllowedSubdivisionCodes.Should().BeEquivalentTo(ExpectedAllSubdivisions);
        scope.AllowedDocumentCategories.Should().BeEquivalentTo(ExpectedAllDocumentCategories);
        scope.AllowedWorkflowCategories.Should().BeEquivalentTo(ExpectedAllWorkflowCategories);
        scope.IsUnscoped.Should().BeFalse();
    }

    /// <summary>
    /// The <c>cnas-admin</c> role is the second recognised super-admin variant.
    /// Verifies the second short-circuit path is wired the same way as
    /// <c>cnas-tech-admin</c>.
    /// </summary>
    [Fact]
    public void FromRoles_WithNationalAdmin_IsUnscoped()
    {
        var scope = RolesBasedAccessScope.FromRoles(GeoPlusNationalAdmin);

        scope.IsUnscoped.Should().BeTrue();
    }

    /// <summary>
    /// The <see cref="RolesBasedAccessScope.Unscoped"/> singleton must answer
    /// IsUnscoped = true with all four allow-lists empty. Guards the hot-path
    /// allocation we depend on for every request that resolves to the national
    /// administrator (or anonymous fallback).
    /// </summary>
    [Fact]
    public void Unscoped_Singleton_HasNoNarrowingAndIsUnscoped()
    {
        IAccessScope scope = RolesBasedAccessScope.Unscoped;

        scope.IsUnscoped.Should().BeTrue();
        scope.AllowedRegions.Should().BeEmpty();
        scope.AllowedSubdivisionCodes.Should().BeEmpty();
        scope.AllowedDocumentCategories.Should().BeEmpty();
        scope.AllowedWorkflowCategories.Should().BeEmpty();
    }
}
