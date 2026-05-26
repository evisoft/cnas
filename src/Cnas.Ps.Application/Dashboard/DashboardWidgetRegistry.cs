using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Dashboard;

/// <summary>
/// R0530 / CF 04.01 — central declaration of every dashboard widget code together
/// with the role-set it is shown to AND the canonical CF 04.02 category it belongs
/// to. The registry is consulted by the dashboard service to:
/// <list type="number">
///   <item>filter the producer set to those whose <c>SupportedRoles</c> match the
///         caller's roles (per-role personalisation per CF 04.01 / CF 04.03);</item>
///   <item>assign the canonical <see cref="DashboardCategory"/> to every emitted
///         <see cref="KpiWidget"/> (per CF 04.02 five-category split).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton, stateless.</b> The registry is a process-static immutable lookup
/// table — register as <c>Singleton</c>. The lookup is O(N) over the descriptor list,
/// which is small (≤ ~25 widgets per the CF 04.02-04.09 superset).
/// </para>
/// <para>
/// <b>Wildcard role.</b> A descriptor that lists the single role <c>"*"</c> means
/// "every authenticated caller" — the registry treats it as an unconditional pass.
/// Otherwise, the caller's roles must intersect the descriptor's role set (OrdinalIgnoreCase).
/// </para>
/// <para>
/// <b>Source of truth.</b> The default catalogue declared by
/// <see cref="Default"/> is the source of truth for the UC04 dashboard. Tests pin the
/// shape so a missing CF clause regresses loudly.
/// </para>
/// </remarks>
public sealed class DashboardWidgetRegistry
{
    private static readonly string[] Wildcard = ["*"];

    /// <summary>
    /// Default UC04 dashboard widget catalogue per CF 04.01-04.02. Order is the UI
    /// rendering order. New widget codes append to the tail so the existing positions
    /// stay stable for snapshot tests / persisted layout preferences.
    /// </summary>
    public static readonly DashboardWidgetRegistry Default = new(
    [
        // ── Legacy KPI tiles (UC04 first-pass, pre-iter-115). Visible to every authenticated caller. ──
        new DashboardWidgetDescriptor(
            Code: "APPS_OPEN",
            Category: DashboardCategory.WorkflowUpdates,
            SupportedRoles: Wildcard,
            Position: 100),
        new DashboardWidgetDescriptor(
            Code: "DOSSIERS_OPEN",
            Category: DashboardCategory.WorkflowUpdates,
            SupportedRoles: Wildcard,
            Position: 110),
        new DashboardWidgetDescriptor(
            Code: "INSURED_TOTAL",
            Category: DashboardCategory.WorkflowUpdates,
            SupportedRoles: ["cnas-decider", "cnas-admin"],
            Position: 120),

        // ── R0531 / CF 04.02 five-category tile producers (iter 115). ──
        new DashboardWidgetDescriptor(
            Code: "NOTIFICATIONS_UNREAD",
            Category: DashboardCategory.SystemNotifications,
            SupportedRoles: Wildcard,
            Position: 200),
        new DashboardWidgetDescriptor(
            Code: "TASKS_PENDING",
            Category: DashboardCategory.TaskArrivals,
            SupportedRoles: Wildcard,
            Position: 210),
        new DashboardWidgetDescriptor(
            Code: "WORKFLOW_UPDATES_LAST24H",
            Category: DashboardCategory.WorkflowUpdates,
            SupportedRoles: Wildcard,
            Position: 220),
        new DashboardWidgetDescriptor(
            Code: "INVOLVEMENT_ITEMS",
            Category: DashboardCategory.ItemsRequiringInvolvement,
            SupportedRoles: Wildcard,
            Position: 230),
        new DashboardWidgetDescriptor(
            Code: "APPROVAL_QUEUE",
            Category: DashboardCategory.ItemsAwaitingApproval,
            SupportedRoles: ["cnas-decider", "cnas-admin", "seful-directiei", "seful-cnas"],
            Position: 240),

        // ── R0532 / iter 134 — decider-only depth tile that pins observable role
        //    differentiation. Approval queue (above) is shared decider/admin; this
        //    depth view is decider-only so admin and decider visibility envelopes
        //    are demonstrably distinct (regression-pinned by RoleScopedFilteringTests).
        new DashboardWidgetDescriptor(
            Code: "APPROVAL_QUEUE_DEPTH",
            Category: DashboardCategory.ItemsAwaitingApproval,
            SupportedRoles: ["cnas-decider"],
            Position: 250),

        // ── R0536 / CF 04.09 (iter 134) — Solicitant-scoped KPI tiles produced by
        //    MyRequests*KpiProducer. The dashboard service filters producer output
        //    against this registry; widgets whose code is not declared here would be
        //    silently dropped. Static codes are listed individually below; the
        //    per-status histogram emits codes matching <see cref="StatusHistogramCodePrefix"/>
        //    plus the status name — those are handled by prefix matching in
        //    <see cref="StatusHistogramCovers"/>.
        new DashboardWidgetDescriptor(
            Code: "MY_REQUESTS_IN_EXAMINATION",
            Category: DashboardCategory.WorkflowUpdates,
            SupportedRoles: Wildcard,
            Position: 260),
        new DashboardWidgetDescriptor(
            Code: "MY_REQUESTS_COMPLETED_IN_WINDOW",
            Category: DashboardCategory.WorkflowUpdates,
            SupportedRoles: Wildcard,
            Position: 270),
    ]);

