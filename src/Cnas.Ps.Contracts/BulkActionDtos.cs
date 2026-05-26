namespace Cnas.Ps.Contracts;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — request body for <c>POST /api/bulk-actions/selections</c>.
/// Carries the registry, the opaque filter envelope, and the (optional) hand-curated
/// include / exclude id lists. The caller's identity (resolved server-side from the
/// authenticated principal) becomes the selection's owner — the input DTO deliberately
/// omits an owner field so a non-admin caller cannot forge a selection for someone
/// else (mass-assignment protection per CLAUDE.md §2.4 / §5.5).
/// </summary>
/// <param name="Registry">
/// Stable registry code (e.g. <c>Solicitant</c>, <c>Cerere</c>, <c>WorkflowTask</c>,
/// <c>Decision</c>). Values outside the <c>BulkRegistries</c> allow-list are rejected
/// with the stable error code <c>VALIDATION_FAILED</c>.
/// </param>
/// <param name="FilterJson">
/// Opaque JSON filter envelope — same shape the registry list endpoint accepts. Capped
/// at 8192 bytes by the service-layer validator.
/// </param>
/// <param name="ExplicitIncludeIds">
/// Sqid-encoded ids the caller hand-picks on top of the filter result. Each entry is
/// decoded at the controller boundary; malformed entries surface as the stable error
/// code <c>INVALID_ID</c>. May be null or empty.
/// </param>
/// <param name="ExplicitExcludeIds">
/// Sqid-encoded ids the caller has un-picked from the filter result. Subtracted from
/// the resolved set. Exclude wins over include on a conflict. May be null or empty.
/// </param>
public sealed record BulkSelectionCreateDto(
    string Registry,
    string FilterJson,
    IReadOnlyList<string>? ExplicitIncludeIds,
    IReadOnlyList<string>? ExplicitExcludeIds);

/// <summary>
/// R0166 — response body for selection create / read endpoints. Surfaces the Sqid id,
/// the registry, the cached row-count taken at create time (informational — the runner
/// re-resolves the filter at run time), the expiry instant, and the consumed flag.
/// </summary>
/// <param name="Id">Sqid-encoded id of the persisted selection (CLAUDE.md RULE 3).</param>
/// <param name="Registry">Stable registry code copied from the input.</param>
/// <param name="ResolvedCount">
/// Number of rows matching the filter (plus include, minus exclude) at the moment the
/// selection was created. Informational only — the runner re-resolves before
/// executing the operation so the live count is authoritative.
/// </param>
/// <param name="ExpiresAtUtc">
/// UTC instant after which the selection refuses to resolve. Default lifetime is
/// one hour (see <c>BulkSelectionOptions.SelectionLifetime</c>).
/// </param>
/// <param name="IsConsumed">
/// <c>true</c> once a <c>BulkOperationRun</c> has executed against the selection
/// (regardless of outcome). Single-use by design.
/// </param>
public sealed record BulkSelectionOutputDto(
    string Id,
    string Registry,
    int ResolvedCount,
    DateTime ExpiresAtUtc,
    bool IsConsumed);

/// <summary>
/// R0166 — request body for <c>POST /api/bulk-actions/runs</c>. Carries the Sqid of
/// the selection to consume, the stable operation code to apply, the operation-specific
/// parameters JSON (or null when the operation declares
/// <c>RequiresParameters = false</c>), and an optional idempotency key.
/// </summary>
/// <param name="BulkSelectionId">Sqid-encoded id of an existing, unconsumed, unexpired selection owned by the caller.</param>
/// <param name="OperationCode">
/// Stable operation code matching one of the registered <c>IBulkOperation.Code</c>
/// values (e.g. <c>WorkflowTask.Reassign</c>). Validated against the registry at run
/// time; unknown codes return the stable error code <c>BULK_OP_UNKNOWN</c>.
/// </param>
/// <param name="ParametersJson">
/// Operation-specific parameters. May be null when the operation declares
/// <c>RequiresParameters = false</c>; required (validated by the runner) when the
/// operation declares <c>RequiresParameters = true</c>.
/// </param>
/// <param name="IdempotencyKey">
/// Optional caller-supplied de-duplication key. When provided the runner returns the
/// prior outcome for a matching <c>(ActorUserId, OperationCode, IdempotencyKey)</c>
/// tuple instead of executing twice. ≤128 chars; ASCII letters/digits/dash/underscore
/// only.
/// </param>
public sealed record BulkOperationRunCreateDto(
    string BulkSelectionId,
    string OperationCode,
    string? ParametersJson,
    string? IdempotencyKey);

/// <summary>
/// R0166 — response body for run create / status endpoints. Surfaces the Sqid id of the
/// run, the stable operation code, the lifecycle status as a stable string (so the wire
/// contract survives reordering of the underlying enum), the row counters, the
/// timestamps, the per-row failure summary (when applicable), and the idempotency key
/// the caller supplied.
/// </summary>
/// <param name="Id">Sqid-encoded id of the run (CLAUDE.md RULE 3).</param>
/// <param name="OperationCode">Stable operation code copied from the input.</param>
/// <param name="Status">
/// Stable string form of <c>BulkOperationStatus</c>
/// (<c>Pending</c>|<c>Running</c>|<c>Completed</c>|<c>PartiallyFailed</c>|<c>Failed</c>|<c>Cancelled</c>).
/// </param>
/// <param name="TotalRows">Total number of rows the runner visited.</param>
/// <param name="SucceededRows">Number of rows whose operation returned a successful outcome.</param>
/// <param name="FailedRows">Number of rows whose operation returned a failed outcome.</param>
/// <param name="StartedUtc">UTC instant the runner started the loop.</param>
/// <param name="CompletedUtc">UTC instant the runner finished — null while <c>Status</c> is <c>Running</c>.</param>
/// <param name="FailureSummaryJson">
/// JSON array of <c>{ "rowId": "&lt;sqid&gt;", "errorCode": "...", "message": "..." }</c>
/// objects, capped at 100 entries. Null when no failures occurred.
/// </param>
/// <param name="IdempotencyKey">The idempotency key the caller supplied, echoed back; null when none was supplied.</param>
public sealed record BulkOperationRunOutputDto(
    string Id,
    string OperationCode,
    string Status,
    int TotalRows,
    int SucceededRows,
    int FailedRows,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    string? FailureSummaryJson,
    string? IdempotencyKey);

/// <summary>
/// R0166 — descriptor for a single registered bulk operation. Returned by
/// <c>GET /api/bulk-actions/operations</c> so a UI can render a per-registry catalog
/// without hard-coding the set.
/// </summary>
/// <param name="Code">Stable operation code (e.g. <c>WorkflowTask.Reassign</c>).</param>
/// <param name="Registry">
/// Registry the operation targets (must match the selection's registry — the runner
/// rejects a mismatch with the stable error code <c>VALIDATION_FAILED</c>).
/// </param>
/// <param name="RequiredPermission">
/// Permission code the caller must hold. Verified by <c>BulkActionsController</c> via
/// the standard authorization service before invoking the runner.
/// </param>
/// <param name="MaxRowsPerRun">
/// Per-operation quota cap. The runner refuses to execute when the resolved row set
/// exceeds this number with the stable error code <c>QUOTA_EXCEEDED</c>.
/// </param>
/// <param name="RequiresParameters">
/// <c>true</c> when the operation needs <c>ParametersJson</c> on the run request;
/// <c>false</c> when the operation is parameterless.
/// </param>
public sealed record BulkOperationDescriptorDto(
    string Code,
    string Registry,
    string RequiredPermission,
    int MaxRowsPerRun,
    bool RequiresParameters);
