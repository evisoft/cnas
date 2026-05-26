using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// Persistence façade for <c>MPayOrder</c> rows. Owns the two write paths that make MPay
/// callbacks idempotent (CLAUDE.md cross-cutting "Idempotent Callbacks", red-flag #15):
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="CreateAsync"/> — called by the outbound <c>IMPayClient.PostOrderAsync</c>
///       wrapper before the SOAP call leaves the process, so the inbound callback
///       endpoints always find a row.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="ConfirmAsync"/> — called by the inbound
///       <c>POST /api/mpay/orders/{orderId}/confirm</c> controller when MPay reports the
///       citizen has paid. Idempotent: a retried call with the SAME
///       <c>(orderId, paymentRef)</c> is a no-op success; a retried call with a
///       DIFFERENT <c>paymentRef</c> on an already-confirmed row returns
///       <see cref="ErrorCodes.Conflict"/> — the store never silently overwrites a
///       prior confirmation.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="GetByOrderIdAsync"/> — read-only lookup used by the
///       <c>GET /api/mpay/orders/{orderId}/details</c> controller to quote the amount +
///       descriptor MPay shows the citizen before initiating payment.
///     </description>
///   </item>
/// </list>
/// </remarks>
public interface IMPayOrderStore
{
    /// <summary>
    /// Returns the snapshot of the order keyed by <paramref name="orderId"/>, or
    /// <c>null</c> when no active row exists. Soft-deleted rows are filtered out by the
    /// global query filter — they look like "not found" to the caller.
    /// </summary>
    /// <param name="orderId">CNAS-side stable identifier of the order to read.</param>
    /// <param name="ct">Cancellation token honoured by the underlying DbContext.</param>
    Task<MPayOrderSnapshot?> GetByOrderIdAsync(string orderId, CancellationToken ct = default);

    /// <summary>
    /// Idempotently records a payment confirmation from MPay. Re-applying the same
    /// <c>(orderId, paymentRef)</c> tuple is a no-op success (returns
    /// <see cref="Result.Success()"/> without an extra DB write); a retried call with a
    /// DIFFERENT <paramref name="paymentRef"/> on an already-confirmed row returns
    /// <see cref="Result.Failure(string, string)"/> with
    /// <see cref="ErrorCodes.Conflict"/> — the store never silently overwrites a
    /// previous confirmation.
    /// </summary>
    /// <param name="orderId">CNAS-side stable identifier of the order being confirmed.</param>
    /// <param name="paymentRef">Upstream bank/processor transaction id supplied by MPay.</param>
    /// <param name="confirmedAtUtc">UTC instant at which MPay recorded the settlement.</param>
    /// <param name="ct">Cancellation token honoured by the underlying DbContext.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> on a first-time or replayed confirmation;
    /// <see cref="Result.Failure(string, string)"/> with <see cref="ErrorCodes.NotFound"/>
    /// when <paramref name="orderId"/> is unknown; <see cref="ErrorCodes.Conflict"/>
    /// when the row is already confirmed with a different payment reference.
    /// </returns>
    Task<Result> ConfirmAsync(string orderId, string paymentRef, DateTime confirmedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new <c>MPayOrder</c> row. Called by the outbound
    /// <c>IMPayClient.PostOrderAsync</c> wrapper BEFORE the SOAP send so the inbound
    /// callback endpoints always find a row. Returns
    /// <see cref="Result.Failure(string, string)"/> with <see cref="ErrorCodes.Conflict"/>
    /// when a row with the same <see cref="MPayOrderSnapshot.OrderId"/> already exists —
    /// the natural-key unique index makes that case deterministic.
    /// </summary>
    /// <param name="snapshot">Initial state of the order — <c>PaymentRef</c> and <c>ConfirmedAtUtc</c> are expected to be <c>null</c>.</param>
    /// <param name="ct">Cancellation token honoured by the underlying DbContext.</param>
    Task<Result> CreateAsync(MPayOrderSnapshot snapshot, CancellationToken ct = default);
}

/// <summary>
/// Immutable projection of an <c>MPayOrder</c> row used as the application-layer
/// transport between <see cref="IMPayOrderStore"/> and its callers (controller,
/// outbound <c>IMPayClient</c> wrapper). Mirrors the domain entity's public columns
/// without exposing the internal <c>Id</c> surrogate key.
/// </summary>
/// <param name="OrderId">CNAS-side stable identifier; used verbatim on every MPay callback.</param>
/// <param name="AmountMdl">Amount the citizen will be charged, in Moldovan Lei.</param>
/// <param name="DescriptionRo">Romanian payment descriptor shown on the MPay page.</param>
/// <param name="BeneficiaryIdnp">13-digit IDNP of the payer. PII — never log.</param>
/// <param name="PaymentRef">Upstream bank/processor transaction id; <c>null</c> until the citizen has paid.</param>
/// <param name="ConfirmedAtUtc">UTC instant at which MPay recorded the settlement; <c>null</c> while pending.</param>
public sealed record MPayOrderSnapshot(
    string OrderId,
    decimal AmountMdl,
    string DescriptionRo,
    string BeneficiaryIdnp,
    string? PaymentRef,
    DateTime? ConfirmedAtUtc);
