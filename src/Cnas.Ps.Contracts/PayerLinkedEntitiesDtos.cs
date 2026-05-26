namespace Cnas.Ps.Contracts;

/// <summary>
/// R0301 — output DTO for a single <c>PayerAddress</c> row. All ids are Sqid-encoded
/// (CLAUDE.md RULE 3).
/// </summary>
/// <param name="Id">Sqid-encoded id of the address row.</param>
/// <param name="PayerSqid">Sqid-encoded id of the parent Payer (Contributor).</param>
/// <param name="Street">Street line.</param>
/// <param name="City">City / town.</param>
/// <param name="Region">Region (raion / county).</param>
/// <param name="PostalCode">Postal code.</param>
/// <param name="Country">ISO-3166-1 alpha-2 country code.</param>
/// <param name="ValidFromUtc">UTC instant the row became active.</param>
/// <param name="ValidToUtc">UTC instant the row was superseded (null when current).</param>
/// <param name="ChangeReason">Free-text rationale for the change (may be null).</param>
/// <param name="RecordedByUserSqid">Sqid string of the operator who recorded the change.</param>
public sealed record PayerAddressDto(
    string Id,
    string PayerSqid,
    string Street,
    string City,
    string Region,
    string PostalCode,
    string Country,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string? ChangeReason,
    string? RecordedByUserSqid);

/// <summary>R0301 — input DTO for an address upsert. No Sqid id, no ValidFromUtc/ValidToUtc.</summary>
/// <param name="Street">Street line. 1..200 chars.</param>
/// <param name="City">City. 1..200 chars.</param>
/// <param name="Region">Region (raion). 1..200 chars.</param>
/// <param name="PostalCode">Postal code. 4..10 alphanumeric.</param>
/// <param name="Country">ISO-3166-1 alpha-2 country code (default <c>MD</c>).</param>
public sealed record PayerAddressInputDto(
    string Street,
    string City,
    string Region,
    string PostalCode,
    string Country);

/// <summary>R0301 — output DTO for a single <c>PayerContact</c> row.</summary>
/// <param name="Id">Sqid-encoded id of the contact row.</param>
/// <param name="PayerSqid">Sqid-encoded id of the parent Payer.</param>
/// <param name="PhoneE164">Primary phone (E.164).</param>
/// <param name="Email">Primary email.</param>
/// <param name="ContactPersonName">Free-text contact person name.</param>
/// <param name="ValidFromUtc">UTC instant the row became active.</param>
/// <param name="ValidToUtc">UTC instant the row was superseded.</param>
/// <param name="ChangeReason">Free-text rationale.</param>
/// <param name="RecordedByUserSqid">Sqid of the operator.</param>
public sealed record PayerContactDto(
    string Id,
    string PayerSqid,
    [property: Cnas.Ps.Contracts.Security.SensitivityClassification(
        Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential,
        Reason = "Phone is PII")]
    string? PhoneE164,
    [property: Cnas.Ps.Contracts.Security.SensitivityClassification(
        Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential,
        Reason = "Email is PII")]
    string? Email,
    string? ContactPersonName,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string? ChangeReason,
    string? RecordedByUserSqid);

/// <summary>R0301 — input DTO for a contact upsert.</summary>
/// <param name="PhoneE164">Phone in E.164.</param>
/// <param name="Email">Email.</param>
/// <param name="ContactPersonName">Contact person name.</param>
public sealed record PayerContactInputDto(
    [property: Cnas.Ps.Contracts.Security.SensitivityClassification(
        Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential,
        Reason = "Phone is PII")]
    string? PhoneE164,
    [property: Cnas.Ps.Contracts.Security.SensitivityClassification(
        Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential,
        Reason = "Email is PII")]
    string? Email,
    string? ContactPersonName);

/// <summary>R0301 — output DTO for a single <c>PayerActivityCAEM</c> row.</summary>
/// <param name="Id">Sqid-encoded id of the activity row.</param>
/// <param name="PayerSqid">Sqid-encoded id of the parent Payer.</param>
/// <param name="CaemCode">CAEM Rev. 2 code (e.g. <c>M.69.10</c>).</param>
/// <param name="CaemDescription">Human-readable activity description.</param>
/// <param name="IsPrimary">True when this is the Payer's primary activity.</param>
/// <param name="ValidFromUtc">UTC instant the row became active.</param>
/// <param name="ValidToUtc">UTC instant the row was ended.</param>
/// <param name="ChangeReason">Free-text rationale.</param>
/// <param name="RecordedByUserSqid">Sqid of the operator.</param>
public sealed record PayerActivityCaemDto(
    string Id,
    string PayerSqid,
    string CaemCode,
    string CaemDescription,
    bool IsPrimary,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string? ChangeReason,
    string? RecordedByUserSqid);

/// <summary>R0301 — input DTO for adding a CAEM activity to a Payer.</summary>
/// <param name="CaemCode">CAEM Rev. 2 code, validated against <c>^[A-Z]\.\d{2}\.\d{2}$</c>.</param>
/// <param name="CaemDescription">Free-text description. 1..500 chars.</param>
/// <param name="IsPrimary">True when this should become the Payer's primary activity.</param>
public sealed record PayerActivityCaemInputDto(
    string CaemCode,
    string CaemDescription,
    bool IsPrimary);

