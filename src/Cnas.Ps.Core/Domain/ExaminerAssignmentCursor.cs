namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0570 / TOR CF 08.02 — singleton-row checkpoint for the round-robin
/// examiner assignment service. Each call to
/// <c>IExaminerAssignmentService.AssignExaminerAsync</c> reads + increments
/// <see cref="NextIndex"/> under <c>SaveChanges</c> optimistic concurrency
/// so consecutive submissions fan out across the eligible examiner pool
/// uniformly (uniform spread per CF 08.02).
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton-via-known-key pattern.</b> Mirrors the
/// <see cref="SiemForwarderState"/> and <see cref="SecurityAlertEvaluatorState"/>
/// pattern: the table carries at most one row per environment, keyed by the
/// literal <c>"default"</c>. Migrations may seed that row idempotently with
/// <c>ON CONFLICT (Key) DO NOTHING</c>; the unique index on <see cref="Key"/>
/// protects against accidental second-row inserts. Holding the cursor in a row
/// (rather than in an in-memory job field) is required for crash-safety —
/// a process restart between two submissions must NOT cause the round-robin
/// to skip back to the start, otherwise the first examiner would receive every
/// post-restart cerere until the in-memory counter ticked past their index.
/// </para>
/// <para>
/// <b>Why not <see cref="IExternalId"/>.</b> The cursor is purely an internal
/// implementation detail of the assignment service — it never surfaces in any
/// output DTO, REST route, or webhook payload. Marking it as an external-id-bearing
/// entity would falsely imply its surrogate id is part of the public contract,
/// which it is not.
/// </para>
/// <para>
/// <b>Concurrency.</b> The <see cref="AuditableEntity.Xmin"/> column inherited
/// from the base type backs an optimistic-concurrency check. Two parallel
/// submissions that read the same <see cref="NextIndex"/> will collide on
/// <c>SaveChanges</c>; the round-robin service catches the
/// <c>DbUpdateConcurrencyException</c> and retries the read-modify-write a
/// bounded number of times. The Application layer documents the retry
/// envelope on the service interface XML doc.
/// </para>
/// </remarks>
public sealed class ExaminerAssignmentCursor : AuditableEntity
{
    /// <summary>
    /// Stable singleton-row key. The assignment service reads + writes
    /// exclusively the row whose <see cref="Key"/> equals <c>"default"</c>.
    /// Capped at 32 characters at the EF mapping layer to leave headroom for
    /// a future per-tenant variant (<c>"tenant-X"</c>, ...) without touching
    /// the schema again.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// Monotonically increasing counter consumed (mod eligible-examiner count)
    /// by the round-robin selection. The cursor is incremented AFTER each
    /// successful assignment so the next call routes to the next eligible
    /// examiner. Wraps naturally via the modulo operator at the call site.
    /// Seeded to <c>0</c> by the migration so the first assignment starts at
    /// the first eligible examiner in the canonical (Id-ascending) ordering.
    /// </summary>
    public long NextIndex { get; set; }
}
