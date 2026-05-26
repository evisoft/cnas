using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R1601 / TOR Annex 3.9 — filter envelope for the
/// <c>RegistrulDeciziilor</c> projection. All fields optional;
/// <c>FromUtc</c> / <c>ToUtc</c> bracket the issuance window, and
/// <c>DecisionTypeCode</c> narrows to a single template kind.
/// </summary>
/// <param name="FromUtc">Inclusive lower bound on the decision issuance instant.</param>
/// <param name="ToUtc">Exclusive upper bound on the decision issuance instant.</param>
/// <param name="DecisionTypeCode">
/// Optional stable code of the decision template (e.g. <c>DECIZIE_RECUPERARE_SUME</c>).
/// </param>
public sealed record DecisionRegisterFilter(
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? DecisionTypeCode = null);

/// <summary>
/// R1601 / TOR Annex 3.9 — single-row projection of the
/// <c>RegistrulDeciziilor</c> register. Every id is Sqid-encoded per CLAUDE.md
/// RULE 3.
/// </summary>
/// <param name="Sqid">Sqid-encoded id of the underlying <c>Document</c> row.</param>
/// <param name="DecisionNumber">Public-facing decision number printed on the rendered DOCX.</param>
/// <param name="DecisionTypeCode">Stable code of the originating decision template.</param>
/// <param name="BeneficiaryIdnp">
/// Plaintext IDNP of the targeted beneficiary when resolvable. Sensitive — the
/// API marks the field with <see cref="SensitivityLabel.Restricted"/>.
/// </param>
/// <param name="IssuedAtUtc">UTC instant the decision was issued.</param>
/// <param name="EffectiveFromDate">Optional effective-from anchor (UTC date).</param>
/// <param name="EffectiveToDate">Optional effective-to anchor (UTC date).</param>
/// <param name="Amount">Decision amount in MDL when applicable.</param>
/// <param name="Status">Lifecycle state stable enum-name string.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record DecisionRegisterRowDto(
    [property: SensitivityClassification(SensitivityLabel.Public)] string Sqid,
    [property: SensitivityClassification(SensitivityLabel.Public)] string DecisionNumber,
    [property: SensitivityClassification(SensitivityLabel.Public)] string DecisionTypeCode,
    [property: SensitivityClassification(SensitivityLabel.Restricted)] string? BeneficiaryIdnp,
    [property: SensitivityClassification(SensitivityLabel.Internal)] DateTime IssuedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)] DateTime? EffectiveFromDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)] DateTime? EffectiveToDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)] decimal? Amount,
    [property: SensitivityClassification(SensitivityLabel.Internal)] string Status);

/// <summary>
/// R1602 / TOR Annex 3.10 — single-row projection of the
/// <c>RegistrulConturilorDePlata</c> register. IBAN is rendered masked per
/// SEC 035 (<c>MD12 **** **** **** XXXX 1234</c>).
/// </summary>
/// <param name="Sqid">Sqid-encoded id of the underlying <c>MPayOrder</c> row.</param>
/// <param name="BeneficiaryIdnpHash">
/// Deterministic-hash shadow of the beneficiary IDNP. We never surface the
/// plaintext on the listing endpoint (TOR SEC 035 / CLAUDE.md §5.7).
/// </param>
/// <param name="PaymentMethod">Stable code of the disbursement channel (e.g. <c>MPAY_IBAN</c>).</param>
/// <param name="Iban">Masked IBAN as it should be rendered on the wire.</param>
/// <param name="LastPaymentAtUtc">UTC instant of the most recent confirmed payment.</param>
/// <param name="TotalPaidYtd">Year-to-date paid amount in MDL.</param>
/// <param name="Status">Stable status code (e.g. <c>ACTIVE</c>, <c>PENDING</c>).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BeneficiaryPaymentAccountRowDto(
    [property: SensitivityClassification(SensitivityLabel.Public)] string Sqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)] string BeneficiaryIdnpHash,
    [property: SensitivityClassification(SensitivityLabel.Public)] string PaymentMethod,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Even when masked for the listing endpoint, IBAN names follow the R0228 / SEC 033 convention (Confidential floor).")] string Iban,
    [property: SensitivityClassification(SensitivityLabel.Internal)] DateTime? LastPaymentAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)] decimal TotalPaidYtd,
    [property: SensitivityClassification(SensitivityLabel.Public)] string Status);
