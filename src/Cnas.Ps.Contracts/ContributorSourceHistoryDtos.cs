namespace Cnas.Ps.Contracts;

/// <summary>
/// R0302 / TOR §2.1 — outbound projection of one
/// <c>ContributorSourceChangeHistory</c> row. All ids are Sqid-encoded per
/// CLAUDE.md RULE 3.
/// </summary>
/// <param name="Id">Sqid-encoded id of the history row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the parent contributor.</param>
/// <param name="OldSourceSystem">
/// Source-system attribution before the change. <c>null</c> on the FIRST row
/// (initial registration).
/// </param>
/// <param name="NewSourceSystem">Source-system attribution after the change.</param>
/// <param name="ChangedAtUtc">UTC instant the change occurred.</param>
/// <param name="ChangedByUserSqid">
/// Sqid-encoded id of the operator who recorded the change, or <c>null</c> for
/// system writers (background reconciliation jobs).
/// </param>
/// <param name="Reason">
/// Free-form operator-supplied justification (≤ 500 chars), or <c>null</c> when
/// no reason was provided.
/// </param>
public sealed record ContributorSourceChangeHistoryDto(
    string Id,
    string ContributorSqid,
    string? OldSourceSystem,
    string NewSourceSystem,
    DateTime ChangedAtUtc,
    string? ChangedByUserSqid,
    string? Reason);

/// <summary>
/// R0302 — paged result wrapper for a contributor's source-change history listing.
/// </summary>
/// <param name="Items">Page of history rows ordered <c>ChangedAtUtc DESC</c>.</param>
/// <param name="TotalCount">Total number of rows matching the filter.</param>
/// <param name="Skip">Echoed back the request's <c>skip</c> offset.</param>
/// <param name="Take">Echoed back the request's <c>take</c> page size.</param>
public sealed record ContributorSourceChangeHistoryPageDto(
    IReadOnlyList<ContributorSourceChangeHistoryDto> Items,
    long TotalCount,
    int Skip,
    int Take);
