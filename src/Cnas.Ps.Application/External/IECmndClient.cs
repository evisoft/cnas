using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// eCMND — Sistemul Informațional al Actelor de Stare Civilă (electronic Civil-Status
/// Acts register). Source of birth/death/marriage/divorce acts maintained by ASP.
/// </summary>
/// <remarks>
/// Routes through MConnect using service code <c>ECMND.GetCivilAct</c>. See TOR §2.1
/// (item 6 in the list of 11 external systems). <see cref="GetCivilActAsync"/> accepts
/// an <c>actKind</c> argument from the closed set {<c>BIRTH</c>, <c>DEATH</c>,
/// <c>MARRIAGE</c>, <c>DIVORCE</c>}; supplying any other value is a validation error.
/// </remarks>
public interface IECmndClient
{
    /// <summary>
    /// Retrieves the requested civil act for the supplied IDNP, or <c>null</c> when
    /// eCMND has no record of that kind for the person.
    /// </summary>
    /// <param name="idnp">IDNP of the person the act references.</param>
    /// <param name="actKind">One of: <c>BIRTH</c>, <c>DEATH</c>, <c>MARRIAGE</c>, <c>DIVORCE</c>.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}"/> wrapping the optional act. <c>Success(null)</c> means
    /// "no act of that kind on file".
    /// </returns>
    Task<Result<ECmndCivilAct?>> GetCivilActAsync(string idnp, string actKind, CancellationToken ct = default);
}

/// <summary>
/// One civil act as returned by <see cref="IECmndClient"/>.
/// </summary>
/// <param name="ActNumber">eCMND-assigned act number (stable identifier).</param>
/// <param name="ActKind">Echo of the requested act kind (BIRTH/DEATH/MARRIAGE/DIVORCE).</param>
/// <param name="ActDate">Date the act was registered.</param>
/// <param name="IssuerOffice">Civil-status office (oficiul stării civile) code that issued the act.</param>
/// <param name="Attributes">Free-form key/value attributes specific to the act kind (e.g. spouse IDNP for marriage).</param>
public sealed record ECmndCivilAct(string ActNumber, string ActKind, DateOnly ActDate, string IssuerOffice, IReadOnlyDictionary<string, string> Attributes);
