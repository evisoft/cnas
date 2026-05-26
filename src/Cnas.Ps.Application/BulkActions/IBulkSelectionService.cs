using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.BulkActions;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — server-side bulk-selection lifecycle service. The
/// service persists a registry + filter + include/exclude id triple as a durable handle
/// so an operator can run a registered bulk operation against the resolved set without
/// re-shipping the entire id list over the wire. The service does not execute the
/// operation itself — see <see cref="IBulkOperationRunner"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why server-side.</b> Today the registry/list views' "select all" only selects the
/// rows visible on the current page. A user filtering 12 000 rows clicking that
/// checkbox would otherwise have to paginate through every page to truly select the
/// set. Persisting the filter envelope server-side lets the bulk-run endpoint
/// re-resolve the live row set at execution time, sidestepping TOCTOU drift between
/// "selection rendered" and "operation submitted".
/// </para>
/// <para>
/// <b>Caller-decoded inputs.</b> The controller layer is responsible for decoding the
/// caller-supplied Sqid strings (<c>ExplicitIncludeIds</c> / <c>ExplicitExcludeIds</c>)
/// into raw <see cref="long"/> primary keys before invoking
/// <see cref="CreateAsync"/>. Malformed Sqids are rejected at the controller with
/// <see cref="ErrorCodes.InvalidId"/>; the service trusts the decoded ids it receives.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> Every output id on this interface is a Sqid-encoded string
/// per CLAUDE.md RULE 3. Raw <see cref="long"/> primary keys never appear on this
/// surface except on the internal-only <see cref="ResolveIdsAsync"/> helper, which is
/// invoked exclusively by <c>IBulkOperationRunner</c> in the same process.
/// </para>
/// </remarks>
public interface IBulkSelectionService
{
    /// <summary>
    /// Persists a new bulk selection owned by the caller. Re-resolves the filter
    /// envelope synchronously to cache the row count on the persisted row, then
    /// returns the Sqid handle. Subsequent <see cref="ResolveIdsAsync"/> calls
    /// re-evaluate the filter against the live DB so the operation runs against the
    /// current row set, not the snapshot from create time.
    /// </summary>
    /// <param name="registry">
    /// Stable registry code. Must be one of the canonical values in
    /// <see cref="BulkRegistries"/>; otherwise returns
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </param>
    /// <param name="filterJson">
    /// Opaque JSON filter envelope. Capped at
    /// <c>BulkSelectionOptions.MaxFilterJsonLength</c> bytes by the service-layer
    /// validator.
    /// </param>
    /// <param name="explicitInclude">
    /// Raw primary keys the caller hand-picked on top of the filter result. Already
    /// Sqid-decoded by the controller. May be null or empty.
    /// </param>
    /// <param name="explicitExclude">
    /// Raw primary keys the caller un-picked from the filter result. Already Sqid-
    /// decoded by the controller. May be null or empty. Exclude wins over include on
    /// a conflict.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the <see cref="BulkSelectionOutputDto"/> with the Sqid handle and
    /// the cached resolved count. Failures: <see cref="ErrorCodes.Unauthorized"/> when
    /// the caller has no resolved user id; <see cref="ErrorCodes.ValidationFailed"/>
    /// for unknown registries / oversized payloads / oversized id lists.
    /// </returns>
    Task<Result<BulkSelectionOutputDto>> CreateAsync(
        string registry,
        string filterJson,
        IReadOnlyList<long>? explicitInclude,
        IReadOnlyList<long>? explicitExclude,
        CancellationToken ct = default);

    /// <summary>
    /// Re-resolves the selection against the live DB and returns the materialised id
    /// list. Invoked by <see cref="IBulkOperationRunner"/> at run time — the
    /// indirection ensures the runner picks up rows added (via include) or removed
    /// (via exclude) between create and run. Internal-only helper; not exposed on
    /// the HTTP surface.
    /// </summary>
    /// <param name="bulkSelectionId">Internal primary key of the selection row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the materialised id list. Failures: <see cref="ErrorCodes.NotFound"/>
    /// when the row is missing or soft-deleted; <see cref="ErrorCodes.BulkSelectionExpired"/>
    /// when the row has expired.
    /// </returns>
    Task<Result<IReadOnlyList<long>>> ResolveIdsAsync(
        long bulkSelectionId,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches selection state by Sqid. The caller must be the owner — non-owners
    /// receive <see cref="ErrorCodes.Forbidden"/>; soft-deleted rows surface as
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the selection row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success the <see cref="BulkSelectionOutputDto"/>; otherwise a structured failure.</returns>
    Task<Result<BulkSelectionOutputDto>> GetAsync(string sqid, CancellationToken ct = default);
}
