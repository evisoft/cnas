namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0514 / TOR CF 02.02 — configuration anchor for the pension-projection
/// simulator. Bound from <c>Cnas:Pension</c>; defaults are baked in so the
/// service works without a per-environment configuration override during
/// early development.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stable defaults.</b> The accrual coefficient and minimum-pension floor
/// approximate the figures published by CNAS for the current contributory
/// regime. They are intentionally configurable because the regulator updates
/// them annually; changing the values does NOT require a code release.
/// </para>
/// <para>
/// <b>Constants for retirement ages.</b> The two retirement-age defaults
/// (63 for men, 60 for women) match the statutory schedule at the time of
/// writing. The simulator treats them as the fallback when the caller omits
/// <see cref="Cnas.Ps.Contracts.PensionSimulationInputDto.RetirementAge"/>.
/// </para>
/// </remarks>
public sealed class PensionOptions
{
    /// <summary>Bound from the <c>Cnas:Pension</c> section.</summary>
    public const string SectionName = "Cnas:Pension";

    /// <summary>
    /// Per-year accrual coefficient applied by the projection formula (percent
    /// — e.g. <c>1.35m</c> means 1.35%). Honoured as the default unless the
    /// caller holds <c>Pension.SimulateAdvanced</c> and supplied an override.
    /// </summary>
    public decimal DefaultAccrualCoefficient { get; set; } = 1.35m;

    /// <summary>
    /// Statutory minimum monthly pension (MDL). When the formula result is
    /// below this value, the service substitutes the floor and flips
    /// <see cref="Cnas.Ps.Contracts.PensionSimulationDto.FloorApplied"/>
    /// to <c>true</c>.
    /// </summary>
    public decimal MinPensionFloor { get; set; } = 2000m;

    /// <summary>
    /// Default retirement age for male callers when
    /// <see cref="Cnas.Ps.Contracts.PensionSimulationInputDto.RetirementAge"/>
    /// is omitted.
    /// </summary>
    public int DefaultMaleRetirementAge { get; set; } = 63;

    /// <summary>
    /// Default retirement age for female callers when
    /// <see cref="Cnas.Ps.Contracts.PensionSimulationInputDto.RetirementAge"/>
    /// is omitted.
    /// </summary>
    public int DefaultFemaleRetirementAge { get; set; } = 60;
}
