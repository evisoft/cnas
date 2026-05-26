namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0602 / TOR CF 11.03 — fulfilment workflow row for a Document whose
/// issuance channel is Paper. Tracks the territorial-subdivision-driven
/// print → dispatch → delivery state machine that runs in parallel to the
/// electronic-channel flow.
/// </summary>
/// <remarks>
/// <para>
/// <b>State machine.</b> One row per Document with Paper channel; statuses
/// flow strictly forward Pending → Printed → Dispatched → Delivered. Any
/// other transition is rejected by <c>IPaperFulfilmentService</c> with the
/// stable code <c>PAPER_FULFILMENT.INVALID_TRANSITION</c>.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — the row is
/// surfaced to the back-office REST surface through a Sqid so operators can
/// reference an individual fulfilment task without exposing the raw long
/// primary key.
/// </para>
/// </remarks>
public sealed class PaperFulfilmentRecord : AuditableEntity, IExternalId
{
    /// <summary>FK to the <see cref="Document"/> being fulfilled.</summary>
    public long DocumentId { get; set; }

    /// <summary>
    /// Stable territorial-subdivision code that owns the physical fulfilment
    /// (mirrors <see cref="CnasBranch.Code"/>). Length ≤ 32.
    /// </summary>
    public string TerritorialSubdivisionCode { get; set; } = string.Empty;

    /// <summary>Current state in the print → dispatch → delivery flow.</summary>
    public PaperFulfilmentStatus Status { get; set; } = PaperFulfilmentStatus.Pending;

    /// <summary>UTC instant the row was enqueued.</summary>
    public DateTime EnqueuedAtUtc { get; set; }

    /// <summary>UTC instant the document was physically printed.</summary>
    public DateTime? PrintedAtUtc { get; set; }

    /// <summary>UTC instant the package was handed to the carrier.</summary>
    public DateTime? DispatchedAtUtc { get; set; }

    /// <summary>Calendar date the delivery was confirmed.</summary>
    public DateOnly? DeliveredOn { get; set; }

    /// <summary>
    /// Optional carrier-supplied tracking number captured when transitioning
    /// to <see cref="PaperFulfilmentStatus.Dispatched"/>. Length ≤ 64.
    /// </summary>
    public string? CarrierTrackingNumber { get; set; }
}

/// <summary>
/// R0602 / TOR CF 11.03 — discrete states of a paper-channel fulfilment.
/// Order is significant: the state machine only allows forward transitions
/// (Pending → Printed → Dispatched → Delivered).
/// </summary>
public enum PaperFulfilmentStatus
{
    /// <summary>Enqueued; not yet printed.</summary>
    Pending = 0,

    /// <summary>Physical print job complete; awaiting carrier pickup.</summary>
    Printed = 1,

    /// <summary>Handed to the carrier; in transit.</summary>
    Dispatched = 2,

    /// <summary>Delivery confirmed by the carrier or recipient.</summary>
    Delivered = 3,
}
