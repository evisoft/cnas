namespace Cnas.Ps.Application.BulkActions;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — tunable parameters for the bulk-selection service.
/// Bound from the <c>Cnas:BulkActions</c> configuration section so operators can adjust
/// the selection lifetime, the cleanup grace window, and the filter-payload cap without
/// redeploying.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default rationale.</b>
/// <list type="bullet">
///   <item>
///     <description><see cref="SelectionLifetime"/> = 1h — long enough for a user to review
///     the resolved count and submit a run, short enough that abandoned selections don't
///     keep a stale row set durably reachable. The lifetime is the
///     <c>BulkSelectionLifetime</c> constant referenced in the cardinal-rule docs.</description>
///   </item>
///   <item>
///     <description><see cref="CleanupGraceDays"/> = 7 — the wider grace window after
///     which the background cleanup job hard-deletes expired selections. Keeps the row
///     around for forensic / debug purposes well after the operational lifetime ends.</description>
///   </item>
///   <item>
///     <description><see cref="MaxFilterJsonLength"/> = 8192 — matches the
///     <c>SavedSearchOptions.MaxFilterJsonLength</c> cap. A payload larger than this is
///     almost certainly a bug in the filter-envelope serialiser.</description>
///   </item>
///   <item>
///     <description><see cref="MaxExplicitIdsPerList"/> = 5 000 — bounds the size of the
///     include / exclude id arrays so a single create request cannot ship a
///     megabyte-scale id list. The selection itself can resolve to many more rows via
///     the filter — the cap only constrains the hand-curated overlay.</description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class BulkSelectionOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:BulkActions";

    /// <summary>
    /// Hard-coded base lifetime referenced by the R0166 design notes. Operators may
    /// override at runtime via <see cref="SelectionLifetime"/>, but the documented
    /// contract is "one hour" — anything tighter risks UX whiplash, anything looser
    /// risks running destructive operations against drifting row sets.
    /// </summary>
    public static readonly TimeSpan BulkSelectionLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Lifetime of a bulk selection before it refuses to resolve. Default
    /// <see cref="BulkSelectionLifetime"/> (1h).
    /// </summary>
    public TimeSpan SelectionLifetime { get; set; } = BulkSelectionLifetime;

    /// <summary>
    /// Grace period after <see cref="SelectionLifetime"/> elapses before the cleanup
    /// job hard-deletes the row. Keeps the selection joinable from audit rows for a
    /// week so an investigator can still inspect the filter envelope. Default 7 days.
    /// </summary>
    public int CleanupGraceDays { get; set; } = 7;

    /// <summary>
    /// Hard cap on the byte length of <c>FilterJson</c>. Returns
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.ValidationFailed"/> on overrun.
    /// Default 8192.
    /// </summary>
    public int MaxFilterJsonLength { get; set; } = 8192;

    /// <summary>
    /// Hard cap on the size of <c>ExplicitIncludeIds</c> / <c>ExplicitExcludeIds</c>.
    /// Default 5 000 — enough to express any reasonable hand-curation without letting
    /// a single create request inflate to megabytes.
    /// </summary>
    public int MaxExplicitIdsPerList { get; set; } = 5_000;
}

/// <summary>
/// R0166 — global default cap on rows-per-run shared by every operation. Per-operation
/// caps may override via <c>IBulkOperation.MaxRowsPerRun</c>; this value is the floor
/// the runner consults when an operation declares the special sentinel <c>0</c>.
/// </summary>
public sealed class BulkOperationOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:BulkActions:Run";

    /// <summary>
    /// Global default for the rows-per-run cap. Default 5 000 — protects against
    /// operator typos that would otherwise issue a run against tens of thousands of
    /// rows.
    /// </summary>
    public int MaxRowsPerRun { get; set; } = 5_000;

    /// <summary>
    /// Maximum number of per-row failure entries persisted in
    /// <c>BulkOperationRun.FailureSummaryJson</c>. Default 100 — the audit trail
    /// carries the full detail; the run row only needs enough to render a meaningful
    /// UI summary.
    /// </summary>
    public int MaxFailureSummaryEntries { get; set; } = 100;
}
