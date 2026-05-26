namespace Cnas.Ps.Contracts;

/// <summary>
/// R0103 / TOR CF 14.02 — outcome envelope returned by the integration-event
/// deduper's <c>TryClaimAsync</c> path. The DTO is consumed exclusively by
/// in-process callers (the inbound CloudEvents dispatcher) — there is no HTTP
/// surface that exposes it directly — but it lives in
/// <c>Cnas.Ps.Contracts</c> because the deduper interface itself sits in the
/// Application layer and the layering rule is "any DTO that crosses the
/// Application↔Infrastructure boundary lives in Contracts".
/// </summary>
/// <remarks>
/// <para>
/// <b>Sensitivity — Internal.</b> The MessageId is operator-visible at the
/// integration boundary; neither it nor the timestamp leak personally
/// identifiable information.
/// </para>
/// <para>
/// <b>Idempotent semantics.</b> When <see cref="AlreadyProcessed"/> is
/// <c>true</c> the deduper guarantees that <see cref="EarlierProcessedAtUtc"/>
/// is populated with the row's original processing instant; when
/// <see cref="AlreadyProcessed"/> is <c>false</c> the deduper guarantees that
/// <see cref="EarlierProcessedAtUtc"/> is <c>null</c> (the row was just
/// minted, "earlier" is not meaningful).
/// </para>
/// </remarks>
/// <param name="AlreadyProcessed">
/// <c>true</c> when the dedup ledger already contained a row for the supplied
/// MessageId BEFORE this call; <c>false</c> when the call was the first one
/// to observe the MessageId. Used by the dispatcher to decide whether to
/// invoke the downstream handler chain.
/// </param>
/// <param name="MessageId">
/// The MessageId echoed back to the caller for correlation logs. Same value
/// the caller just passed in.
/// </param>
/// <param name="EarlierProcessedAtUtc">
/// When <see cref="AlreadyProcessed"/> is <c>true</c>, the UTC instant at
/// which the row was originally inserted. <c>null</c> otherwise.
/// </param>
public sealed record IntegrationEventDedupOutcomeDto(
    bool AlreadyProcessed,
    string MessageId,
    DateTime? EarlierProcessedAtUtc);

/// <summary>
/// R0103 / TOR CF 14.02 — read-only projection of a
/// <c>ProcessedIntegrationEvent</c> row, surfaced through future ops
/// dashboards (the actual HTTP surface is out-of-scope for this iteration).
/// </summary>
/// <remarks>
/// All <c>Id</c> fields are Sqid-encoded strings per CLAUDE.md RULE 3.
/// Sensitivity is Internal: the row never carries event-payload bytes.
/// </remarks>
/// <param name="Id">Sqid-encoded primary key of the dedup row.</param>
/// <param name="MessageId">
/// CloudEvents <c>id</c> attribute of the processed envelope. Capped at 128
/// chars by the writer.
/// </param>
/// <param name="Source">
/// CloudEvents <c>source</c> attribute (e.g. <c>cnas-ps</c>, <c>urn:RSP</c>).
/// </param>
/// <param name="Type">
/// CloudEvents <c>type</c> attribute (e.g.
/// <c>md.cnas.ps.decision.issued.v1</c>).
/// </param>
/// <param name="ProcessedAtUtc">UTC instant at which the row was first claimed.</param>
/// <param name="Outcome">
/// Outcome stamped after the downstream handler chain finished. One of the
/// stable string values defined by the corresponding domain enum
/// (<c>Accepted</c>, <c>Skipped</c>, <c>Failed</c>) — kept as a string here so
/// the wire shape stays stable when new outcomes are added on the producer
/// side.
/// </param>
/// <param name="FailureReason">
/// Sanitised single-line description of the failure when
/// <see cref="Outcome"/> is <c>Failed</c>; <c>null</c> otherwise.
/// </param>
public sealed record ProcessedIntegrationEventDto(
    string Id,
    string MessageId,
    string Source,
    string Type,
    DateTime ProcessedAtUtc,
    string Outcome,
    string? FailureReason);
