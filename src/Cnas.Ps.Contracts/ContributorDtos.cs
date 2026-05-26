using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// Input DTO for registering a new <c>Plătitor de contribuții</c> (contributor) in the
/// CNAS Annex 1 registry per TOR §2.1 / §2.3 #5.
/// </summary>
/// <remarks>
/// All identifier fields that LEAVE the system are Sqid-encoded (CLAUDE.md RULE 3), but
/// the <c>Idno</c> here is a business value object (the Moldovan fiscal code of the legal
/// person), not a database primary key — it is sent in plain form and validated at the
/// application boundary via the <c>Cnas.Ps.Core.ValueObjects.Idno</c> value object.
/// </remarks>
/// <param name="Idno">
/// 13-digit Moldovan IDNO of the legal person. Validated for format and mod-10 checksum.
/// </param>
/// <param name="Denumire">Display name of the contributor. Max 256 characters.</param>
/// <param name="CfojCode">
/// Optional CFOJ classifier code (form of organisation). 1-4 digits when supplied.
/// </param>
/// <param name="CaemCode">
/// Optional CAEM classifier code (primary economic activity). 1-5 digits when supplied.
/// </param>
/// <example>
/// <code>
/// new ContributorRegistrationInput(
///     Idno: "1003600012345",
///     Denumire: "SRL Exemplu",
///     CfojCode: "1170",
///     CaemCode: "47111");
/// </code>
/// </example>
[SensitivityClassification(SensitivityLabel.Confidential,
    Reason = "Registration input carries IDNO + legal-person name treated as PII per R0228 / SEC 033.")]
public sealed record ContributorRegistrationInput(
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "IDNO is the legal-person fiscal code treated as PII per project convention.")]
    string Idno,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Denumire is the legal-person name treated as PII under GDPR.")]
    string Denumire,
    string? CfojCode,
    string? CaemCode);

/// <summary>
/// Output DTO representing a single contributor record as it leaves the system.
/// </summary>
/// <remarks>
/// <para><see cref="Id"/> is a Sqid-encoded string (CLAUDE.md RULE 3).</para>
/// <para><see cref="Idno"/> is the canonical business identifier (not a database key) and is
/// returned in plain form so external systems can correlate.</para>
/// </remarks>
/// <param name="Id">Sqid-encoded internal id of the contributor.</param>
/// <param name="Idno">13-digit Moldovan IDNO (business identifier).</param>
/// <param name="Denumire">Display name of the contributor.</param>
/// <param name="CfojCode">CFOJ classifier code (form of organisation), if known.</param>
/// <param name="CaemCode">CAEM classifier code (economic activity), if known.</param>
/// <param name="IsInsolvent">True when the contributor is on the insolvency list.</param>
/// <param name="RegisteredAtUtc">UTC instant the contributor was first registered with CNAS.</param>
/// <param name="DeregisteredAtUtc">UTC instant the contributor was de-registered, if applicable.</param>
[SensitivityClassification(SensitivityLabel.Confidential,
    Reason = "Contributor records carry IDNO + legal-person name treated as PII per R0228 / SEC 033.")]
public sealed record ContributorOutput(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "IDNO is the legal-person fiscal code treated as PII per project convention (matches PayerDataDto).")]
    string Idno,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Denumire is the legal-person name treated as PII under GDPR.")]
    string Denumire,
    string? CfojCode,
    string? CaemCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool IsInsolvent,
    DateTime RegisteredAtUtc,
    DateTime? DeregisteredAtUtc);

/// <summary>
/// Compact projection used for paged registry listings (TOR UI 014).
/// </summary>
/// <param name="Id">Sqid-encoded internal id of the contributor.</param>
/// <param name="Idno">13-digit Moldovan IDNO (business identifier).</param>
/// <param name="Denumire">Display name of the contributor.</param>
/// <param name="IsInsolvent">True when the contributor is on the insolvency list.</param>
[SensitivityClassification(SensitivityLabel.Confidential,
    Reason = "List rows carry IDNO + legal-person name treated as PII per R0228 / SEC 033.")]
public sealed record ContributorListItem(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string Idno,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string Denumire,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool IsInsolvent);

