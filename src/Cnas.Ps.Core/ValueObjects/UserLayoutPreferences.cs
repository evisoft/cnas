using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// R0535 / CF 04.07-08 — per-user UI layout configuration. Persisted as opaque JSON on
/// <c>UserProfile.LayoutPreferences</c>. Carries grid column visibility / order, page-size
/// defaults, and dashboard widget order keyed by a stable grid/widget code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a single JSON column.</b> The shape evolves with each new grid / widget that
/// the front-end introduces; a normalised schema would force a migration per addition.
/// JSONB gives compact storage on Postgres + future indexability, and the EF Core
/// InMemory provider used in tests treats it as a regular nullable string. The trade-off
/// is that parsing happens at the application boundary — see
/// <see cref="UserLayoutPreferencesJson.Parse(string)"/>.
/// </para>
/// <para>
/// <b>Default = "use system defaults".</b> A NULL column value is equivalent to
/// <see cref="Default"/> — every grid uses the registry's default columns and the
/// system-wide default page size. A new user therefore has no row migration cost; the
/// dispatcher chooses defaults on every read until the user explicitly saves a layout.
/// </para>
/// <para>
/// <b>Fail-open parse.</b> Malformed JSON returns <see cref="Default"/> AND increments
/// the <c>cnas.user_layout.parse_failure</c> counter so operators can chart silent
/// drift (a schema change that broke older rows). Notifications dispatch must never be
/// blocked by a UI preference parse error — the read path can't either; the UI simply
/// falls back to system defaults.
/// </para>
/// <para>
/// <b>No PII.</b> The JSON contains only column / widget identifiers and integer
/// page-size defaults. It does NOT carry national identifiers, addresses, or any other
/// personal data. The column therefore does NOT participate in the encrypted-at-rest
/// set (CLAUDE.md §5.7 / TOR SEC 035).
/// </para>
/// </remarks>
public sealed record UserLayoutPreferences
{
    /// <summary>
    /// Per-grid layout overrides keyed by the stable kebab-case grid code (e.g.
    /// <c>solicitants</c>, <c>cereri</c>, <c>tasks</c>). Defaults to an empty,
    /// case-insensitive dictionary — every grid renders with the registry defaults.
    /// </summary>
    public Dictionary<string, GridLayoutPreference> Grids { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// System-wide default page size used when a grid did not register a per-grid
    /// override. Range [10, 200] enforced at the validator boundary.
    /// </summary>
    public int DefaultPageSize { get; init; } = 25;

    /// <summary>
    /// Ordered list of dashboard widget codes the user prefers — earlier entries are
    /// rendered first. Codes not in the list render after the configured ones in their
    /// registry-declared order so a partial save still produces a usable dashboard.
    /// </summary>
    public List<string> DashboardWidgetOrder { get; init; } = [];

    /// <summary>
    /// Returns the fresh "system defaults" instance — empty grid overrides, page size
    /// 25, empty widget order. New users start here.
    /// </summary>
    public static UserLayoutPreferences Default => new();
}

/// <summary>
/// R0535 / CF 04.07-08 — per-grid layout override. Each instance describes one
/// registry's column visibility, ordering, and (optional) page-size override.
/// </summary>
/// <remarks>
/// <para>
/// <b>Visibility vs order.</b> <see cref="VisibleColumns"/> is the SET of columns the
/// user has chosen to display; <see cref="ColumnOrder"/> is the ORDER in which those
/// columns appear. The renderer treats the two as independent: columns in
/// <see cref="ColumnOrder"/> but not in <see cref="VisibleColumns"/> are skipped; columns
/// visible but missing from the ordering list trail behind in registry order.
/// </para>
/// <para>
/// <b>PageSize override.</b> <c>null</c> means "use the parent
/// <see cref="UserLayoutPreferences.DefaultPageSize"/>"; a positive value (range
/// [10, 200], enforced at the validator boundary) overrides the system default for this
/// specific grid.
/// </para>
/// </remarks>
public sealed record GridLayoutPreference
{
    /// <summary>The set of column codes the user wants displayed. Empty = "show all".</summary>
    public List<string> VisibleColumns { get; init; } = [];

