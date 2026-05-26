using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// RSUD — Registrul de Stat al Unităților de Drept (State Register of Legal Persons).
/// Authoritative source for legal-entity identity in Moldova (companies, NGOs, public
/// institutions).
/// </summary>
/// <remarks>
/// Routes through MConnect using service code <c>RSUD.GetLegalPerson</c>. See TOR §2.1
/// (item 2 in the list of 11 external systems). Used during contributor onboarding and
/// when validating an employer reference declared by SFS.
/// </remarks>
public interface IRsudClient
{
    /// <summary>
    /// Retrieves the legal-entity record for the given IDNO.
    /// </summary>
    /// <param name="idno">Moldovan organisation numeric code (validated via <see cref="Cnas.Ps.Core.ValueObjects.Idno.TryCreate(string)"/>).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}"/> wrapping <see cref="RsudLegalPerson"/> on success;
    /// <see cref="ErrorCodes.InvalidIdno"/> when validation failed; or
    /// <see cref="ErrorCodes.MConnectFailed"/> on upstream/parse failure.
    /// </returns>
    Task<Result<RsudLegalPerson>> GetByIdnoAsync(string idno, CancellationToken ct = default);
}

/// <summary>
/// RSUD legal-entity snapshot returned by <see cref="IRsudClient"/>.
/// </summary>
/// <param name="Idno">Organisation numeric code (echo from the request).</param>
/// <param name="Name">Legal name of the entity as recorded in RSUD.</param>
/// <param name="LegalForm">Legal form (e.g. "SRL", "SA", "IP", "ONG").</param>
/// <param name="RegisteredOn">Date of state registration.</param>
/// <param name="IsActive">True when the entity is currently active (not in liquidation/struck-off).</param>
/// <param name="Address">Registered legal address, formatted by RSUD.</param>
public sealed record RsudLegalPerson(
    string Idno,
    string Name,
    string LegalForm,
    DateOnly RegisteredOn,
    bool IsActive,
    string? Address);
