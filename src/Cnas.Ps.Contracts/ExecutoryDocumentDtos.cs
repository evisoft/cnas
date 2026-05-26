using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R1600 / R1406 — Executory documents registry (TOR Annex 3.8 + §3.6-G)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R1600 — one executory document (document executoriu) row as it leaves the
/// system. The IDNP and full IBAN are surfaced for in-system viewers; the
/// IBAN is partially masked in audit references via Last4 semantics elsewhere.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying row.</param>
/// <param name="DocumentSeriesNumber">Stable external identifier (e.g. <c>EXE-2026-000001</c>, <c>OE-2026-1234</c>).</param>
/// <param name="DebtorIdnp">Moldovan IDNP (13 digits) of the debtor whose payments must be withheld from.</param>
/// <param name="Kind">Stable enum-name of <c>ExecutoryDocumentKind</c> (<c>CourtOrder</c>, <c>BailiffOrder</c>, <c>NotaryOrder</c>, <c>AdministrativeOrder</c>, <c>Other</c>).</param>
/// <param name="Status">Stable enum-name of <c>ExecutoryDocumentStatus</c> (<c>Active</c>, <c>Suspended</c>, <c>Completed</c>, <c>Cancelled</c>).</param>
/// <param name="IssuedBy">Issuing body (court / bailiff office / notary office).</param>
/// <param name="IssuedDate">Calendar date the document was issued.</param>
/// <param name="EffectiveFrom">First date on which withholding must occur.</param>
/// <param name="EffectiveUntil">Optional last date on which withholding must occur; null = open-ended.</param>
/// <param name="WithholdingMode">Stable enum-name of <c>ExecutoryDocumentWithholdingMode</c> (<c>FixedAmount</c>, <c>Percentage</c>, <c>FullExcessOverMinimum</c>).</param>
/// <param name="WithholdingAmountMdl">Fixed MDL amount per payment when <paramref name="WithholdingMode"/> = <c>FixedAmount</c>.</param>
/// <param name="WithholdingPercentage">Percentage of gross benefit (0..70) when <paramref name="WithholdingMode"/> = <c>Percentage</c>.</param>
/// <param name="PriorityRank">Priority rank (1 = highest, 5 = lowest); the calculator honours documents in PriorityRank ASC order.</param>
/// <param name="CreditorAccountIban">Destination IBAN for the withheld amount (canonical UPPERCASE MD format).</param>
/// <param name="CreditorName">Display name of the creditor.</param>
/// <param name="TotalOwedMdl">Total debt amount (MDL); null when open-ended.</param>
/// <param name="TotalWithheldMdl">Running tally of all amounts withheld against this document (MDL).</param>
/// <param name="CompletedDate">Date the document was marked complete; null in non-terminal states.</param>
/// <param name="CancellationReason">Operator-supplied rationale when status is <c>Cancelled</c>; null otherwise.</param>
public sealed record ExecutoryDocumentDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DocumentSeriesNumber,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string DebtorIdnp,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string IssuedBy,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly IssuedDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly EffectiveFrom,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? EffectiveUntil,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string WithholdingMode,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? WithholdingAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? WithholdingPercentage,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int PriorityRank,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string CreditorAccountIban,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string CreditorName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? TotalOwedMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalWithheldMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? CompletedDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancellationReason);

/// <summary>
/// R1600 — input DTO for the <c>POST /api/executory-documents</c> endpoint.
/// <paramref name="DocumentSeriesNumber"/> is optional — the service
/// auto-generates a value of the form <c>EXE-{year}-{seq:000000}</c> when the
/// caller does not supply one.
/// </summary>
/// <param name="DocumentSeriesNumber">Optional external identifier; auto-generated when null/empty.</param>
/// <param name="DebtorIdnp">Moldovan IDNP (13 digits) of the debtor.</param>
/// <param name="Kind">Stable enum-name of <c>ExecutoryDocumentKind</c>.</param>
/// <param name="IssuedBy">Issuing body (3..256 chars).</param>
/// <param name="IssuedDate">Calendar date the document was issued; must be ≤ today.</param>
/// <param name="EffectiveFrom">First date on which withholding must occur; must be ≥ <paramref name="IssuedDate"/>.</param>
/// <param name="EffectiveUntil">Optional last date on which withholding must occur; must be ≥ <paramref name="EffectiveFrom"/> when set.</param>
/// <param name="WithholdingMode">Stable enum-name of <c>ExecutoryDocumentWithholdingMode</c>.</param>
/// <param name="WithholdingAmountMdl">Fixed MDL amount when mode = <c>FixedAmount</c>; must be &gt; 0 and ≤ 100_000_000.</param>
/// <param name="WithholdingPercentage">Percentage (0..70) when mode = <c>Percentage</c>; two decimals.</param>
/// <param name="PriorityRank">Priority rank (1..5).</param>
/// <param name="CreditorAccountIban">Destination IBAN (canonical UPPERCASE MD format, 24 chars).</param>
/// <param name="CreditorName">Display name of the creditor (3..256 chars).</param>
/// <param name="TotalOwedMdl">Total debt amount (MDL); null for open-ended obligations.</param>
public sealed record ExecutoryDocumentRegisterInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? DocumentSeriesNumber,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string DebtorIdnp,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string IssuedBy,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly IssuedDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly EffectiveFrom,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? EffectiveUntil,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string WithholdingMode,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? WithholdingAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? WithholdingPercentage,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int PriorityRank,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string CreditorAccountIban,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string CreditorName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? TotalOwedMdl);

