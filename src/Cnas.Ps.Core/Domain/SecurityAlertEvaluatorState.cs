namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Persistent checkpoint for the security-alert evaluator background job
/// (R0189 / SEC 048). The evaluator polls <see cref="AuditLog"/> rows whose
/// <see cref="AuditableEntity.Id"/> is greater than
/// <see cref="LastEvaluatedAuditId"/>, scores them against the active
/// <see cref="SecurityAlertRule"/> set, and advances the checkpoint so the next
/// iteration resumes where the previous one stopped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton-via-known-key pattern.</b> Identical to
/// <see cref="SiemForwarderState"/> (R0190) — the table carries a single row keyed by
/// the literal <c>"default"</c>. The migration seeds that row idempotently with
/// <c>ON CONFLICT ("Key") DO NOTHING</c>; the unique index on <see cref="Key"/>
/// protects against accidental second-row inserts. Holding the checkpoint in a row
/// (rather than in an in-memory job field) guarantees crash-safety: a process restart
/// between two evaluator cycles must NOT cause already-scored audit rows to be
/// scored again, otherwise rules would re-fire and operators would receive duplicate
/// notifications.
/// </para>
/// <para>
/// <b>Why not <see cref="IExternalId"/>.</b> The checkpoint is purely an internal
/// implementation detail of the evaluator — it never surfaces in any output DTO, REST
/// route, or webhook payload. Marking it as an external-id-bearing entity would
/// misimply its surrogate id is part of the public contract, which it is not.
/// </para>
/// <para>
/// <b>Failure handling contract.</b> The evaluator advances
/// <see cref="LastEvaluatedAuditId"/> to the highest id it scanned on every successful
/// pass, regardless of whether any rule fired. A scan that examined rows but matched
/// nothing still advances the checkpoint — the rows have been "considered" and need
/// not be re-scanned. If the evaluator crashes mid-pass (between the rule-fire
/// SaveChanges and the checkpoint-update SaveChanges) the same per-iteration
/// transaction is rolled back together; the checkpoint stays at its previous value
/// and the next pass re-scores the same window. This is at-least-once evaluation
/// with the cooldown semantics on <see cref="SecurityAlertRule.LastFiredAtUtc"/>
/// preventing duplicate alerts.
/// </para>
/// <para>
/// <b>Soft-delete.</b> Inherits the standard <see cref="AuditableEntity.IsActive"/>
/// soft-delete marker. Operators MAY soft-delete the row to disable evaluation
/// without touching configuration — the job's query is gated on
/// <c>IsActive == true</c>, so an inactive row makes the next iteration log a
/// warning and return. Re-activating restores evaluation from the stored checkpoint.
/// </para>
/// </remarks>
public sealed class SecurityAlertEvaluatorState : AuditableEntity
{
    /// <summary>
    /// Stable singleton-row key. The evaluator reads and writes exclusively the row
    /// whose <see cref="Key"/> equals <c>"default"</c>; the migration seeds that row
    /// and the unique index on this column prevents accidental duplicates. Capped at
    /// 32 characters at the EF mapping layer to leave headroom for a future per-tenant
    /// variant (<c>"tenant-X"</c>, …) without touching the schema again.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// Highest <see cref="AuditLog"/> primary key that has been scored against the
    /// active rule set. The evaluator's next iteration queries
    /// <c>AuditLogs.Where(a =&gt; a.Id &gt; LastEvaluatedAuditId)</c> — strictly
    /// greater-than so an audit row is never scored twice. Seeded to <c>0</c> by the
    /// migration so the first iteration starts from the bottom of the table.
    /// </summary>
    public long LastEvaluatedAuditId { get; set; }
}
