using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.AthletePensions;

/// <summary>
/// R1403 / TOR §3.6-D — pure-function eligibility evaluator for the athlete-
/// pension registry. Encodes the legal thresholds that decide whether a
/// candidate's career records qualify for the lifetime pension under the
/// given role (Athlete / Coach).
/// </summary>
/// <remarks>
/// <para>
/// <b>Placeholder semantics.</b> The thresholds shipped in this iteration are
/// PLACEHOLDER values pending the regulatory load (Government Decision on
/// athlete pensions). Each rule carries a stable code (<c>R_ATHLETE.*</c> /
/// <c>R_COACH.*</c>) that survives any future revision of the underlying
/// numbers; only the threshold constants will move when the regulation is
/// loaded.
/// </para>
/// <para>
/// <b>No PII.</b> The evaluator consumes only enum-name codes + dates +
/// numerics — the input DTO carries no IDNP, no display name. Implementations
/// register as singletons (stateless once constructed) and MUST be safe to
/// call concurrently.
/// </para>
/// </remarks>
public interface IAthletePensionEligibilityEvaluator
{
    /// <summary>
    /// Evaluates whether the supplied input meets the role-specific
    /// eligibility threshold for a lifetime athlete pension.
    /// </summary>
    /// <param name="input">PII-free evaluation envelope (role + dates + verified records).</param>
    /// <returns>
    /// On success an <see cref="AthletePensionEligibilityVerdictDto"/>
    /// carrying the verdict + explain trace; on input shape failure a typed
    /// <see cref="ErrorCodes.ValidationFailed"/> result.
    /// </returns>
    Result<AthletePensionEligibilityVerdictDto> Evaluate(AthletePensionEligibilityInputDto input);
}
