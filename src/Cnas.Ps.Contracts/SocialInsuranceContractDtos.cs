using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0912 / TOR BP 2.2-C — social-insurance contract lifecycle (issue, modify,
// terminate). Reuses the R0311 ContributorSocialInsuranceContractDto for the
// outbound shape.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0912 / BP 2.2-C — input envelope for the
/// <c>POST /api/social-insurance-contracts/issue</c> endpoint. Issues a brand
/// new contract for the specified Contributor (InsuredPerson). Rejected when
/// an overlapping current contract already exists.
/// </summary>
/// <param name="ContributorSqid">Sqid-encoded id of the parent Contributor (InsuredPerson).</param>
/// <param name="ContractNumber">Reference number assigned to the contract (1..50 chars).</param>
/// <param name="ContractStartDate">Date the contract begins to be effective.</param>
/// <param name="ContractEndDate">Optional date the contract ends. Must be &gt; start when present.</param>
/// <param name="MonthlyContributionAmount">Monthly contribution (MDL, 0..1_000_000).</param>
/// <param name="CounterpartyName">Optional counterparty description (e.g. CNAS branch).</param>
/// <param name="ChangeReason">Free-text rationale captured in the audit trail (3..500 chars).</param>
public sealed record SocialInsuranceContractIssueDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ContractNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ContractStartDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ContractEndDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal MonthlyContributionAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CounterpartyName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ChangeReason);

/// <summary>
/// R0912 / BP 2.2-C — input envelope for the
/// <c>PUT /api/social-insurance-contracts/{sqid}/modify</c> endpoint. Applies
/// a supersession update — the current row is closed
/// (<c>ValidToUtc = now</c>) and a new row at <c>ValidFromUtc = now</c> is
/// inserted with the modified fields. Nullable fields preserve the existing
/// value when not supplied.
/// </summary>
/// <param name="ContractNumber">Updated contract reference number (1..50 chars).</param>
/// <param name="ContractStartDate">Optional updated start date.</param>
/// <param name="ContractEndDate">Optional updated end date.</param>
/// <param name="MonthlyContributionAmount">Optional updated monthly amount (MDL, 0..1_000_000).</param>
/// <param name="CounterpartyName">Optional updated counterparty description.</param>
/// <param name="ChangeReason">Free-text rationale captured in the audit trail (3..500 chars).</param>
public sealed record SocialInsuranceContractModifyDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ContractNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ContractStartDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ContractEndDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? MonthlyContributionAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CounterpartyName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ChangeReason);

/// <summary>
/// R0912 / BP 2.2-C — input envelope for the
/// <c>POST /api/social-insurance-contracts/{sqid}/terminate</c> endpoint.
/// Closes the active contract by setting <c>ContractEndDate</c> +
/// <c>ValidToUtc</c> on the existing row — no new supersession row is
/// inserted on terminate (terminal state).
/// </summary>
/// <param name="EffectiveDate">Date the contract terminates.</param>
/// <param name="Reason">Operator rationale (3..500 chars).</param>
public sealed record SocialInsuranceContractTerminateDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly EffectiveDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);
