using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0514 — Pension Calculator (authenticated self-service simulation)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0514 / TOR CF 02.02 — input for the citizen-facing pension-projection
/// calculator. The endpoint is authenticated (any role) but the service is
/// stateless: it does NOT load the caller's contributory record from the
/// database. The caller supplies the projection variables explicitly so the
/// simulator can also be exercised in a "what if" mode (e.g. "what if I keep
/// contributing 8 more years at this base?").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why explicit inputs.</b> A first-deliverable simulator must avoid
/// coupling to the personal-account aggregation pipeline (R0516) — the
/// production formula in TOR §4.2 carries stagiu-complete adjustments,
/// disability indexation, and historical-base reconciliation that are
/// deliberately deferred. The simpler linear formula documented on
/// <see cref="PensionSimulationDto.FormulaDescriptionRo"/> matches the
/// citizen-portal mockups while leaving room for the richer formula to land
/// without changing the wire shape.
/// </para>
/// <para>
/// <b>No PII echo.</b> The response carries only the projection — it never
/// echoes IDNP, name, or any other personal data. The caller's identity is
/// resolved server-side via <c>ICallerContext</c>; the input record does NOT
/// carry an applicant id.
/// </para>
/// </remarks>
/// <param name="YearsOfService">
/// Total years of contributory service the citizen has accrued (or projects to
/// accrue by retirement). Validated 0..70 by
/// <c>Cnas.Ps.Application.Validators.PensionSimulationInputValidator</c>.
/// </param>
/// <param name="AverageMonthlyContributionBase">
/// Mean monthly contribution base across the citizen's career (MDL). Validated
/// 0..1_000_000.
/// </param>
/// <param name="CurrentAge">
/// Current age of the citizen in completed years. Validated 14..120. Used to
/// derive <see cref="PensionSimulationDto.YearsUntilRetirement"/>.
/// </param>
/// <param name="RetirementAge">
/// Statutory retirement age the citizen plans to reach. Validated 50..75 when
/// supplied. When <c>null</c>, the service falls back to the default for
/// <see cref="Gender"/> (63 for M, 60 for F).
/// </param>
/// <param name="Gender">
/// Gender code — <c>"M"</c> or <c>"F"</c> (validated by the input validator).
/// Determines the default retirement age when <see cref="RetirementAge"/> is
/// omitted; never persisted on the citizen's record by the simulator.
/// </param>
/// <param name="CoefficientOverride">
/// Per-year accrual coefficient override (percent — e.g. <c>1.35m</c> means
/// 1.35%). Honoured ONLY when the caller holds the
/// <c>Pension.SimulateAdvanced</c> permission; silently ignored otherwise so
/// callers without the permission cannot manufacture inflated projections.
/// </param>
public sealed record PensionSimulationInputDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    int YearsOfService,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal AverageMonthlyContributionBase,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    int CurrentAge,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    int? RetirementAge,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Gender,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    decimal? CoefficientOverride);

/// <summary>
/// R0514 / TOR CF 02.02 — output of the citizen-facing pension projection. Both
/// the computed amount and the variables that produced it are returned so the
/// UI can render the inline formula breakdown the citizen-portal mockups
/// require.
/// </summary>
/// <param name="EstimatedMonthlyPension">
/// Projected monthly pension after applying the formula and the minimum-pension
/// floor (MDL). Rounded to 2 decimals.
/// </param>
/// <param name="YearsUntilRetirement">
/// Difference between the effective retirement age and
/// <see cref="PensionSimulationInputDto.CurrentAge"/>. Never negative —
/// citizens already past retirement age see <c>0</c>.
/// </param>
/// <param name="AccrualCoefficient">
/// Effective per-year accrual coefficient used by the formula (percent — e.g.
/// <c>1.35m</c> means 1.35%). Echoes the configured default unless the caller
/// holds <c>Pension.SimulateAdvanced</c> and supplied
/// <see cref="PensionSimulationInputDto.CoefficientOverride"/>.
/// </param>
/// <param name="MinPensionFloor">
/// Statutory minimum monthly pension (MDL) sourced from configuration. The
/// service replaces the formula result with this value when the formula would
/// otherwise produce a lower amount.
/// </param>
/// <param name="FloorApplied">
/// <c>true</c> when the formula result was below the floor and the floor
/// replaced it; <c>false</c> when the formula result stood on its own.
/// </param>
/// <param name="FormulaDescriptionRo">
/// Human-readable Romanian explanation of the formula used, including the
/// substituted variables (e.g.
/// <c>"5000.00 MDL × 1.35% × 25 ani = 1687.50 MDL; aplicat plafonul minim 2000.00 MDL"</c>).
/// </param>
public sealed record PensionSimulationDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal EstimatedMonthlyPension,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    int YearsUntilRetirement,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    decimal AccrualCoefficient,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    decimal MinPensionFloor,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool FloorApplied,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string FormulaDescriptionRo);
