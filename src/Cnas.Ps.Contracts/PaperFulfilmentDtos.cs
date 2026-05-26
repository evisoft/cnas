using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0602 / TOR CF 11.03 — well-known paper-fulfilment status names. Stable
/// strings carried on the DTO so the API contract is enum-independent.
/// </summary>
public static class PaperFulfilmentStatusNames
{
    /// <summary>Enqueued; not yet printed.</summary>
    public const string Pending = "Pending";

    /// <summary>Physical print job complete; awaiting carrier pickup.</summary>
    public const string Printed = "Printed";

    /// <summary>Handed to the carrier; in transit.</summary>
    public const string Dispatched = "Dispatched";

    /// <summary>Delivery confirmed by the carrier or recipient.</summary>
    public const string Delivered = "Delivered";
}

/// <summary>
/// R0602 / TOR CF 11.03 — wire DTO for one paper-channel fulfilment row.
/// Surfaces the state-machine progress to the back-office UI.
/// </summary>
/// <param name="Id">Sqid-encoded id of the fulfilment row.</param>
/// <param name="DocumentSqid">Sqid-encoded id of the underlying Document.</param>
/// <param name="TerritorialSubdivisionCode">Subdivision code owning the physical fulfilment.</param>
/// <param name="Status">Stable status name (one of <see cref="PaperFulfilmentStatusNames"/>).</param>
/// <param name="EnqueuedAtUtc">UTC instant the row was enqueued.</param>
/// <param name="PrintedAtUtc">UTC instant the document was printed (null until Printed).</param>
/// <param name="DispatchedAtUtc">UTC instant the package was dispatched (null until Dispatched).</param>
/// <param name="DeliveredOn">Calendar date of delivery (null until Delivered).</param>
/// <param name="CarrierTrackingNumber">Carrier tracking number (null until Dispatched).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record PaperFulfilmentDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DocumentSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TerritorialSubdivisionCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime EnqueuedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? PrintedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? DispatchedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? DeliveredOn,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CarrierTrackingNumber);

/// <summary>
/// R0602 / TOR CF 11.03 — request body for the enqueue endpoint.
/// </summary>
/// <param name="TerritorialSubdivisionCode">Subdivision that owns the physical fulfilment.</param>
public sealed record PaperFulfilmentEnqueueInput(string TerritorialSubdivisionCode);

/// <summary>
/// R0602 / TOR CF 11.03 — request body for the dispatched endpoint.
/// </summary>
/// <param name="CarrierTrackingNumber">Tracking number issued by the carrier.</param>
public sealed record PaperFulfilmentDispatchInput(string CarrierTrackingNumber);

/// <summary>
/// R0602 / TOR CF 11.03 — request body for the delivered endpoint.
/// </summary>
/// <param name="DeliveredOn">Calendar date the carrier confirmed delivery.</param>
public sealed record PaperFulfilmentDeliveryInput(DateOnly DeliveredOn);
