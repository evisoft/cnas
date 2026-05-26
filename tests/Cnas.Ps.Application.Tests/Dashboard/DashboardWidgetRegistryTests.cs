using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Dashboard;

/// <summary>
/// R0530 / R0531 / CF 04.01-04.02 — unit tests pinning the per-role personalisation
/// behaviour of <see cref="DashboardWidgetRegistry"/>. The registry is the source of
/// truth for which widgets belong to which CF 04.02 category and which roles are
/// permitted to see them — drift here would silently re-introduce the iter-114
/// "every widget for every caller" gap that R0530 / R0531 close.
/// </summary>
public sealed class DashboardWidgetRegistryTests
{
    private static readonly string[] DeciderRoles = ["cnas-decider"];
    private static readonly string[] UserRoles = ["cnas-user"];
    private static readonly string[] AdminLower = ["cnas-admin"];
    private static readonly string[] AdminUpper = ["CNAS-ADMIN"];

    /// <summary>
    /// Caller carries no roles — only widgets that declare the wildcard <c>"*"</c>
    /// role surface. Pins the deny-by-default contract: a brand-new role we have not
    /// yet whitelisted MUST NOT see role-gated widgets.
    /// </summary>
    [Fact]
    public void VisibleTo_AnonymousRoleSet_ReturnsWildcardWidgetsOnly()
    {
        var registry = DashboardWidgetRegistry.Default;

        var visible = registry.VisibleTo(Array.Empty<string>());

        visible.Should().NotBeEmpty(
            "the default catalogue MUST expose at least one widget visible to every authenticated caller");
        visible.Should().OnlyContain(d => d.SupportedRoles.Contains("*"),
            "an empty role set must only match wildcard descriptors");
    }

    /// <summary>
    /// A caller carrying <c>cnas-decider</c> sees the APPROVAL_QUEUE tile because that
    /// widget is gated on the decider / admin roles. A caller with only <c>cnas-user</c>
    /// must NOT see it.
    /// </summary>
    [Fact]
    public void VisibleTo_DeciderRole_IncludesApprovalQueueWidget()
    {
        var registry = DashboardWidgetRegistry.Default;

        var deciderView = registry.VisibleTo(DeciderRoles);
        var userView = registry.VisibleTo(UserRoles);

        deciderView.Should().Contain(d => d.Code == "APPROVAL_QUEUE");
        userView.Should().NotContain(d => d.Code == "APPROVAL_QUEUE",
            "cnas-user has no business approving — the approval-queue tile MUST hide for them");
    }

    /// <summary>
    /// Role matching is case-insensitive — the registry must accept the same role code
    /// whether it arrives as <c>cnas-admin</c>, <c>CNAS-ADMIN</c>, or mixed case from
    /// upstream identity providers (MPass attribute names vary).
    /// </summary>
    [Fact]
    public void VisibleTo_RoleMatching_IsCaseInsensitive()
    {
        var registry = DashboardWidgetRegistry.Default;

        var lower = registry.VisibleTo(AdminLower);
        var upper = registry.VisibleTo(AdminUpper);

        upper.Should().BeEquivalentTo(lower,
            "MPass / OIDC casing variations MUST NOT change the visible widget set");
    }

    /// <summary>
    /// Every CF 04.02 canonical category MUST be represented by at least one widget in
    /// the default catalogue. A regression here means the dashboard would silently
    /// drop a whole category bucket from the snapshot.
    /// </summary>
    [Fact]
    public void Default_Catalogue_CoversEveryCanonicalCategory()
    {
        var registry = DashboardWidgetRegistry.Default;
        var categories = registry.All.Select(d => d.Category).Distinct().ToHashSet();

        foreach (var canonical in Enum.GetValues<DashboardCategory>())
        {
            categories.Should().Contain(canonical,
                $"CF 04.02 requires a widget for category {canonical}");
        }
    }

    /// <summary>
    /// Constructing a registry with a duplicate widget code is a programmer error — it
    /// MUST throw rather than silently accept the second occurrence (the layout
    /// preferences subsystem references widgets by code).
    /// </summary>
    [Fact]
    public void Constructor_DuplicateCode_Throws()
    {
        var dup = new List<DashboardWidgetDescriptor>
        {
            new("DUP", DashboardCategory.SystemNotifications, ["*"], 100),
            new("dup", DashboardCategory.TaskArrivals, ["*"], 110),
        };

        var act = () => new DashboardWidgetRegistry(dup);

        act.Should().Throw<ArgumentException>("widget codes are the layout-preferences join key — duplicates MUST be rejected");
    }

    /// <summary>
    /// Lookup by code is case-insensitive and returns null on a miss rather than
    /// throwing — that lets the dashboard service degrade gracefully when an
    /// upstream layout preference references a widget that was retired.
    /// </summary>
    [Fact]
    public void FindByCode_UnknownCode_ReturnsNull()
    {
        var registry = DashboardWidgetRegistry.Default;

        registry.FindByCode("DOES_NOT_EXIST").Should().BeNull();
        registry.FindByCode("apps_open").Should().NotBeNull(
            "lookup MUST be case-insensitive — UI persistence can lower-case codes");
    }
}