/// <summary>
/// R1600 — input DTO for the <c>PUT /api/executory-documents/{sqid}</c>
/// endpoint. Nullable fields = "leave unchanged". <paramref name="ChangeReason"/>
/// is mandatory (3..500 chars).
/// </summary>
/// <param name="IssuedBy">New issuing body; null leaves unchanged.</param>
/// <param name="EffectiveUntil">New end date; null leaves unchanged.</param>
/// <param name="WithholdingMode">New mode (stable enum-name); null leaves unchanged.</param>
/// <param name="WithholdingAmountMdl">New fixed amount; null leaves unchanged.</param>
/// <param name="WithholdingPercentage">New percentage (0..70); null leaves unchanged.</param>
/// <param name="PriorityRank">New priority rank (1..5); null leaves unchanged.</param>
/// <param name="CreditorAccountIban">New destination IBAN; null leaves unchanged.</param>
/// <param name="CreditorName">New creditor name; null leaves unchanged.</param>
/// <param name="TotalOwedMdl">New total debt amount; null leaves unchanged.</param>
/// <param name="ChangeReason">Mandatory operator-supplied rationale (3..500 chars).</param>
public sealed record ExecutoryDocumentModifyInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? IssuedBy,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? EffectiveUntil,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? WithholdingMode,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? WithholdingAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? WithholdingPercentage,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int? PriorityRank,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string? CreditorAccountIban,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? CreditorName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? TotalOwedMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ChangeReason);

/// <summary>
/// R1600 — input DTO for the suspend / resume / cancel endpoints. Carries the
/// operator-supplied rationale only.
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
public sealed record ExecutoryDocumentReasonInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R1406 — whole-plan calculation result returned by the withholding
/// calculator. The plan is pure computation — committing it to the registry is
/// the caller's responsibility (after a successful payment is dispatched).
/// </summary>
/// <param name="DebtorIdnp">IDNP the plan was computed for.</param>
/// <param name="GrossBenefitMdl">Gross benefit amount that was passed into the calculator.</param>
/// <param name="LegalMinimumMdl">Legal minimum-subsistence floor passed into the calculator.</param>
/// <param name="BenefitPeriod">Benefit period (calendar date) the plan applies to.</param>
/// <param name="TotalWithheldMdl">Sum of all allocated amounts across the per-row entries.</param>
/// <param name="NetPayableMdl">Gross minus <paramref name="TotalWithheldMdl"/> (what the beneficiary actually receives).</param>
/// <param name="CapHit">True when the 70% cap clipped at least one allocation row (<c>Rationale = CAP_EXCEEDED</c>).</param>
/// <param name="Rows">Per-document plan entries in PriorityRank ASC order.</param>
public sealed record ExecutoryDocumentWithholdingPlanDto(
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string DebtorIdnp,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal GrossBenefitMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal LegalMinimumMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly BenefitPeriod,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalWithheldMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal NetPayableMdl,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool CapHit,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    IReadOnlyList<ExecutoryDocumentWithholdingPlanRowDto> Rows);

/// <summary>
/// R1406 — per-document plan entry produced by the calculator.
/// </summary>
/// <param name="DocumentSqid">Sqid-encoded id of the underlying executory document.</param>
/// <param name="DocumentSeriesNumber">Stable external identifier of the document.</param>
/// <param name="PriorityRank">Priority rank of the document; rows are ordered by this column ASC.</param>
/// <param name="RequestedMdl">Amount the document's parameters asked the calculator to withhold (before cap).</param>
/// <param name="AllocatedMdl">Amount actually allocated after applying the 70% cap (may equal <paramref name="RequestedMdl"/> or be smaller).</param>
/// <param name="Rationale">
/// Short stable code describing why <paramref name="AllocatedMdl"/> equals what it equals:
/// <c>FULL_ALLOCATION</c>, <c>PARTIAL_ALLOCATION</c>, or <c>CAP_EXCEEDED</c>.
/// </param>
/// <param name="CreditorAccountIbanLast4">Last 4 characters of the destination IBAN (masked for audit safety).</param>
public sealed record ExecutoryDocumentWithholdingPlanRowDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DocumentSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DocumentSeriesNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int PriorityRank,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal RequestedMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal AllocatedMdl,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Rationale,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string CreditorAccountIbanLast4);
