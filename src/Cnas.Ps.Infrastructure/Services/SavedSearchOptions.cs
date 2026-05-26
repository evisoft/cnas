namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0165 / CF 03.06 — tunable parameters for the saved-search service. Bound from the
/// <c>Cnas:SavedSearch</c> configuration section so operators can adjust the per-owner
/// cap and the filter-payload budget without redeploying.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default rationale.</b>
/// <list type="bullet">
///   <item>
///     <description><see cref="MaxPerOwner"/> = 50 — a generous cap that satisfies normal
///     usage (operators report curating ≤10 saved queries per registry in practice) while
///     guarding against runaway accumulation that would degrade the per-owner list query.</description>
///   </item>
///   <item>
///     <description><see cref="MaxFilterJsonLength"/> = 8192 — a payload that's larger
///     than this almost certainly indicates either a bug in the QBE serializer or an
///     attempt to abuse the saved-search store as freeform user storage. The cap protects
///     the audit pipeline's enqueue budget (we don't record FilterJson in audit, but the
///     row still travels through service-layer logs at TRACE).</description>
///   </item>
///   <item>
///     <description><see cref="MaxNameLength"/> = 128 — the EF column cap. Documented here
///     so a future relaxation of the column would surface as a single test-and-config
///     change rather than a hunt through three layers.</description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class SavedSearchOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:SavedSearch";

    /// <summary>
    /// Maximum number of active saved searches a single owner may hold. The service
    /// returns <see cref="Cnas.Ps.Core.Common.ErrorCodes.SavedSearchLimitReached"/> when
    /// the count is at or above this cap on create. Soft-deleted rows do NOT count.
    /// Default 50.
    /// </summary>
    public int MaxPerOwner { get; set; } = 50;

    /// <summary>
    /// Hard cap on the byte length of <c>FilterJson</c>. Returns
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.ValidationFailed"/> on overrun. Default
    /// 8192.
    /// </summary>
    public int MaxFilterJsonLength { get; set; } = 8192;

    /// <summary>
    /// Hard cap on the character length of <c>Name</c>. Mirrors the EF column cap so the
    /// service can produce a clean <c>ValidationFailed</c> result rather than a database
    /// <c>DbUpdateException</c>. Default 128.
    /// </summary>
    public int MaxNameLength { get; set; } = 128;
}