/// <summary>R0803 — output DTO for a single <c>PayerBankAccount</c> row.</summary>
/// <param name="Id">Sqid-encoded id of the bank-account row.</param>
/// <param name="PayerSqid">Sqid-encoded id of the parent Payer.</param>
/// <param name="AccountHolderName">Display holder name on the account.</param>
/// <param name="Iban">Canonical-form IBAN (uppercase, no spaces); Restricted at rest.</param>
/// <param name="BankName">Bank denumire / brand.</param>
/// <param name="BankBic">BIC / SWIFT code (8 or 11 chars).</param>
/// <param name="IsPrimary">True when this is the Payer's current primary account.</param>
/// <param name="Currency">ISO 4217 alpha-3 currency code (default MDL).</param>
/// <param name="ValidFromUtc">UTC instant the row became active.</param>
/// <param name="ValidToUtc">UTC instant the row was closed (null when current).</param>
/// <param name="ChangeReason">Free-text rationale.</param>
/// <param name="RecordedByUserSqid">Sqid of the operator.</param>
[Cnas.Ps.Contracts.Security.SensitivityClassification(
    Cnas.Ps.Contracts.Security.SensitivityLabel.Restricted,
    Reason = "Bank account / IBAN of the Payer (R0803 / TOR SEC 035).")]
public sealed record PayerBankAccountDto(
    string Id,
    string PayerSqid,
    string AccountHolderName,
    [property: Cnas.Ps.Contracts.Security.SensitivityClassification(
        Cnas.Ps.Contracts.Security.SensitivityLabel.Restricted,
        Reason = "IBAN crossing the system boundary.")]
    string Iban,
    string BankName,
    string BankBic,
    bool IsPrimary,
    string Currency,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string? ChangeReason,
    string? RecordedByUserSqid);

/// <summary>R0803 — input DTO for adding a bank account to a Payer.</summary>
/// <param name="AccountHolderName">Display holder name (1..200 chars).</param>
/// <param name="Iban">IBAN — ISO 13616 shape; canonicalised (uppercase + de-spaced) in the service.</param>
/// <param name="BankName">Bank denumire (1..200 chars).</param>
/// <param name="BankBic">BIC / SWIFT code, 8 or 11 chars.</param>
/// <param name="IsPrimary">True when this should become the Payer's primary account (supersedes any current primary).</param>
/// <param name="Currency">ISO 4217 alpha-3 currency code (default MDL).</param>
public sealed record PayerBankAccountInputDto(
    string AccountHolderName,
    [property: Cnas.Ps.Contracts.Security.SensitivityClassification(
        Cnas.Ps.Contracts.Security.SensitivityLabel.Restricted,
        Reason = "Incoming IBAN payload.")]
    string Iban,
    string BankName,
    string BankBic,
    bool IsPrimary,
    string Currency);

/// <summary>R0803 — output DTO for a single <c>PayerSecondaryContact</c> row.</summary>
/// <param name="Id">Sqid-encoded id of the secondary-contact row.</param>
/// <param name="PayerSqid">Sqid-encoded id of the parent Payer.</param>
/// <param name="ContactPersonName">Free-text contact-person name.</param>
/// <param name="Role">Optional role descriptor (Accountant, Legal, ...).</param>
/// <param name="PhoneE164">Optional E.164 phone (Confidential PII).</param>
/// <param name="Email">Optional email address (Confidential PII).</param>
/// <param name="ValidFromUtc">UTC instant the row became active.</param>
/// <param name="ValidToUtc">UTC instant the row was closed (null when current).</param>
/// <param name="ChangeReason">Free-text rationale.</param>
/// <param name="RecordedByUserSqid">Sqid of the operator.</param>
public sealed record PayerSecondaryContactDto(
    string Id,
    string PayerSqid,
    string ContactPersonName,
    string? Role,
    [property: Cnas.Ps.Contracts.Security.SensitivityClassification(
        Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential,
        Reason = "Phone is PII.")]
    string? PhoneE164,
    [property: Cnas.Ps.Contracts.Security.SensitivityClassification(
        Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential,
        Reason = "Email is PII.")]
    string? Email,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string? ChangeReason,
    string? RecordedByUserSqid);

/// <summary>R0803 — input DTO for adding a secondary contact to a Payer.</summary>
/// <param name="ContactPersonName">Free-text contact-person name (1..200 chars).</param>
/// <param name="Role">Optional role descriptor (max 100 chars).</param>
/// <param name="PhoneE164">Optional phone in E.164 form.</param>
/// <param name="Email">Optional email address.</param>
public sealed record PayerSecondaryContactInputDto(
    string ContactPersonName,
    string? Role,
    [property: Cnas.Ps.Contracts.Security.SensitivityClassification(
        Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential,
        Reason = "Phone is PII")]
    string? PhoneE164,
    [property: Cnas.Ps.Contracts.Security.SensitivityClassification(
        Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential,
        Reason = "Email is PII")]
    string? Email);

/// <summary>R0301 — output DTO for a <c>PayerHistory</c> row (append-only audit log).</summary>
/// <param name="Id">Sqid-encoded id of the history row.</param>
/// <param name="PayerSqid">Sqid-encoded id of the parent Payer.</param>
/// <param name="FieldName">Name of the parent field that changed.</param>
/// <param name="OldValue">Previous value (stringified).</param>
/// <param name="NewValue">New value (stringified).</param>
/// <param name="ChangeReason">Free-text rationale.</param>
/// <param name="ChangedAtUtc">UTC instant the change was recorded.</param>
/// <param name="RecordedByUserSqid">Sqid of the operator.</param>
public sealed record PayerHistoryDto(
    string Id,
    string PayerSqid,
    string FieldName,
    string? OldValue,
    string? NewValue,
    string? ChangeReason,
    DateTime ChangedAtUtc,
    string? RecordedByUserSqid);
