using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0165 / CF 03.06 — saved-search lifecycle service. Owners can persist a saved query
/// (registry + filter + friendly name) and replay it later; flipping the shared flag
/// publishes the row to every authenticated CNAS staff member. Implementations enforce
/// the ownership / sharing access rules, the per-owner row cap, and the natural-key
/// uniqueness contract on <c>(OwnerUserId, Registry, Name)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Access rules.</b> Owners may read, update, and delete their own rows regardless of
/// the <see cref="SavedSearchItem.IsShared"/> flag. Non-owners receive READ access
/// exclusively to rows whose <see cref="SavedSearchItem.IsShared"/> is <c>true</c>; they
/// cannot mutate shared rows in any way. Sharing is unilateral (no per-recipient ACL) —
/// per-team scoping is deferred to R0056 ABAC.
/// </para>
/// <para>
/// <b>Limits.</b> Implementations enforce three caps documented on the corresponding
/// failure codes: <c>Name</c> length (1-128 chars), <c>FilterJson</c> length (1-8192
/// bytes), and per-owner row count (default 50). All three surface as
/// <see cref="ErrorCodes.ValidationFailed"/> EXCEPT the per-owner cap which has its own
/// stable code <see cref="ErrorCodes.SavedSearchLimitReached"/> so a UI can render a
/// specific prompt.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> Every method that emits or consumes an id uses the Sqid string
/// form per CLAUDE.md RULE 3. Raw <see cref="long"/> primary keys never appear on this
/// surface.
/// </para>
/// </remarks>
public interface ISavedSearchService
{
    /// <summary>
    /// Lists the caller's OWN saved searches for the specified registry. As of R0524
    /// this method is the "list mine" projection — only rows whose
    /// <c>OwnerUserId</c> matches the caller. The broader union (owned + Shared +
    /// Group-where-member) lives on
    /// <see cref="ListAccessibleAsync(string, CancellationToken)"/>.
    /// </summary>
    /// <param name="registry">Registry code (e.g. <c>Contributors</c>); required.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success, the caller-owned rows as <see cref="SavedSearchItem"/> records sorted by name.
    /// On <see cref="ErrorCodes.ValidationFailed"/> the registry was missing or whitespace.
    /// </returns>
    Task<Result<IReadOnlyList<SavedSearchItem>>> ListAsync(string registry, CancellationToken ct = default);

    /// <summary>
    /// R0524 / TOR CF 03.06 — lists every saved search the caller can READ on the named
    /// registry. The result is the union of:
    /// <list type="bullet">
    ///   <item><description>Caller-owned rows (any <c>SharingScope</c>).</description></item>
    ///   <item><description>Rows owned by anyone where
    ///     <c>SharingScope == Shared</c>.</description></item>
    ///   <item><description>Rows owned by anyone where <c>SharingScope == Group</c>
    ///     and the row's <c>SharedWithGroupCode</c> is in the caller's
    ///     <c>UserProfile.Groups</c> list.</description></item>
    /// </list>
    /// Sorted ascending by <see cref="SavedSearchItem.Name"/>. Soft-deleted rows are
    /// excluded.
    /// </summary>
    /// <param name="registry">Registry code (e.g. <c>Contributors</c>); required.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The accessible rows for the caller in name-ascending order. Empty when no rows match.</returns>
    Task<IReadOnlyList<SavedSearchItem>> ListAccessibleAsync(string registry, CancellationToken ct = default);

    /// <summary>
    /// Fetches a single saved search by Sqid. The caller must be either the owner or
    /// reading a row published with <c>IsShared = true</c>; otherwise the service
    /// returns <see cref="ErrorCodes.Forbidden"/>. Soft-deleted rows surface as
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success, the resolved <see cref="SavedSearchItem"/>; otherwise a structured failure.</returns>
    Task<Result<SavedSearchItem>> GetAsync(string sqid, CancellationToken ct = default);

    /// <summary>
    /// Persists a new saved search owned by the caller. Idempotent on the
    /// <c>(OwnerUserId, Registry, Name)</c> natural key: if the caller already has a row
    /// with the supplied name on the supplied registry, the existing row's Sqid is
    /// returned and NO write occurs. To overwrite fields use
    /// <see cref="UpdateAsync(string, SavedSearchUpdateInput, CancellationToken)"/>
    /// instead.
    /// </summary>
    /// <param name="input">Create payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the Sqid id of the persisted (or pre-existing) row. Failure codes:
    /// <see cref="ErrorCodes.Unauthorized"/> when the caller has no resolved user id;
    /// <see cref="ErrorCodes.ValidationFailed"/> for malformed inputs;
    /// <see cref="ErrorCodes.SavedSearchLimitReached"/> when the per-owner cap is full.
    /// </returns>
    Task<Result<string>> CreateAsync(SavedSearchCreateInput input, CancellationToken ct = default);

    /// <summary>
    /// Updates the three mutable fields (name, filter, shared flag) of a saved search
    /// owned by the caller. Non-owners receive <see cref="ErrorCodes.Forbidden"/>;
    /// missing rows return <see cref="ErrorCodes.NotFound"/>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the row.</param>
    /// <param name="input">Update payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Success on apply; otherwise <see cref="ErrorCodes.NotFound"/>,
    /// <see cref="ErrorCodes.Forbidden"/>, or <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result> UpdateAsync(string sqid, SavedSearchUpdateInput input, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a saved search owned by the caller (flips <c>IsActive = false</c>);
    /// the row remains queryable for audit forensics. Non-owners receive
    /// <see cref="ErrorCodes.Forbidden"/>; missing rows return
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success on apply; otherwise <see cref="ErrorCodes.NotFound"/> or <see cref="ErrorCodes.Forbidden"/>.</returns>
    Task<Result> DeleteAsync(string sqid, CancellationToken ct = default);

    /// <summary>
    /// R0524 / TOR CF 03.06 — flips the row's sharing scope. The caller MUST be the
    /// owner; non-owners receive <see cref="ErrorCodes.Forbidden"/>. On success, the
    /// service updates both the new <c>SharingScope</c> + <c>SharedWithGroupCode</c>
    /// columns AND the legacy <c>IsShared</c> mirror flag (true iff scope = Shared),
    /// then emits a <c>SAVED_SEARCH.SHARED</c> audit row at
    /// <see cref="Cnas.Ps.Core.Domain.AuditSeverity.Notice"/>. The returned DTO reflects the post-update
    /// state so the caller does not need a round-trip GET.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the row.</param>
    /// <param name="input">Share payload (scope + optional group code).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the updated <see cref="SavedSearchItem"/>. Failure codes:
    /// <see cref="ErrorCodes.Unauthorized"/> when the caller has no resolved user id;
    /// <see cref="ErrorCodes.InvalidSqid"/> for malformed Sqids;
    /// <see cref="ErrorCodes.NotFound"/> when the row does not exist or is soft-deleted;
    /// <see cref="ErrorCodes.Forbidden"/> when the caller is not the owner;
    /// <see cref="ErrorCodes.ValidationFailed"/> for unparseable scopes or
    /// inconsistent scope/group-code pairs (defence-in-depth — the API-level
    /// validator should catch these first).
    /// </returns>
    Task<Result<SavedSearchItem>> ShareAsync(string sqid, SavedSearchShareInput input, CancellationToken ct = default);
}