    /// <summary>
    /// R0536 / iter 134 — code prefix for the per-status histogram tiles emitted by
    /// <c>MyRequestsByStatusKpiProducer</c>. Each enum value of
    /// <c>ApplicationStatus</c> produces a widget whose code is this prefix + an
    /// underscore + the upper-cased status name. Codes matching this prefix bypass
    /// the registry's strict per-code visibility filter because the histogram is
    /// data-driven (one tile per status the caller actually has applications in).
    /// </summary>
    public const string StatusHistogramCodePrefix = "MY_REQUESTS_STATUS_";

    private readonly IReadOnlyList<DashboardWidgetDescriptor> _descriptors;

    /// <summary>
    /// Constructs a registry over the supplied descriptor list. Used by tests to
    /// inject custom catalogues; production code consumes <see cref="Default"/>.
    /// </summary>
    /// <param name="descriptors">The widget descriptor list. Must not be null or
    /// contain duplicates by <see cref="DashboardWidgetDescriptor.Code"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="descriptors"/> is null.</exception>
    /// <exception cref="ArgumentException">The list contains a duplicate widget code.</exception>
    public DashboardWidgetRegistry(IReadOnlyList<DashboardWidgetDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in descriptors)
        {
            if (!seen.Add(d.Code))
            {
                throw new ArgumentException(
                    $"Duplicate dashboard widget code '{d.Code}'.",
                    nameof(descriptors));
            }
        }
        _descriptors = descriptors;
    }

    /// <summary>
    /// Returns every registered widget descriptor in declaration order (rendering
    /// order). The returned list is safe to enumerate without further filtering when
    /// the caller wants the full catalogue — call <see cref="VisibleTo(IReadOnlyCollection{string})"/>
    /// instead when role-personalisation is required.
    /// </summary>
    public IReadOnlyList<DashboardWidgetDescriptor> All => _descriptors;

    /// <summary>
    /// Filters the catalogue to those descriptors whose <see cref="DashboardWidgetDescriptor.SupportedRoles"/>
    /// intersects the supplied role set (or who declare the wildcard <c>"*"</c>).
    /// The output preserves declaration order (i.e. rendering order).
    /// </summary>
    /// <param name="callerRoles">Role codes carried by the caller (typically
    /// <c>ICallerContext.Roles</c>). Match is OrdinalIgnoreCase.</param>
    /// <returns>The list of descriptors the caller is allowed to see.</returns>
    public IReadOnlyList<DashboardWidgetDescriptor> VisibleTo(IReadOnlyCollection<string> callerRoles)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);
        var rolesSet = new HashSet<string>(callerRoles, StringComparer.OrdinalIgnoreCase);
        var matches = new List<DashboardWidgetDescriptor>(_descriptors.Count);
        foreach (var d in _descriptors)
        {
            if (d.IsVisibleTo(rolesSet))
            {
                matches.Add(d);
            }
        }
        return matches;
    }

    /// <summary>
    /// Looks up a single descriptor by stable widget code. Returns <c>null</c> when
    /// the code is not registered — callers should treat that as a missing-tile
    /// fallthrough rather than a hard failure.
    /// </summary>
    /// <param name="code">Stable widget code (case-insensitive match).</param>
    /// <returns>The matching descriptor, or <c>null</c> when no descriptor with the
    /// supplied code is registered.</returns>
    public DashboardWidgetDescriptor? FindByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        foreach (var d in _descriptors)
        {
            if (string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                return d;
            }
        }
        return null;
    }
}

/// <summary>
/// R0530 / R0531 — single row of the <see cref="DashboardWidgetRegistry"/>. Carries
/// the stable widget code, the canonical CF 04.02 category it belongs to, the role
/// set it is shown to, and a numeric rendering position the UI uses to lay out the
/// grid (lower = leftmost / topmost).
/// </summary>
/// <param name="Code">Stable widget code (case-insensitive). Cross-referenced from
/// <c>UserProfile.LayoutPreferences</c> for per-user re-ordering.</param>
/// <param name="Category">Canonical CF 04.02 category this widget contributes to.</param>
/// <param name="SupportedRoles">Role codes that may see this widget. The single-element
/// list containing <c>"*"</c> means "every authenticated caller".</param>
/// <param name="Position">Numeric rendering order; lower renders first. Stable across
/// releases so persisted layout preferences keep working.</param>
public sealed record DashboardWidgetDescriptor(
    string Code,
    DashboardCategory Category,
    IReadOnlyList<string> SupportedRoles,
    int Position)
{
    /// <summary>
    /// Tests whether this descriptor is visible to a caller carrying the supplied
    /// role set. Returns <c>true</c> when the descriptor declares the wildcard role
    /// <c>"*"</c>, otherwise when the caller's roles intersect
    /// <see cref="SupportedRoles"/> (OrdinalIgnoreCase).
    /// </summary>
    /// <param name="callerRoles">Roles the caller carries (already case-folded into a
    /// HashSet by the caller for O(1) lookup).</param>
    /// <returns><c>true</c> when the caller is allowed to see the widget.</returns>
    public bool IsVisibleTo(HashSet<string> callerRoles)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);
        for (int i = 0; i < SupportedRoles.Count; i++)
        {
            var role = SupportedRoles[i];
            if (role == "*") return true;
            if (callerRoles.Contains(role)) return true;
        }
        return false;
    }
}
