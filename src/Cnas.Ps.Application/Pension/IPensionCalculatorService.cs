using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Pension;

/// <summary>
/// R0514 / TOR CF 02.02 — citizen-facing pension-projection calculator. Drives
/// the authenticated <c>POST /api/self-service/pension/simulate</c> endpoint
/// (Solicitant + Utilizator autorizat). The service is deterministic and
/// stateless: it does NOT load the caller's contributory record from the
/// database, leaving the per-citizen aggregation pipeline to R0516.
/// </summary>
/// <remarks>
/// <para>
/// <b>Formula.</b> A simple linear projection — the implementation rounds the
/// product
/// <c>AverageMonthlyContributionBase × AccrualCoefficient/100 × YearsOfService</c>
/// to two decimals and substitutes the configured minimum-pension floor when
/// the result falls below it. The richer TOR §4.2 formula (stagiu-complete
/// adjustments, disability indexation, historical-base reconciliation) is
/// deliberately deferred — see the iteration scope memo for the gap.
/// </para>
/// <para>
/// <b>Permission gating.</b>
/// <see cref="PensionSimulationInputDto.CoefficientOverride"/> is honoured
/// only when the caller holds the <c>Pension.SimulateAdvanced</c> permission
/// (administrator or future "simulator playground" surface). Callers without
/// the permission may still submit the field — it is silently ignored so the
/// validator can stay aligned with the wire shape across roles. The
/// configured default applies in every other case.
/// </para>
/// <para>
/// <b>Audit.</b> Every successful invocation writes one Information-severity
/// <c>PUBLIC.PENSION_SIMULATION</c> audit row carrying the input variables and
/// the projected amount. The audit row carries NO PII — the inputs are
/// numbers, not identifiers.
/// </para>
/// </remarks>
public interface IPensionCalculatorService
{
    /// <summary>
    /// Validates the input, applies the configured pension formula, and
    /// returns the projected monthly amount plus the formula breakdown.
    /// Always writes one audit row on success.
    /// </summary>
    /// <param name="input">Projection variables — see DTO doc for the field-by-field contract.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated <see cref="PensionSimulationDto"/>; on
    /// validation failure a <see cref="Result{T}"/> with
    /// <see cref="ErrorCodes.ValidationFailed"/>; when the caller is anonymous
    /// (defense in depth) <see cref="ErrorCodes.Unauthorized"/>.
    /// </returns>
    Task<Result<PensionSimulationDto>> SimulateAsync(
        PensionSimulationInputDto input,
        CancellationToken ct = default);
}
