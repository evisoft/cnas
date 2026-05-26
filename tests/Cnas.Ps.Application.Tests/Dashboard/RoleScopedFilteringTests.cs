using Cnas.Ps.Application.Dashboard;

namespace Cnas.Ps.Application.Tests.Dashboard;

/// <summary>
/// R0532 / TOR CF 04.03 — role-scoped dashboard widget filtering. The
/// <see cref="DashboardWidgetRegistry"/>'s <see cref="DashboardWidgetRegistry.VisibleTo"/>
/// is the single point where role personalisation gets enforced; these tests pin the
/// per-role observability matrix so a future widget that drifts the role allow-list
/// regresses loudly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Universal vs role-gated.</b> A "universal" widget declares the wildcard role
/// <c>"*"</c> and surfaces for every authenticated caller (even unknown roles). A
/// "role-gated" widget declares an explicit role allow-list and only surfaces when
/// the caller's roles intersect it.
/// </para>
/// <para>
/// <b>Catalog assumptions.</b> The tests target the
/// <see cref="DashboardWidgetRegistry.Default"/> catalogue. New widgets append to
/// the tail of the catalogue per the registry's "stable position" contract — adding
/// one MUST keep the existing role / wildcard visibility invariants intact.
/// </para>
/// </remarks>
public sealed class RoleScopedFilteringTests
{
    /// <summary>
    /// A plain <c>cnas-user</c> (citizen-side caller) MUST see every universal widget
    /// AND MUST NOT see any widget gated to a CNAS staff role (decider, admin,
    /// șef-direcţie, șef-CNAS). The decider-only and approval-queue tiles are the
    /// canonical regression points.
    /// </summary>
    [Fact]
    public void VisibleTo_CnasUserRole_SeesUniversalWidgetsOnly()
    {
        var registry = DashboardWidgetRegistry.Default;

        var rolesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cnas-user" };
        var visible = registry.VisibleTo(["cnas-user"]);

        visible.Should().NotBeEmpty("every authenticated caller must see at least one universal widget");
        visible.Should().OnlyContain(d => d.IsVisibleTo(rolesSet));
        visible.Should().NotContain(d => d.Code == "APPROVAL_QUEUE",
            "approval queue is gated on decider/admin/seful-directiei/seful-cnas — cnas-user MUST NOT see it");
        visible.Should().NotContain(d => d.Code == "APPROVAL_QUEUE_DEPTH",
            "decider-only depth tile MUST NOT surface for a plain cnas-user");
        visible.Should().NotContain(d => d.Code == "INSURED_TOTAL",
            "total insured count is gated on cnas-decider/cnas-admin — cnas-user MUST NOT see it");
    }

    /// <summary>
    /// A <c>cnas-decider</c> sees every universal widget PLUS the decider-gated tiles:
    /// approval queue (shared with admin), insured total (shared with admin), AND the
    /// new decider-specific depth tile that pins role differentiation.
    /// </summary>
    [Fact]
    public void VisibleTo_CnasDeciderRole_SeesDeciderWidgets()
    {
        var registry = DashboardWidgetRegistry.Default;

        var visible = registry.VisibleTo(["cnas-decider"]);

        visible.Should().Contain(d => d.Code == "APPROVAL_QUEUE",
            "approval queue MUST surface for cnas-decider");
        visible.Should().Contain(d => d.Code == "APPROVAL_QUEUE_DEPTH",
            "decider depth-tile MUST surface for cnas-decider");
        visible.Should().Contain(d => d.Code == "INSURED_TOTAL",
            "insured total is shared decider/admin and MUST surface for cnas-decider");
        visible.Should().Contain(d => d.Code == "APPS_OPEN",
            "universal widgets stay visible to every authenticated caller including cnas-decider");
    }

    /// <summary>
    /// A <c>cnas-admin</c> sees every universal widget PLUS the admin-gated tiles
    /// (approval queue, insured total) — but NOT the decider-only depth tile. This
    /// guarantees that role differentiation is observable: admin and decider visibility
    /// envelopes are distinct, not equal.
    /// </summary>
    [Fact]
    public void VisibleTo_CnasAdminRole_SeesAdminWidgetsButNotDeciderOnlyDepthTile()
    {
        var registry = DashboardWidgetRegistry.Default;

        var visible = registry.VisibleTo(["cnas-admin"]);

        visible.Should().Contain(d => d.Code == "APPROVAL_QUEUE",
            "approval queue is shared decider/admin and MUST surface for cnas-admin");
        visible.Should().Contain(d => d.Code == "INSURED_TOTAL",
            "insured total is shared decider/admin and MUST surface for cnas-admin");
        visible.Should().NotContain(d => d.Code == "APPROVAL_QUEUE_DEPTH",
            "depth tile is decider-only — admin sees the queue but not the decider depth view");
        visible.Should().Contain(d => d.Code == "APPS_OPEN",
            "universal widgets MUST stay visible to every authenticated caller including cnas-admin");
    }

    /// <summary>
    /// A caller carrying a brand-new role we have never whitelisted MUST only see the
    /// universal (wildcard <c>"*"</c>) widgets — every role-gated tile MUST hide. This
    /// pins the deny-by-default contract.
    /// </summary>
    [Fact]
    public void VisibleTo_UnknownRole_SeesOnlyUniversalWidgets()
    {
        var registry = DashboardWidgetRegistry.Default;

        var visible = registry.VisibleTo(["some-future-role-not-yet-whitelisted"]);

        visible.Should().NotBeEmpty("universal widgets surface for every authenticated caller");
        visible.Should().OnlyContain(d => d.SupportedRoles.Contains("*"),
            "an unknown role MUST only match wildcard descriptors (deny-by-default)");
        visible.Should().NotContain(d => d.Code == "APPROVAL_QUEUE");
        visible.Should().NotContain(d => d.Code == "INSURED_TOTAL");
        visible.Should().NotContain(d => d.Code == "APPROVAL_QUEUE_DEPTH");
    }

    /// <summary>
    /// The decider-only depth tile is the canonical role-differentiator landed in
    /// iter 134 to make per-role widget allow-lists observable. Test pins:
    /// <list type="bullet">
    ///   <item>registered in the default catalogue (so the next iter cannot drop it silently),</item>
    ///   <item>tagged with the <see cref="DashboardCategory.ItemsAwaitingApproval"/> bucket,</item>
    ///   <item>role allow-list contains <c>cnas-decider</c> only (no wildcard, no admin).</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Default_Catalogue_DeclaresDeciderOnlyApprovalDepthTile()
    {
        var registry = DashboardWidgetRegistry.Default;

        var depth = registry.FindByCode("APPROVAL_QUEUE_DEPTH");

        depth.Should().NotBeNull("the decider depth tile MUST be registered in the default catalogue");
        depth!.Category.Should().Be(Cnas.Ps.Contracts.DashboardCategory.ItemsAwaitingApproval,
            "approval-depth belongs to the ItemsAwaitingApproval bucket per CF 04.02");
        depth.SupportedRoles.Should().BeEquivalentTo(["cnas-decider"],
            "depth tile MUST surface for cnas-decider only — adding admin to the list would erase the role differentiation this widget exists to pin");
    }
}
