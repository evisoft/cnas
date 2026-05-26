using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>R0311 — output DTO for a <c>ContributorAddress</c> row.</summary>
/// <param name="Id">Sqid-encoded id of the address row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the parent Contributor (InsuredPerson).</param>
/// <param name="Street">Street line.</param>
/// <param name="City">City.</param>
/// <param name="Region">Region.</param>
/// <param name="PostalCode">Postal code.</param>
/// <param name="Country">ISO-3166-1 alpha-2 country code.</param>
/// <param name="ValidFromUtc">UTC instant the row became active.</param>
/// <param name="ValidToUtc">UTC instant the row was superseded.</param>
/// <param name="ChangeReason">Free-text rationale.</param>
/// <param name="RecordedByUserSqid">Sqid of the operator.</param>
public sealed record ContributorAddressDto(
    string Id,
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Street is citizen address PII per R0228 / SEC 033.")]
    string Street,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "City is citizen address PII per R0228 / SEC 033.")]
    string City,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Region is citizen address PII per R0228 / SEC 033.")]
    string Region,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "PostalCode is citizen address PII per R0228 / SEC 033.")]
    string PostalCode,
    string Country,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string? ChangeReason,
    string? RecordedByUserSqid);

/// <summary>R0311 — input DTO for a ContributorAddress upsert.</summary>
/// <param name="Street">Street line.</param>
/// <param name="City">City.</param>
/// <param name="Region">Region.</param>
/// <param name="PostalCode">Postal code.</param>
/// <param name="Country">ISO-3166-1 alpha-2 country code.</param>
public sealed record ContributorAddressInputDto(
    string Street,
    string City,
    string Region,
    string PostalCode,
    string Country);

/// <summary>R0311 — output DTO for a <c>ContributorContact</c> row.</summary>
/// <param name="Id">Sqid-encoded id of the contact row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the parent.</param>
/// <param name="PhoneE164">Phone in E.164.</param>
/// <param name="Email">Email.</param>
/// <param name="ContactPersonName">Contact person name.</param>
/// <param name="ValidFromUtc">UTC instant the row became active.</param>
/// <param name="ValidToUtc">UTC instant the row was superseded.</param>
/// <param name="ChangeReason">Free-text rationale.</param>
/// <param name="RecordedByUserSqid">Sqid of the operator.</param>
public sealed record ContributorContactDto(
    string Id,
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "PhoneE164 is citizen contact PII per R0228 / SEC 033.")]
    string? PhoneE164,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Email is citizen contact PII per R0228 / SEC 033.")]
    string? Email,
    string? ContactPersonName,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string? ChangeReason,
    string? RecordedByUserSqid);

/// <summary>R0311 — input DTO for a ContributorContact upsert.</summary>
/// <param name="PhoneE164">Phone in E.164.</param>
/// <param name="Email">Email.</param>
/// <param name="ContactPersonName">Contact person name.</param>
public sealed record ContributorContactInputDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Phone is PII")]
    string? PhoneE164,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Email is PII")]
    string? Email,
    string? ContactPersonName);

/// <summary>R0311 — output DTO for a <c>ContributorActivityPeriod</c> row.</summary>
/// <param name="Id">Sqid-encoded id of the activity row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the parent.</param>
/// <param name="EmployerCode">Stable employer reference (e.g. employer IDNO).</param>
/// <param name="Position">Job title at the employer.</param>
/// <param name="MonthlySalary">Monthly salary in MDL (nullable).</param>
/// <param name="ValidFromUtc">UTC instant the period became active.</param>
/// <param name="ValidToUtc">UTC instant the period ended.</param>
/// <param name="ChangeReason">Free-text rationale.</param>
/// <param name="RecordedByUserSqid">Sqid of the operator.</param>
public sealed record ContributorActivityPeriodDto(
    string Id,
    string ContributorSqid,
    string EmployerCode,
    string Position,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "MonthlySalary is personal-finance PII per R0228 / SEC 033.")]
    decimal? MonthlySalary,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string? ChangeReason,
    string? RecordedByUserSqid);

/// <summary>R0311 — input DTO for adding a Contributor activity period.</summary>
/// <param name="EmployerCode">Employer reference (IDNO of the legal-person Payer).</param>
/// <param name="Position">Job title. 1..200 chars.</param>
/// <param name="MonthlySalary">Monthly salary in MDL (must be ≥ 0 when supplied).</param>
public sealed record ContributorActivityPeriodInputDto(
    string EmployerCode,
    string Position,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "MonthlySalary is personal-finance PII per R0228 / SEC 033.")]
    decimal? MonthlySalary);

