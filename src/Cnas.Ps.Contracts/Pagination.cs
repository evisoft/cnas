namespace Cnas.Ps.Contracts;

/// <summary>
/// Standard pagination query input. Used at every list endpoint per TOR UI 014 / CF 01.06
/// (results must be paged to avoid overloading the browser).
/// </summary>
/// <param name="Page">1-based page number. Default 1.</param>
/// <param name="PageSize">
/// Items per page. Bounded to [1, 200] both by the <c>[Range]</c> annotation below (so
/// MVC model-binding / FluentValidation can reject out-of-range values at the boundary)
/// and clamped defensively in the service layer.
/// </param>
public sealed record PageRequest(
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
    int Page = 1,
    [System.ComponentModel.DataAnnotations.Range(1, 200)]
    int PageSize = 20);

/// <summary>
/// Standard pagination output. All Ids inside <typeparamref name="TItem"/> are Sqid-encoded
/// strings per CLAUDE.md RULE 3.
/// </summary>
public sealed record PagedResult<TItem>(IReadOnlyList<TItem> Items, int Page, int PageSize, long TotalCount)
{
    /// <summary>True when there is at least one more page after the current one.</summary>
    public bool HasNext => (long)Page * PageSize < TotalCount;
}
