namespace Cnas.Ps.Contracts;

/// <summary>
/// UC04 / CF 04.01 — one KPI / counter widget rendered on a citizen or staff dashboard.
/// Carries an aggregate scalar value (counter, gauge, sum) plus presentation metadata
/// (translated title + optional unit suffix). When the producer assigns the widget to
/// one of the five canonical tile categories per CF 04.02 the value lives on
/// <see cref="Category"/>; otherwise (legacy callers) the category stays <c>null</c> and
/// the UI renders the tile in the default "Indicatori" bucket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire-stable fields.</b> The first four positional parameters
/// (<see cref="Code"/>, <see cref="Title"/>, <see cref="Value"/>, <see cref="Unit"/>) are the
/// pre-iter-115 shape and MUST NOT be reordered — JSON consumers and integration tests pin
/// the positional names. <see cref="Category"/> is additive and optional: legacy clients
/// that ignore it keep working; new clients that read it can group tiles by category per
/// CF 04.02.
/// </para>
/// <para>
/// <b>Serialisation of <see cref="Category"/>.</b> The category is the string name of the
/// <see cref="DashboardCategory"/> enum (e.g. <c>"SystemNotifications"</c>) so the wire
/// shape stays stable across protobuf-like consumers; do NOT expose the raw enum int value.
/// </para>
/// </remarks>
/// <param name="Code">Stable widget code (e.g. <c>APPS_OPEN</c>). Used as the React key
/// on the UI side and as the audit-log target.</param>
/// <param name="Title">Localised title rendered above the value. The producer resolves the
/// translation; the controller passes the result through untouched.</param>
/// <param name="Value">Aggregate numeric value (counter / gauge / sum). Decimal so we can
/// host both integer counters and ratio gauges without a wider DTO family.</param>
/// <param name="Unit">Optional unit suffix (e.g. <c>"cereri"</c>, <c>"%"</c>, <c>"MDL"</c>).
/// <c>null</c> when the tile is a unit-less integer counter.</param>
/// <param name="Category">Canonical tile-category bucket per CF 04.02. <c>null</c> on
/// legacy widgets that pre-date the categorisation pass.</param>
/// <param name="DeepLinkUrl">R0534 / TOR CF 04.05-06 — optional UI deep-link route the
/// dashboard renders as an anchor target so a click on the tile drills into the
/// underlying record list. <c>null</c> on tiles with no canonical drill-down.</param>
/// <param name="Trend">R0533 / TOR CF 04.04 — optional trend indicator
/// (<c>"Up"</c> / <c>"Down"</c> / <c>"Flat"</c>) used by the UI to render a directional
/// glyph next to the counter. <c>null</c> when no historical baseline is available.</param>
public sealed record KpiWidget(
    string Code,
    string Title,
    decimal Value,
    string? Unit,
    string? Category = null,
    string? DeepLinkUrl = null,
    string? Trend = null);

/// <summary>
/// R0533 / TOR CF 04.04 — one cell of the aggregate KPI grid surfaced on the dashboard
/// alongside the per-category tile producers. Each cell carries a stable code, a
/// localised title, the current aggregate value, an optional trend direction
/// ("Up" / "Down" / "Flat") relative to the previous snapshot, and an optional deep-link
/// URL that the UI renders as an anchor target so the operator can drill from the KPI
/// into the underlying record list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire-stable trend.</b> The trend is serialised as a string ("Up" / "Down" /
/// "Flat" / null) rather than the enum int so JSON consumers stay stable across
/// re-orderings of the underlying enum.
/// </para>
/// <para>
/// <b>Sensitivity.</b> The DTO is metadata — Public by default. Operators may see
/// aggregate counts; the underlying entity ids never appear here, only the deep-link
/// URL which already carries a Sqid-encoded resource id when present.
/// </para>
/// </remarks>
/// <param name="Code">Stable KPI cell code (e.g. <c>DOCS_PENDING_APPROVAL</c>). Used as the
/// React key on the UI side and as the audit-log target.</param>
/// <param name="Title">Localised title rendered above the value.</param>
/// <param name="Value">Aggregate numeric value (counter / gauge / sum).</param>
/// <param name="Trend">Optional trend indicator: <c>"Up"</c>, <c>"Down"</c>, <c>"Flat"</c>,
/// or <c>null</c> when no previous comparison is available.</param>
/// <param name="DeepLinkUrl">Optional deep-link route the UI renders as an anchor target
/// for click-through to the underlying record list. <c>null</c> when the cell is
/// purely informational and has no drill-down.</param>
public sealed record KpiGridCellDto(
    string Code,
    string Title,
    decimal Value,
    string? Trend,
    string? DeepLinkUrl);

