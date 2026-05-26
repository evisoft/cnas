namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0311 / ARH 028 / TOR Annex 2.3 — voluntary social-insurance contract on file for an
/// <see cref="InsuredPerson"/>. Captures the contract number, validity window, monthly
/// contribution amount, and optional counterparty (e.g. CNAS branch). Supersession-only
/// updates with the filtered single-current-row index enforced in
/// <c>ContributorSocialInsuranceContractConfiguration</c>.
/// </summary>
/// <remarks>
/// Multiple contracts may be on file for the same Contributor over time (sequentially);
/// at most one concurrent contract is permitted. The application service validates
/// <see cref="ContractEndDate"/> &gt; <see cref="ContractStartDate"/> when both are
/// present.
/// </remarks>
public sealed class ContributorSocialInsuranceContract : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="InsuredPerson"/> row.</summary>
    public long ContributorId { get; set; }

    /// <summary>Contract reference number on the paper original. 1..50 chars.</summary>
    public required string ContractNumber { get; set; }

    /// <summary>Date the contract begins to be effective.</summary>
    public DateOnly ContractStartDate { get; set; }

    /// <summary>Date the contract ends. Null for open-ended contracts.</summary>
    public DateOnly? ContractEndDate { get; set; }

    /// <summary>Monthly contribution amount in MDL. Must be ≥ 0 and ≤ 1_000_000.</summary>
    public decimal MonthlyContributionAmount { get; set; }

    /// <summary>Optional counterparty description (e.g. branch name).</summary>
    public string? CounterpartyName { get; set; }

    /// <summary>UTC instant at which this row became active.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>UTC instant at which this row was superseded. Null when current.</summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>Free-text rationale for the change. Max 500 chars.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Sqid string of the operator who recorded the change.</summary>
    public string? RecordedByUserSqid { get; set; }
}
