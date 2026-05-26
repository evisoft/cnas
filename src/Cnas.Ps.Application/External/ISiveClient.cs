using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// SIVE — Sistemul Informațional Vulnerabilitate Energetică (Energy Vulnerability
/// Information System). Source of energy-vulnerability certifications used to determine
/// eligibility for energy-related social assistance.
/// </summary>
/// <remarks>
/// Routes through MConnect using service code <c>SIVE.GetEnergyVulnerability</c>. See
/// TOR §2.1 (item 8 in the list of 11 external systems). A <c>null</c> payload means
/// the household has no current certification on file and is not a failure.
/// </remarks>
public interface ISiveClient
{
    /// <summary>
    /// Retrieves the current energy-vulnerability status for the supplied IDNP, or
    /// <c>null</c> when SIVE has no record.
    /// </summary>
    /// <param name="idnp">Insured person's IDNP (head of household).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns><see cref="Result{T}"/> wrapping the optional status record.</returns>
    Task<Result<SiveStatus?>> GetVulnerabilityAsync(string idnp, CancellationToken ct = default);
}

/// <summary>
/// Energy-vulnerability snapshot returned by <see cref="ISiveClient"/>.
/// </summary>
/// <param name="IsVulnerable">True when the household currently holds a vulnerability certification.</param>
/// <param name="CertifiedOn">Date the certification was issued.</param>
/// <param name="ExpiresOn">Date the certification expires.</param>
/// <param name="Category">Vulnerability category code (e.g. "VERY_HIGH", "HIGH", "MEDIUM", "LOW").</param>
public sealed record SiveStatus(bool IsVulnerable, DateOnly? CertifiedOn, DateOnly? ExpiresOn, string Category);