/// <summary>R0311 — output DTO for a <c>ContributorCivilStatus</c> row.</summary>
/// <param name="Id">Sqid-encoded id of the civil-status row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the parent.</param>
/// <param name="Status">Civil status as a string (e.g. <c>Married</c>).</param>
/// <param name="EffectiveDate">Date the change was recorded in the civil register.</param>
/// <param name="ValidFromUtc">UTC instant the row became active in CNAS.</param>
/// <param name="ValidToUtc">UTC instant the row was superseded.</param>
/// <param name="ChangeReason">Free-text rationale.</param>
/// <param name="RecordedByUserSqid">Sqid of the operator.</param>
public sealed record ContributorCivilStatusDto(
    string Id,
    string ContributorSqid,
    string Status,
    DateOnly? EffectiveDate,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string? ChangeReason,
    string? RecordedByUserSqid);

/// <summary>R0311 — input DTO for a civil-status update.</summary>
/// <param name="Status">Civil-status code as a string (parsed against <c>CivilStatusType</c>).</param>
/// <param name="EffectiveDate">Date of the civil-register entry (optional).</param>
public sealed record ContributorCivilStatusInputDto(
    string Status,
    DateOnly? EffectiveDate);

/// <summary>R0311 — output DTO for a <c>ContributorSocialInsuranceContract</c> row.</summary>
/// <param name="Id">Sqid-encoded id of the contract row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the parent.</param>
/// <param name="ContractNumber">Contract reference number.</param>
/// <param name="ContractStartDate">Start date.</param>
/// <param name="ContractEndDate">End date (nullable).</param>
/// <param name="MonthlyContributionAmount">Monthly contribution in MDL.</param>
/// <param name="CounterpartyName">Optional counterparty description.</param>
/// <param name="ValidFromUtc">UTC instant the row became active.</param>
/// <param name="ValidToUtc">UTC instant the row was superseded.</param>
/// <param name="ChangeReason">Free-text rationale.</param>
/// <param name="RecordedByUserSqid">Sqid of the operator.</param>
public sealed record ContributorSocialInsuranceContractDto(
    string Id,
    string ContributorSqid,
    string ContractNumber,
    DateOnly ContractStartDate,
    DateOnly? ContractEndDate,
    decimal MonthlyContributionAmount,
    string? CounterpartyName,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string? ChangeReason,
    string? RecordedByUserSqid);

/// <summary>R0311 — input DTO for a social-insurance-contract update.</summary>
/// <param name="ContractNumber">Contract reference number. 1..50 chars.</param>
/// <param name="ContractStartDate">Start date.</param>
/// <param name="ContractEndDate">End date (must be > start date when present).</param>
/// <param name="MonthlyContributionAmount">Monthly amount (0..1_000_000).</param>
/// <param name="CounterpartyName">Optional counterparty.</param>
public sealed record ContributorSocialInsuranceContractInputDto(
    string ContractNumber,
    DateOnly ContractStartDate,
    DateOnly? ContractEndDate,
    decimal MonthlyContributionAmount,
    string? CounterpartyName);

/// <summary>R0311 — output DTO for a <c>ContributorPre1999PeriodCarnetMunca</c> row.</summary>
/// <param name="Id">Sqid-encoded id of the row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the parent.</param>
/// <param name="CarnetMuncaNumber">Carnet de muncă booklet number.</param>
/// <param name="PeriodStartDate">Start date.</param>
/// <param name="PeriodEndDate">End date.</param>
/// <param name="EmployerName">Employer recorded in the booklet.</param>
/// <param name="Position">Position recorded in the booklet.</param>
/// <param name="ValidFromUtc">UTC instant the row was digitised.</param>
/// <param name="ValidToUtc">UTC instant the row was superseded (rare).</param>
/// <param name="ChangeReason">Free-text rationale.</param>
/// <param name="RecordedByUserSqid">Sqid of the operator who digitised.</param>
public sealed record ContributorPre1999PeriodCarnetMuncaDto(
    string Id,
    string ContributorSqid,
    string CarnetMuncaNumber,
    DateOnly PeriodStartDate,
    DateOnly PeriodEndDate,
    string? EmployerName,
    string? Position,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string? ChangeReason,
    string? RecordedByUserSqid);

/// <summary>R0311 — input DTO for inserting a pre-1999 Carnet de muncă period.</summary>
/// <param name="CarnetMuncaNumber">Booklet number.</param>
/// <param name="PeriodStartDate">Start date.</param>
/// <param name="PeriodEndDate">End date.</param>
/// <param name="EmployerName">Employer name.</param>
/// <param name="Position">Position.</param>
public sealed record ContributorPre1999PeriodCarnetMuncaInputDto(
    string CarnetMuncaNumber,
    DateOnly PeriodStartDate,
    DateOnly PeriodEndDate,
    string? EmployerName,
    string? Position);
