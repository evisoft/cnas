using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// RSP — Registrul de Stat al Populației (State Population Register). Authoritative source
/// of citizen civil identity (name, birth date, deceased status, citizenship, address).
/// </summary>
/// <remarks>
/// Routes through MConnect using service code <c>RSP.GetPerson</c>. See TOR §2.1
/// (Sistemele informaționale externe consumate de SI PS — RSP is item 1 in the list of
/// 11 external systems). This facade hides the MConnect service-code/JSON contract from
/// callers so that an MConnect schema change is a one-file refactor.
/// </remarks>
public interface IRspClient
{
    /// <summary>
    /// Retrieves the civil-register record for the given IDNP.
    /// </summary>
    /// <param name="idnp">Moldovan personal numeric code (validated via <see cref="Cnas.Ps.Core.ValueObjects.Idnp.TryCreate(string)"/>).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}"/> wrapping the <see cref="RspPerson"/> on success;
    /// <see cref="ErrorCodes.InvalidIdnp"/> if validation failed; or
    /// <see cref="ErrorCodes.MConnectFailed"/> if the upstream call could not be completed
    /// or the JSON payload could not be parsed.
    /// </returns>
    Task<Result<RspPerson>> GetByIdnpAsync(string idnp, CancellationToken ct = default);
}

/// <summary>
/// RSP citizen snapshot returned by <see cref="IRspClient"/>.
/// </summary>
/// <param name="Idnp">Personal numeric code (echo from the request).</param>
/// <param name="LastName">Family name as recorded in RSP.</param>
/// <param name="FirstName">Given name as recorded in RSP.</param>
/// <param name="Patronymic">Patronymic (middle) name when present in the register.</param>
/// <param name="BirthDate">Date of birth as recorded in RSP.</param>
/// <param name="IsDeceased">True when RSP has registered a death certificate for this person.</param>
/// <param name="DateOfDeath">Date of death when <paramref name="IsDeceased"/> is true.</param>
/// <param name="Address">Last known address of permanent residence, formatted by RSP.</param>
/// <param name="Citizenship">ISO 3166 alpha-3 country code of citizenship (e.g. "MDA").</param>
public sealed record RspPerson(
    string Idnp,
    string LastName,
    string FirstName,
    string? Patronymic,
    DateOnly BirthDate,
    bool IsDeceased,
    DateOnly? DateOfDeath,
    string? Address,
    string? Citizenship);
