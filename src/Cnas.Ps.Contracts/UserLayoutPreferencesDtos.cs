using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0535 / CF 04.07-08 — server-shaped user UI layout preferences. Carried by both
/// <c>GET</c> and <c>PUT /api/profile/layout-preferences</c>. The DTO mirrors the
/// <c>UserLayoutPreferences</c> value object 1-1; the controller / service is the
/// boundary that translates between the two so the persisted JSON shape never leaks
/// into the API consumer's tooling.
/// </summary>
/// <remarks>
/// <para>
/// <b>No Sqid id.</b> The owner is implicitly the authenticated caller (the controller
/// derives the user id from <c>ICallerContext</c>), so the DTO carries no identifier —
/// it's the owner's own preferences either way.
/// </para>
/// <para>
/// <b>Sensitivity = Internal.</b> Layout preferences are pure UI metadata — they
/// contain no PII (only column / widget identifier strings and integer page sizes).
/// They are still <see cref="SensitivityLabel.Internal"/> because they are per-user
/// state and should not appear in anonymous-readable responses.
/// </para>
/// </remarks>
/// <param name="Grids">
/// Per-grid layout overrides keyed by the stable kebab-case grid code (e.g.
/// <c>solicitants</c>, <c>cereri</c>, <c>tasks</c>). Empty dictionary = "use registry
/// defaults for every grid".
/// </param>
/// <param name="DefaultPageSize">
/// System-wide default page size used when a grid did not register a per-grid
/// override. Range [10, 200] enforced by the validator.
/// </param>
/// <param name="DashboardWidgetOrder">
/// Ordered list of dashboard widget codes the user prefers — earlier entries render
/// first. Codes not in the list trail behind in registry order so a partial save
/// still produces a usable dashboard.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record UserLayoutPreferencesDto(
    IReadOnlyDictionary<string, GridLayoutDto> Grids,
    int DefaultPageSize,
    IReadOnlyList<string> DashboardWidgetOrder);

/// <summary>
/// R0535 / CF 04.07-08 — server-shaped per-grid layout override. One entry per grid
/// the user has customised; absent grids render with their registry defaults.
/// </summary>
/// <remarks>
/// <para>
/// <b>Visibility vs order.</b> <see cref="VisibleColumns"/> is the SET of columns the
/// user has chosen to display; <see cref="ColumnOrder"/> is the ORDER in which those
/// columns appear. The renderer treats them as independent: columns in
/// <see cref="ColumnOrder"/> but not in <see cref="VisibleColumns"/> are skipped;
/// visible columns missing from the ordering list trail behind in registry order.
/// </para>
/// <para>
/// <b>PageSize override.</b> <c>null</c> means "use the parent
/// <see cref="UserLayoutPreferencesDto.DefaultPageSize"/>"; a positive value (range
/// [10, 200], enforced at the validator boundary) overrides the system default for
/// this specific grid.
/// </para>
/// </remarks>
/// <param name="VisibleColumns">Column codes the user wants displayed (empty = "all").</param>
/// <param name="ColumnOrder">User-preferred column order — trailing columns render after.</param>
/// <param name="PageSize">Optional per-grid page-size override.</param>
public sealed record GridLayoutDto(
    IReadOnlyList<string> VisibleColumns,
    IReadOnlyList<string> ColumnOrder,
    int? PageSize);

/// <summary>
/// R0535 / CF 04.07-08 — write-side wire shape accepted by <c>PUT
/// /api/profile/layout-preferences</c>. Identical to <see cref="UserLayoutPreferencesDto"/>
/// in shape, but the input/output split lets us tighten future validation (e.g. accept
/// nullable widget orders on input but always emit empty lists on output) without
/// breaking either side.
/// </summary>
/// <remarks>
/// <para>
/// <b>Whole-object PUT semantics.</b> The endpoint replaces the user's stored layout
/// in full — there is no merge / patch. The caller is expected to send the canonical
/// shape on every save; partial saves come through the API as the full updated
/// document.
/// </para>
/// </remarks>
/// <param name="Grids">Per-grid layout overrides (case-insensitive grid codes).</param>
/// <param name="DefaultPageSize">System-wide page-size default — range [10, 200].</param>
/// <param name="DashboardWidgetOrder">Preferred dashboard widget ordering.</param>
public sealed record UserLayoutPreferencesSaveDto(
    IReadOnlyDictionary<string, GridLayoutDto> Grids,
    int DefaultPageSize,
    IReadOnlyList<string> DashboardWidgetOrder);