/// <summary>
/// Result of an "is this IDNO insured as a contributor on a given date" query, callable
/// from <c>IApplicationProcessingService</c> when evaluating eligibility rules.
/// </summary>
/// <param name="Idno">The IDNO the question was asked about.</param>
/// <param name="IsInsured">
/// True when a Contributor with this IDNO is active and was not de-registered before <see cref="AsOfUtc"/>.
/// </param>
/// <param name="AsOfUtc">UTC instant the answer applies to.</param>
[SensitivityClassification(SensitivityLabel.Confidential,
    Reason = "IDNO is the legal-person fiscal code treated as PII per R0228 / SEC 033.")]
public sealed record IsInsuredResult(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string Idno,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool IsInsured,
    DateTime AsOfUtc);

/// <summary>
/// R0305 / BP 1.2 — input DTO for updating the mutable primary attributes of a
/// contributor (Denumire + classifier codes). The 13-digit IDNO is immutable
/// post-registration; corrections to it go through BP 1.7 admin-correction (or
/// BP 1.5 merge if the record was created under the wrong identifier).
/// </summary>
/// <param name="Denumire">New display name; required, 1..256 chars.</param>
/// <param name="CfojCode">Optional CFOJ classifier code (form of organisation), 1..4 digits.</param>
/// <param name="CaemCode">Optional CAEM classifier code (economic activity), 1..5 digits.</param>
public sealed record ContributorAttributesUpdateDto(
    string Denumire,
    string? CfojCode,
    string? CaemCode);

/// <summary>
/// R0305 / BP 1.3 — input DTO for deactivation. Carries the operator-supplied
/// reason that gets persisted on <c>Contributor.DeactivationReason</c> AND
/// recorded on the Critical audit event.
/// </summary>
/// <param name="Reason">Free-form deactivation reason; required, 3..500 chars.</param>
public sealed record ContributorDeactivationInputDto(string Reason);

/// <summary>
/// R0305 / BP 1.4 — input DTO for reactivation. Same shape as
/// <see cref="ContributorDeactivationInputDto"/> but kept distinct so the audit
/// trail differentiates "why deactivated" from "why reactivated".
/// </summary>
/// <param name="Reason">Free-form reactivation reason; required, 3..500 chars.</param>
public sealed record ContributorReactivationInputDto(string Reason);

/// <summary>
/// R0305 / BP 1.6 — placeholder input DTO for the "split" lifecycle operation.
/// Split is rare and tied to specialist tooling not in scope for this batch; the
/// service ships as <c>Result.Failure(NotImplemented, "CONTRIBUTOR_SPLIT_NOT_IMPLEMENTED")</c>.
/// </summary>
/// <param name="Reason">Operator-supplied rationale captured if split is ever implemented.</param>
public sealed record ContributorSplitInputDto(string Reason);

/// <summary>
/// R0305 / BP 1.7 — input DTO for an admin-recorded field-level correction.
/// The actual field write goes through a sibling service (BP 1.2 update or
/// R0301 child-table services); this DTO carries the audit metadata only —
/// hashed before-and-after values so the journal records the change without
/// exposing PII.
/// </summary>
/// <param name="FieldName">Name of the corrected field (e.g. <c>"Denumire"</c>); 1..64 chars.</param>
/// <param name="OldValueHash">SHA-256 / HMAC hex/base64 of the prior value; 1..128 chars.</param>
/// <param name="NewValueHash">SHA-256 / HMAC hex/base64 of the new value; 1..128 chars.</param>
/// <param name="Reason">Justification for the correction; required, 3..500 chars.</param>
public sealed record ContributorAdminCorrectionInputDto(
    string FieldName,
    string OldValueHash,
    string NewValueHash,
    string Reason);

/// <summary>
/// R0305 / BP 1.9 — input DTO for the terminal mark-deceased-or-dissolved event.
/// The service inspects the contributor type (NaturalPerson vs LegalPerson) to
/// decide which flag to set; the effective date is provided by the operator.
/// </summary>
/// <param name="EffectiveDate">Local date the contributor became deceased / dissolved.</param>
public sealed record ContributorMarkDeceasedInputDto(DateOnly EffectiveDate);
