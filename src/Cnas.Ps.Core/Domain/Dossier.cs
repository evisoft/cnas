namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Dosar — composite case file opened by CNAS when a ServiceApplication is accepted for examination.
/// TOR §2.3 #2. Holds the documents, decisions, and trasabilitate trail for a single service request.
/// </summary>
public sealed class Dossier : AuditableEntity, IExternalId
{
    /// <summary>FK to the originating ServiceApplication.</summary>
    public long ApplicationId { get; set; }

    /// <summary>Navigation to the originating ServiceApplication.</summary>
    public ServiceApplication? Application { get; set; }

    /// <summary>
    /// Internal dossier number (year + sequence). Generated server-side at creation time;
    /// stable for the life of the dossier.
    /// </summary>
    public required string DossierNumber { get; set; }

    /// <summary>FK to the CNAS examiner currently assigned (UC07/UC08).</summary>
    public long? AssignedExaminerId { get; set; }

    /// <summary>FK to the șef-direcție responsible for approving the decision (UC10).</summary>
    public long? ApproverId { get; set; }

    /// <summary>UTC timestamp the examiner accepted the dossier.</summary>
    public DateTime? AcceptedAtUtc { get; set; }

    /// <summary>UTC timestamp the dossier was closed (final decision issued).</summary>
    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>
    /// Computed monetary amount (in MDL) the decision engine awarded for this dossier.
    /// Populated when the dossier is created from an eligible ServiceApplication and consumed
    /// by <c>MPayDispatcherJob</c> when dispatching the outbound payment. Null until the
    /// amount-computation step has run (or for non-monetary services).
    /// </summary>
    /// <remarks>
    /// Populated by <c>ApplicationProcessingService.AdvanceAsync</c> from
    /// <c>DecisionOutcome.Amount</c> at dossier-creation time. Stays null for non-monetary
    /// services (asset-grant, vouchers) — <c>MPayDispatcherJob</c> then skips the row with
    /// a logged warning instead of dispatching a zero-amount transfer.
    /// </remarks>
    public decimal? ComputedAmountMdl { get; set; }
}
