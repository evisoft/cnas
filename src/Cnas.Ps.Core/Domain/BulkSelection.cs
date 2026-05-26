namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — server-side persistence of a cross-page bulk-action
/// selection. A user filtering 12 000 rows over an arbitrary registry list view picks a
/// registry, supplies the same opaque filter envelope the registry list endpoint
/// accepts, and optionally hand-curates the selection by including / excluding specific
/// rows. The row stored here is the durable handle: an operator can later run a
/// registered bulk operation against the resolved set without re-shipping the entire
/// id list over the wire (TOCTOU drift is handled by re-resolving the filter at
/// operation time).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> <see cref="OwnerUserId"/> is the internal primary key of the
/// <c>UserProfile</c> that created the selection. Only the owner may consume the
/// selection — a different caller running an operation against another user's
/// selection receives <see cref="Cnas.Ps.Core.Common.ErrorCodes.Forbidden"/>.
/// </para>
/// <para>
/// <b>Filter opacity.</b> <see cref="FilterJson"/> is treated as an opaque blob by
/// the selection service. A per-registry <c>IBulkSelectionFilterResolver</c>
/// implementation interprets the JSON when the selection is created (to compute the
/// cached <see cref="ResolvedCount"/>) and again at operation time (to materialise the
/// live id list — same query, against possibly-mutated rows). The selection record
/// itself never tries to validate the JSON shape; that responsibility lives with the
/// per-registry resolver.
/// </para>
/// <para>
/// <b>Explicit include / exclude semantics.</b>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="ExplicitIncludeIds"/> is unioned with the filter result. The
///       typical use case is a user un-filtering a row but still wanting it included
///       — they hand-tick the row in the UI on top of the filter set.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="ExplicitExcludeIds"/> is subtracted from the filter result. The
///       typical use case is "select all 12 000 rows then un-tick the three the user
///       knows are anomalies". Exclude wins over include on a conflict (if a row
///       appears in BOTH lists the row is excluded).
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Expiry.</b> <see cref="ExpiresAtUtc"/> is stamped at create-time to
/// <c>now + BulkSelectionOptions.SelectionLifetime</c> (default 1h). After expiry the
/// selection refuses to resolve and the operation runner refuses to consume it. A
/// background cleanup job hard-deletes selection rows past a wider 7-day grace window
/// so the table doesn't grow unboundedly.
/// </para>
/// <para>
/// <b>Single-use.</b> <see cref="IsConsumed"/> flips to <c>true</c> as soon as a
/// <c>BulkOperationRun</c> completes against the selection (regardless of outcome —
/// even a fully-failed run consumes the selection). A second attempt at the same
/// selection requires a fresh create call; this prevents an operator from
/// accidentally re-running a destructive operation against a stale row set after
/// a colleague has fixed the original anomalies.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> The numeric <see cref="AuditableEntity.Id"/> never leaves
/// the system. <see cref="IExternalId"/> is applied because the
/// <c>BulkSelectionOutputDto</c> surfaces the Sqid form as the public identifier —
/// CLAUDE.md RULE 3 / ARH 027.
/// </para>
/// </remarks>
public sealed class BulkSelection : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable registry code identifying which list view the selection targets
    /// (e.g. <c>Solicitant</c>, <c>Cerere</c>, <c>WorkflowTask</c>, <c>Decision</c>).
    /// The accepted set lives in <c>BulkRegistries</c>; values outside that set are
    /// rejected at the service boundary with
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.ValidationFailed"/>. Capped at
    /// <c>varchar(32)</c> by the EF mapping — registry codes are short PascalCase
    /// identifiers.
    /// </summary>
    public required string Registry { get; set; }

    /// <summary>
    /// Internal <c>UserProfile.Id</c> of the user who created the selection. The
    /// operation runner refuses to consume a selection owned by a different caller
    /// with <see cref="Cnas.Ps.Core.Common.ErrorCodes.Forbidden"/>.
    /// </summary>
    public long OwnerUserId { get; set; }

    /// <summary>
    /// Opaque JSON filter envelope describing the registry-level filter — the same
    /// shape the registry's list endpoint accepts. Re-evaluated against the live DB
    /// each time the selection is resolved so the operation runs against the
    /// current row set, not the snapshot taken at create time. Capped at
    /// <c>BulkSelectionOptions.MaxFilterJsonLength</c> bytes (default 8192) by the
    /// service-layer validator; the DB column is <c>text</c> with no hard cap so the
    /// service can be relaxed without a migration.
    /// </summary>
    public required string FilterJson { get; set; }

    /// <summary>
    /// Primary keys the caller explicitly added to the selection on top of the
    /// filter result. Stored as an EF-mapped <c>List&lt;long&gt;</c> JSON array
    /// (Postgres jsonb). When the selection is resolved the include list is unioned
    /// with the filter result; if a row appears in BOTH the include and the exclude
    /// list, the exclude wins. May be empty.
    /// </summary>
    public List<long> ExplicitIncludeIds { get; set; } = new();

    /// <summary>
    /// Primary keys the caller explicitly removed from the selection after the
    /// filter result was rendered. Subtracted from the filter result at resolve
    /// time. May be empty.
    /// </summary>
    public List<long> ExplicitExcludeIds { get; set; } = new();

    /// <summary>
    /// Row count captured at create time — informational only. The runner re-resolves
    /// the live row set before executing so a TOCTOU drift between create and run
    /// (rows added / removed by other users) is handled correctly without relying on
    /// this cached value.
    /// </summary>
    public int ResolvedCount { get; set; }

    /// <summary>
    /// UTC instant after which the selection refuses to resolve and the operation
    /// runner refuses to consume it. Stamped at create-time to
    /// <c>now + BulkSelectionOptions.SelectionLifetime</c>. Default lifetime is one
    /// hour — enough for a user to review the resolved count, choose an operation,
    /// and submit a run, but short enough that abandoned selections don't hold
    /// stale row sets indefinitely.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// <c>true</c> after a <see cref="BulkOperationRun"/> has executed against this
    /// selection (regardless of outcome). A second consume attempt receives
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.Conflict"/> — running a destructive
    /// operation against a stale selection twice is forbidden by design.
    /// </summary>
    public bool IsConsumed { get; set; }
}
