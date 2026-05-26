namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — records one execution of a registered bulk operation
/// against a <see cref="BulkSelection"/>. One row per <c>POST /api/bulk-actions/runs</c>
/// regardless of outcome (Completed, PartiallyFailed, Failed, Cancelled). The row is the
/// durable audit anchor: a caller polling for status reads from this table and a future
/// investigator joins from the audit-log row's <c>TargetEntityId</c> back to this row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Created in <see cref="BulkOperationStatus.Running"/> with
/// <see cref="TotalRows"/> set and the counters zero. Each row processed bumps either
/// <see cref="SucceededRows"/> or <see cref="FailedRows"/>; failures are captured in
/// <see cref="FailureSummaryJson"/> up to a cap of 100 entries (the cap keeps a
/// large-failure run from inflating row size — a future investigator goes to the audit
/// trail for the per-row detail). When the loop completes the row's
/// <see cref="Status"/> is set to one of the terminal values
/// (<see cref="BulkOperationStatus.Completed"/>,
/// <see cref="BulkOperationStatus.PartiallyFailed"/>,
/// <see cref="BulkOperationStatus.Failed"/>) and <see cref="CompletedUtc"/> is
/// stamped.
/// </para>
/// <para>
/// <b>Idempotency.</b> When the caller supplies <see cref="IdempotencyKey"/> the
/// runner looks up an existing row keyed by <c>(ActorUserId, OperationCode,
/// IdempotencyKey)</c> and short-circuits with the prior outcome instead of running
/// the operation twice. A null key disables the de-duplication. The DB enforces the
/// triple's uniqueness via a partial unique index where <c>IdempotencyKey IS NOT
/// NULL</c> so a racing concurrent submit cannot squeeze a second row through.
/// </para>
/// <para>
/// <b>Failure summary shape.</b> JSON array of
/// <c>{ "rowId": "&lt;sqid&gt;", "errorCode": "...", "message": "..." }</c>
/// objects, capped at 100 entries. The <c>rowId</c> is Sqid-encoded so the payload
/// can be safely rendered in the UI without leaking raw primary keys (CLAUDE.md
/// RULE 3). Beyond 100 the loop stops appending — operators see the first 100 in
/// the API response and the rest in the audit log.
/// </para>
/// <para>
/// <b>Audit coupling.</b> The runner emits a <c>BULK.{OperationCode}.STARTED</c>
/// row at the start of the run and a <c>BULK.{OperationCode}.COMPLETED</c> row when
/// the loop finishes. Both rows carry severity <c>Critical</c> because bulk
/// operations are inherently high-blast-radius admin actions that MUST land in the
/// MLog mirror per TOR SEC 056. <c>TargetEntityId</c> on both audit rows is the
/// <see cref="AuditableEntity.Id"/> of this run.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> The numeric <see cref="AuditableEntity.Id"/> never leaves
/// the system. <see cref="IExternalId"/> is applied because the
/// <c>BulkOperationRunOutputDto</c> surfaces the Sqid form as the public
/// identifier — CLAUDE.md RULE 3 / ARH 027.
/// </para>
/// </remarks>
public sealed class BulkOperationRun : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to <see cref="BulkSelection"/>. Read at run start to resolve the live id
    /// list; the selection is marked <c>IsConsumed = true</c> after the run
    /// completes regardless of outcome.
    /// </summary>
    public long BulkSelectionId { get; set; }

    /// <summary>
    /// Stable operation code matching <c>IBulkOperation.Code</c> (e.g.
    /// <c>WorkflowTask.Reassign</c>). Capped at <c>varchar(64)</c> by the EF mapping
    /// — operation codes are short PascalCase dotted identifiers.
    /// </summary>
    public required string OperationCode { get; set; }

    /// <summary>
    /// Internal <c>UserProfile.Id</c> of the caller that submitted the run. Always
    /// the same user as the selection's owner because the runner refuses
    /// cross-owner consumption — captured here so the idempotency triple
    /// <c>(ActorUserId, OperationCode, IdempotencyKey)</c> is self-contained on the
    /// run row.
    /// </summary>
    public long ActorUserId { get; set; }

    /// <summary>
    /// Terminal-or-in-progress status of the run. Starts at
    /// <see cref="BulkOperationStatus.Running"/>; one of the four terminal values
    /// (<see cref="BulkOperationStatus.Completed"/>,
    /// <see cref="BulkOperationStatus.PartiallyFailed"/>,
    /// <see cref="BulkOperationStatus.Failed"/>,
    /// <see cref="BulkOperationStatus.Cancelled"/>) is stamped when the loop
    /// finishes.
    /// </summary>
    public BulkOperationStatus Status { get; set; } = BulkOperationStatus.Pending;

    /// <summary>Total number of rows the runner will visit. Captured before the loop starts.</summary>
    public int TotalRows { get; set; }

    /// <summary>Number of rows whose operation returned a successful <c>BulkRowOutcome</c>.</summary>
    public int SucceededRows { get; set; }

    /// <summary>Number of rows whose operation returned a failed <c>BulkRowOutcome</c>.</summary>
    public int FailedRows { get; set; }

    /// <summary>UTC instant the runner started the loop.</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>UTC instant the runner finished — null while <see cref="Status"/> is <see cref="BulkOperationStatus.Running"/>.</summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>
    /// Operation-specific parameters supplied by the caller (e.g. the
    /// <c>{"newAssigneeSqid":"…"}</c> body for <c>WorkflowTask.Reassign</c>).
    /// Stored verbatim; the operation implementation interprets the shape. May be
    /// null when the operation declares <c>RequiresParameters = false</c>.
    /// </summary>
    public string? ParametersJson { get; set; }

    /// <summary>
    /// Optional caller-supplied idempotency key. When provided the runner returns
    /// the prior outcome for a matching <c>(ActorUserId, OperationCode,
    /// IdempotencyKey)</c> tuple instead of executing the operation again. ≤128
    /// ASCII characters; the validator restricts to letters/digits/dash/underscore.
    /// A partial unique index (where <see cref="IdempotencyKey"/> IS NOT NULL)
    /// guards the triple at the DB level.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// JSON array of per-row failure details — <c>[{ "rowId": "&lt;sqid&gt;",
    /// "errorCode": "...", "message": "..." }, ...]</c>. Capped at 100 entries;
    /// beyond the cap the runner stops appending and operators see the per-row
    /// detail in the audit trail. Null when no failures occurred.
    /// </summary>
    public string? FailureSummaryJson { get; set; }
}
