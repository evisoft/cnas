namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0103 / TOR CF 14.02 — durable record of one inbound integration event the
/// CNAS PS subsystem has processed (or attempted to process). The
/// <see cref="MessageId"/> column carries the CloudEvents v1.0
/// <c>id</c> attribute of the inbound envelope and is enforced UNIQUE — the
/// row therefore acts both as the dedup ledger ("have we seen this MessageId
/// before?") and as the forensic trail ("what was the outcome the first time
/// we processed it?").
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency contract.</b> The integration-event dispatcher
/// (<c>LoggingCloudEventHandler</c> et al.) MUST call
/// <c>IIntegrationEventDeduper.TryClaimAsync</c> as its very first step on
/// every inbound envelope. The deduper inserts a row with
/// <see cref="Outcome"/> = <see cref="ProcessedEventOutcome.Accepted"/>; if a
/// row with the same <see cref="MessageId"/> already exists the deduper
/// returns <c>AlreadyProcessed=true</c> and the handler short-circuits without
/// invoking the downstream chain. This is the canonical "exactly-once at the
/// boundary, at-least-once in transit" pattern.
/// </para>
/// <para>
/// <b>Atomic insert.</b> The implementation relies on the UNIQUE constraint
/// on <see cref="MessageId"/> for race-freedom: two concurrent processors that
/// both observe a missing row will each attempt to insert; PostgreSQL allows
/// exactly one to win, the loser receives a <c>DbUpdateException</c> wrapping
/// a 23505 unique-violation, and the deduper translates that loss into the
/// same <c>AlreadyProcessed=true</c> outcome as if the row already existed.
/// </para>
/// <para>
/// <b>No PII.</b> The row carries only the CloudEvents envelope metadata
/// (id / source / type) plus the outcome. The event payload (data) is NEVER
/// stored here — replay forensics use the per-handler audit trail, not this
/// table. The <see cref="FailureReason"/> column is bounded and sanitised by
/// the writer before persistence (CLAUDE.md §5.6 — no IDNPs, IPs, or token
/// material).
/// </para>
/// <para>
/// <b>Retention.</b> Rows are kept indefinitely by default; a future operator
/// sweep job may prune <see cref="ProcessedAtUtc"/> older than the dedup
/// window. Pruning rows whose <see cref="MessageId"/> is no longer in flight
/// is safe — the upstream producer guarantees no replay beyond the upstream
/// retention window.
/// </para>
/// </remarks>
public sealed class ProcessedIntegrationEvent : AuditableEntity, IExternalId
{
    /// <summary>
    /// CloudEvents v1.0 <c>id</c> attribute of the inbound envelope. UNIQUE
    /// across the table — the column acts as the dedup key. Capped at 128
    /// chars; producers that mint longer ids are out of contract.
    /// </summary>
    public required string MessageId { get; set; }

    /// <summary>
    /// CloudEvents v1.0 <c>source</c> attribute — the URI/URN identifying the
    /// producing system (e.g. <c>cnas-ps</c>, <c>urn:RSP</c>). Stored for
    /// per-source dashboards and per-source retention sweeps.
    /// </summary>
    public required string Source { get; set; }

    /// <summary>
    /// CloudEvents v1.0 <c>type</c> attribute — the reverse-DNS event name
    /// (e.g. <c>md.cnas.ps.decision.issued.v1</c>). Stored for per-type
    /// dashboards.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// UTC instant at which the dispatcher first claimed this MessageId.
    /// Distinct from <see cref="AuditableEntity.CreatedAtUtc"/> only insofar
    /// as the audit timestamp is the row-creation moment — they are populated
    /// to the same value by the deduper today.
    /// </summary>
    public DateTime ProcessedAtUtc { get; set; }

    /// <summary>
    /// Outcome stamped after the downstream handler chain finished. Starts at
    /// <see cref="ProcessedEventOutcome.Accepted"/> on first claim; the
    /// dispatcher may later flip the row to
    /// <see cref="ProcessedEventOutcome.Failed"/> via
    /// <c>IIntegrationEventDeduper.MarkFailedAsync</c> after recording the
    /// downstream exception. Once a row is on Failed it is NEVER reset back to
    /// Accepted — Failed rows still short-circuit subsequent retries.
    /// </summary>
    public ProcessedEventOutcome Outcome { get; set; }

    /// <summary>
    /// Sanitised single-line description of the failure, populated only when
    /// <see cref="Outcome"/> = <see cref="ProcessedEventOutcome.Failed"/>.
    /// Truncated to 1000 chars by the writer; never carries IDNPs / IPs /
    /// token material per CLAUDE.md §5.6.
    /// </summary>
    public string? FailureReason { get; set; }
}