    /// <summary>The user-preferred column order. Trailing registry columns render after.</summary>
    public List<string> ColumnOrder { get; init; } = [];

    /// <summary>
    /// Optional per-grid page-size override. <c>null</c> falls back to
    /// <see cref="UserLayoutPreferences.DefaultPageSize"/>. Validator enforces [10, 200].
    /// </summary>
    public int? PageSize { get; init; }
}

/// <summary>
/// Serialisation helpers for <see cref="UserLayoutPreferences"/>. Mirrors the design
/// of <see cref="NotificationPreferencesJson"/> — one import for the whole preference
/// subsystem, camelCase JSON shape, fail-open parse.
/// </summary>
/// <remarks>
/// <para>
/// The helpers use a <see cref="JsonSerializerOptions"/> instance with
/// <c>PropertyNamingPolicy = CamelCase</c> so the JSON on disk matches the wire shape
/// consumed by the React / Blazor front-end without an extra naming policy on the API
/// serializer.
/// </para>
/// <para>
/// <b>Fail-open parse.</b> <see cref="Parse(string)"/> returns
/// <see cref="UserLayoutPreferences.Default"/> for null / empty / malformed input
/// rather than throwing. A schema drift must NEVER prevent the dashboard from
/// rendering — the user just sees system defaults until they save again.
/// </para>
/// </remarks>
public static class UserLayoutPreferencesJson
{
    /// <summary>Shared serializer options — camelCase property names, compact output.</summary>
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Outcome of a <see cref="TryParse(string)"/> attempt. Records whether the parse
    /// succeeded so callers can increment the operator-facing parse-failure counter
    /// without having to reach into the serializer's exception surface.
    /// </summary>
    /// <param name="Value">Parsed (or default) preferences — never null.</param>
    /// <param name="Succeeded">
    /// <c>true</c> when the input was null/empty (legitimate "use defaults") OR
    /// parsed cleanly; <c>false</c> when the input was non-empty but malformed
    /// (operator should investigate via the counter).
    /// </param>
    public readonly record struct ParseOutcome(UserLayoutPreferences Value, bool Succeeded);

    /// <summary>
    /// Parses the persisted JSON column into a <see cref="UserLayoutPreferences"/>
    /// instance. NULL / empty / whitespace / malformed input returns
    /// <see cref="UserLayoutPreferences.Default"/> (fail-open contract).
    /// </summary>
    /// <param name="json">Raw JSON column value, or <c>null</c>.</param>
    /// <returns>Parsed preferences, or the system defaults on any failure.</returns>
    public static UserLayoutPreferences Parse(string? json) => TryParse(json).Value;

    /// <summary>
    /// Like <see cref="Parse(string)"/> but reports whether the input was malformed.
    /// The application service increments a counter on a parse failure so operators
    /// can chart schema-drift incidents without spamming logs.
    /// </summary>
    /// <param name="json">Raw JSON column value, or <c>null</c>.</param>
    /// <returns>
    /// A <see cref="ParseOutcome"/> carrying the parsed value (or the default) and a
    /// <c>Succeeded</c> flag.
    /// </returns>
    public static ParseOutcome TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ParseOutcome(UserLayoutPreferences.Default, Succeeded: true);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<UserLayoutPreferences>(json, Opts);
            return parsed is null
                ? new ParseOutcome(UserLayoutPreferences.Default, Succeeded: true)
                : new ParseOutcome(parsed, Succeeded: true);
        }
        catch (JsonException)
        {
            // Fail-open per the type remarks — malformed JSON must NOT block UI rendering.
            // The caller increments cnas.user_layout.parse_failure so operators chart drift.
            return new ParseOutcome(UserLayoutPreferences.Default, Succeeded: false);
        }
    }

    /// <summary>
    /// Serialises a <see cref="UserLayoutPreferences"/> instance to its canonical
    /// camelCase JSON form for persistence on the <c>UserProfile.LayoutPreferences</c>
    /// column.
    /// </summary>
    /// <param name="prefs">Preferences to serialise (must be non-null).</param>
    /// <returns>Compact UTF-16 JSON string.</returns>
    public static string Serialize(UserLayoutPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        return JsonSerializer.Serialize(prefs, Opts);
    }
}
