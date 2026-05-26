using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// SIAAS — Sistemul Informațional Asistență Socială (Social Assistance Information
/// System). Source of social-assistance benefits paid by MMPS to natural persons.
/// </summary>
/// <remarks>
/// Routes through MConnect using service code <c>SIAAS.GetSocialAssistance</c>. See TOR
/// §2.1 (item 9 in the list of 11 external systems). A <c>null</c> payload means the
/// person is not currently a SIAAS beneficiary.
/// </remarks>
public interface ISiaasClient
{
    /// <summary>
    /// Retrieves the current social-assistance record for the supplied IDNP, or
    /// <c>null</c> when SIAAS has no active benefit on file.
    /// </summary>
    /// <param name="idnp">Beneficiary IDNP.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns><see cref="Result{T}"/> wrapping the optional record.</returns>
    Task<Result<SiaasRecord?>> GetAssistanceAsync(string idnp, CancellationToken ct = default);
}

/// <summary>
/// Social-assistance snapshot returned by <see cref="ISiaasClient"/>.
/// </summary>
/// <param name="IsBeneficiary">True when the person is currently receiving a SIAAS-administered benefit.</param>
/// <param name="MonthlyAllowanceMdl">Current monthly allowance amount in MDL.</param>
/// <param name="GrantedOn">Date the benefit was granted.</param>
/// <param name="ProgramCode">SIAAS program code identifying which assistance scheme is active.</param>
public sealed record SiaasRecord(bool IsBeneficiary, decimal? MonthlyAllowanceMdl, DateOnly? GrantedOn, string ProgramCode);