/// <summary>
/// R0533 / TOR CF 04.04 — paged-style envelope surfaced by the dashboard service
/// alongside the per-category tile producers. Carries the aggregate KPI grid (cells)
/// plus the legacy widget list (kept for backwards compatibility — both lists are
/// emitted on the same payload).
/// </summary>
/// <param name="Widgets">Legacy per-category widgets (R0530 / R0531).</param>
/// <param name="KpiGrid">R0533 / CF 04.04 aggregate KPI grid cells.</param>
public sealed record DashboardSnapshotDto(
    IReadOnlyList<KpiWidget> Widgets,
    IReadOnlyList<KpiGridCellDto> KpiGrid);

/// <summary>
/// UC04 / CF 04.02 — canonical five-bucket tile classification rendered on every CNAS
/// dashboard. The set is intentionally closed: extensions to this list are a breaking
/// contract change for downstream dashboards and require a new <c>R05xx</c> requirement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire shape.</b> Always serialise as the enum member's string name
/// (e.g. <c>"SystemNotifications"</c>) rather than the raw int — protobuf-like consumers
/// and JSON-Schema validators rely on the stable identifier and breaking the integer
/// ordering MUST NOT propagate to the wire. See <see cref="KpiWidget.Category"/> for the
/// canonical carrier.
/// </para>
/// <para>
/// <b>Sensitivity.</b> The enum itself is metadata — Public by default. Tile payloads
/// (counter values, attached entity Sqids) inherit the surrounding DTO's classification.
/// </para>
/// </remarks>
public enum DashboardCategory
{
    /// <summary>
    /// CF 04.02 — broadcast / system-wide notifications surfaced to the caller. Sourced
    /// from <c>Notification</c> rows whose <c>RecipientUserId</c> matches the caller and
    /// whose <c>ReadAtUtc</c> is null. The "Notifications.UnreadCount" tile lives in
    /// this bucket.
    /// </summary>
    SystemNotifications,

    /// <summary>
    /// CF 04.02 — newly-arrived workflow tasks awaiting the caller. Sourced from
    /// <c>WorkflowTask</c> rows whose <c>AssignedUserId</c> matches the caller and whose
    /// <c>Status</c> is <c>Pending</c>. The "Tasks.Pending" tile lives in this bucket.
    /// </summary>
    TaskArrivals,

    /// <summary>
    /// CF 04.02 — status changes on workflows the caller is involved in but does not
    /// currently own. Sourced from <c>WorkflowTaskStepHistory</c> rows whose actor is NOT
    /// the caller (system events + sibling-step actors) for tasks where the caller was
    /// previously an assignee. The "Workflow.UpdatesLast24h" tile lives in this bucket.
    /// </summary>
    WorkflowUpdates,

    /// <summary>
    /// CF 04.02 — items where the caller is an actor on the current step (currently
    /// owns the task). Sourced from <c>WorkflowTask</c> rows whose <c>AssignedUserId</c>
    /// equals the caller AND whose <c>Status</c> is <c>InProgress</c>. The
    /// "Workflow.RequiringInvolvement" tile lives in this bucket.
    /// </summary>
    ItemsRequiringInvolvement,

    /// <summary>
    /// CF 04.02 — items in the caller's approval queue. Sourced from
    /// <c>PendingAdminAction</c> rows AND from <c>WorkflowTask</c> rows whose title /
    /// step code carries an "approval" / "approve" anchor. The
    /// "Approval.QueueDepth" tile lives in this bucket.
    /// </summary>
    ItemsAwaitingApproval,
}
