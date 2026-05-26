using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.AthletePensions;

/// <summary>
/// R1403 / TOR §3.6-D — pure-function amount calculator for the athlete-
/// pension registry. Maps a candidate's verified career records onto the
/// regulatory multiplier table and returns the monthly MDL amount plus the
/// snapshotted multiplier components.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placeholder semantics.</b> The multiplier table shipped in this
/// iteration (Olympic gold → 250%, World gold → 180%, ...) plus the coach
/// 0.80 factor are PLACEHOLDER values pending the regulatory load. The
/// breakdown JSON returned alongside the amount captures the per-record
/// contribution so a future audit can re-verify the math without recomputing
/// from scratch.
/// </para>
/// <para>
/// <b>No PII.</b> The calculator consumes only enum-name codes + numerics —
/// the input carries no IDNP, no display name. The breakdown JSON output
/// likewise embeds only achievement codes and contribution percents.
/// </para>
/// </remarks>
public interface IAthletePensionAmountCalculator
{
    /// <summary>
    /// Computes the monthly MDL pension amount from the role + verified
    /// records + regulatory base + optional additional multipliers.
    /// </summary>
    /// <param name="input">PII-free computation envelope.</param>
    /// <returns>
    /// On success an <see cref="AthletePensionAmountComputationDto"/>
    /// carrying the monthly amount + multiplier components + breakdown JSON;
    /// on input shape failure a typed <see cref="ErrorCodes.ValidationFailed"/>
    /// result.
    /// </returns>
    Result<AthletePensionAmountComputationDto> Compute(AthletePensionAmountInputDto input);
}
