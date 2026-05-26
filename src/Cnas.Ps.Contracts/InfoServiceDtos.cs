using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>Inputs for the retirement-age calculator (UC02).</summary>
/// <param name="BirthDate">Citizen birth date used to project the retirement target date.</param>
/// <param name="Sex">
/// Citizen biological sex as the single-character wire-format code: <c>"M"</c>
/// (male) or <c>"F"</c> (female). String-typed for consistency with
/// <c>PensionSimulationInputDto.Gender</c> and
/// <c>AthletePensionAwardDto.BeneficiarySex</c>; the service still inspects only
/// the first character, so legacy lower-case <c>"m"</c> / <c>"f"</c> remain
/// accepted.
/// </param>
public sealed record RetirementAgeInput(
    DateOnly BirthDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Biological sex is citizen PII per R0228 / SEC 033.")]
    string Sex);

/// <summary>Retirement-age calculator output.</summary>
public sealed record RetirementAgeOutput(DateOnly RetirementDate, int AgeYears);

/// <summary>Status surfaced by UC02 (no PII per CF 01.09 / SEC 044).</summary>
public sealed record ApplicationStatusOutput(string ReferenceNumber, string Status, DateTime? LastUpdateUtc);
