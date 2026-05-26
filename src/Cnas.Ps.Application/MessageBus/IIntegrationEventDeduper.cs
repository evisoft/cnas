using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.MessageBus;

/// <summary>
/// R0103 / TOR CF 14.02 — atomic dedup ledger consulted by the inbound
/// CloudEvents dispatcher to keep MessageId processing idempotent. Per the
/// "at-least-once in transit, exactly-once at the boundary" pattern, every
/// inbound handler MUST call <see cref="TryClaimAsync"/> as the first action
/// on every received envelope; if <see cref="IntegrationEventDedupOutcomeDto.AlreadyProcessed"/>
/// is <c>true</c> the handler short-circuits without invoking the downstream
/// chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomic claim.</b> The implementation MUST be race-free in the face of
/// concurrent dispatchers that both observe a missing row. The canonical
/// implementation relies on a UNIQUE constraint on the underlying dedup-row
/// MessageId column: two competing inserts both reach the database, exactly
/// one commits, the loser receives a unique-violation, and the deduper
/// translates that loss into the same <c>AlreadyProcessed=true</c> outcome
/// as if the row had already been present.
/// </para>
/// <para>
/// <b>No exceptions for the happy path.</b> All operations return
/// <see cref="Result"/> / <see cref="Result{T}"/>. The deduper never throws on
/// duplicate detection — duplicates are an outcome, not an error. Truly
/// exceptional conditions (DbContext disposed, transport offline) may still
/// bubble out as exceptions; see CLAUDE.md §2.1.
/// </para>
/// </remarks>
public interface IIntegrationEventDeduper
{
    /// <summary>
    /// Atomically claims the supplied <paramref name="messageId"/>. On the
    /// first call for a given MessageId the implementation inserts a row with
    /// outcome <c>Accepted</c> and returns
    /// <see cref="IntegrationEventDedupOutcomeDto.AlreadyProcessed"/> =
    /// <c>false</c>; on every subsequent call the implementation returns
    /// <see cref="IntegrationEventDedupOutcomeDto.AlreadyProcessed"/> =
    /// <c>true</c> along with the timestamp the row was originally claimed.
    /// </summary>
    /// <param name="messageId">
    /// CloudEvents v1.0 <c>id</c> attribute of the inbound envelope. Bounded
    /// at 128 characters by the input validator; longer ids fail validation.
    /// </param>
    /// <param name="source">
    /// CloudEvents v1.0 <c>source</c> attribute (URI/URN of the producer).
    /// Bounded at 256 characters; longer values fail validation.
    /// </param>
    /// <param name="type">
    /// CloudEvents v1.0 <c>type</c> attribute (reverse-DNS event name).
    /// Bounded at 256 characters; longer values fail validation.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the dedup outcome on the
    /// happy path; <see cref="ErrorCodes.ValidationFailed"/> when any input is
    /// missing or out of bounds.
    /// </returns>
    Task<Result<IntegrationEventDedupOutcomeDto>> TryClaimAsync(
        string messageId,
        string source,
        string type,
        CancellationToken ct = default);

    /// <summary>
    /// Flips the outcome on an existing dedup row to
    /// <c>Failed</c> and attaches a sanitised
    /// <paramref name="failureReason"/>. Invoked by the inbound dispatcher
    /// when the downstream handler chain raises an unhandled exception after
    /// the row has already been claimed. The row is preserved (not deleted)
    /// so subsequent retries still short-circuit at the dedup boundary.
    /// </summary>
    /// <param name="messageId">
    /// MessageId whose row to flip. If no row exists the implementation
    /// returns <see cref="ErrorCodes.NotFound"/> — the caller is expected to
    /// have successfully claimed the row earlier in the same handler
    /// invocation.
    /// </param>
    /// <param name="failureReason">
    /// Sanitised single-line description of the failure. Truncated to 1000
    /// chars by the writer; MUST NOT carry IDNPs, IP addresses, or token
    /// material per CLAUDE.md §5.6.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on a successful flip;
    /// <see cref="ErrorCodes.ValidationFailed"/> on validation errors;
    /// <see cref="ErrorCodes.NotFound"/> when no row matches the supplied
    /// <paramref name="messageId"/>.
    /// </returns>
    Task<Result> MarkFailedAsync(
        string messageId,
        string failureReason,
        CancellationToken ct = default);

    /// <summary>
    /// Pure-read probe of the dedup ledger; returns <c>true</c> when a row
    /// already exists for the supplied <paramref name="messageId"/>. Useful
    /// for ops dashboards and for cross-handler diagnostics; the dispatcher
    /// itself uses <see cref="TryClaimAsync"/> instead because the claim is
    /// the atomic gate.
    /// </summary>
    /// <param name="messageId">MessageId to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping <c>true</c> when a row
    /// exists, <c>false</c> otherwise; <see cref="ErrorCodes.ValidationFailed"/>
    /// on a missing / oversized MessageId.
    /// </returns>
    Task<Result<bool>> IsKnownAsync(string messageId, CancellationToken ct = default);
}
