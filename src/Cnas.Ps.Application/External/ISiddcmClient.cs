using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// SIDDCM — Sistemul Informațional Determinarea Dizabilității și Capacității de Muncă
/// (Disability and Work Capacity Determination System). Source of disability degree
/// evaluations issued by CNDDCM.
/// </summary>
/// <remarks>
/// Routes through MConnect using service code <c>SIDDCM.GetDisabilityStatus</c>. See TOR
/// §2.1 (item 4 in the list of 11 external systems). A <c>null</c> payload is a valid
/// "no disability record on file" answer and is not a failure.
/// </remarks>
public interface ISiddcmClient
{
    /// <summary>
    /// Retrieves the most recent disability evaluation for the supplied IDNP, or
    /// <c>null</c> when SIDDCM has no record.
    /// </summary>
    /// <param name="idnp">Insured person's IDNP.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}"/> wrapping the optional record. <c>Success(null)</c> means
    /// "no disability record on file"; failure means the upstream call could not be
    /// completed.
    /// </returns>
    Task<Result<SiddcmDisabilityRecord?>> GetDisabilityAsync(string idnp, CancellationToken ct = default);
}

/// <summary>
/// Disability evaluation snapshot returned by <see cref="ISiddcmClient"/>.
/// </summary>
/// <param name="Degree">Disability degree code (e.g. "SEVERE", "ACCENTUATED", "MEDIUM").</param>
/// <param name="EvaluatedAtUtc">UTC instant of the evaluation decision.</param>
/// <param name="ValidUntilUtc">UTC instant at which the determination expires; <c>null</c> when permanent.</param>
/// <param name="CommissionRef">Reference identifier of the medical commission that issued the determination.</param>
public sealed record SiddcmDisabilityRecord(string Degree, DateTime EvaluatedAtUtc, DateTime? ValidUntilUtc, string CommissionRef);
