using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Documents;

/// <summary>
/// R0602 / TOR CF 11.03 — paper-channel fulfilment workflow service. Tracks
/// the per-document Pending → Printed → Dispatched → Delivered state machine
/// driven by the territorial subdivision that owns the physical fulfilment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enqueue.</b> <see cref="EnqueueAsync"/> is invoked by the
/// <c>IDocumentService</c> finalisation path whenever a <c>Document</c>
/// with <c>Channel = Paper</c> is issued. The service writes a Pending
/// <c>PaperFulfilmentRecord</c> and emits a Notice audit row.
/// </para>
/// <para>
/// <b>Idempotency.</b> A second <see cref="EnqueueAsync"/> for the same
/// document fails with
/// <see cref="ErrorCodes.PaperFulfilmentAlreadyEnqueued"/> — the state
/// machine treats the row as authoritative and refuses to start a parallel
/// fulfilment.
/// </para>
/// <para>
/// <b>State transitions.</b> The Mark* methods enforce strict forward
/// progression. Any other transition fails with
/// <see cref="ErrorCodes.PaperFulfilmentInvalidTransition"/>. Audit rows
/// at Notice severity capture every transition.
/// </para>
/// </remarks>
public interface IPaperFulfilmentService
{
    /// <summary>
    /// Creates a fresh <c>PaperFulfilmentRecord</c> in Pending state for
    /// the supplied document and assigns it to the supplied territorial
    /// subdivision.
    /// </summary>
    /// <param name="documentSqid">Sqid-encoded id of the Document being fulfilled.</param>
    /// <param name="territorialSubdivisionCode">Subdivision code that owns the print run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted DTO or a failure code.</returns>
    Task<Result<PaperFulfilmentDto>> EnqueueAsync(
        string documentSqid,
        string territorialSubdivisionCode,
        CancellationToken ct = default);

    /// <summary>Transitions the row to <c>Printed</c>. Source state must be <c>Pending</c>.</summary>
    /// <param name="sqid">Sqid-encoded id of the fulfilment record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or a failure code.</returns>
    Task<Result> MarkPrintedAsync(string sqid, CancellationToken ct = default);

    /// <summary>
    /// Transitions the row to <c>Dispatched</c>. Source state must be
    /// <c>Printed</c>. Captures the carrier tracking number on the row.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the fulfilment record.</param>
    /// <param name="carrierTrackingNumber">Carrier tracking number (≤ 64 chars).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or a failure code.</returns>
    Task<Result> MarkDispatchedAsync(string sqid, string carrierTrackingNumber, CancellationToken ct = default);

    /// <summary>
    /// Transitions the row to <c>Delivered</c>. Source state must be
    /// <c>Dispatched</c>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the fulfilment record.</param>
    /// <param name="deliveredOn">Calendar date the carrier confirmed delivery.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or a failure code.</returns>
    Task<Result> MarkDeliveredAsync(string sqid, DateOnly deliveredOn, CancellationToken ct = default);
}
