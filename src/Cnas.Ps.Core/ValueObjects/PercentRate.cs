using System.Globalization;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// A percentage value in the inclusive range [0, 100], stored with up to four
/// decimal digits of precision (e.g. <c>22.5</c> means 22.5%, <c>0.1234</c> means 0.1234%).
/// <para>
/// Designed for CNAS contribution rates (cota CAS individual / patron), pension
/// indexation factors, and similar legally-defined coefficients. The 4-decimal
/// precision is enough for all rates published by Government decisions.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var rateCas = PercentRate.TryCreate(22.5m).Value;
/// var contribution = rateCas.Apply(Money.Mdl(8500m));  // 1912.50 MDL
/// </code>
/// </example>
public readonly record struct PercentRate
{
    /// <summary>The percentage value, rounded to 4 decimals. 100 means 100%, not 1.0.</summary>
    public decimal Value { get; }

    private PercentRate(decimal value) => Value = value;

    /// <summary>
    /// Validates and constructs a <see cref="PercentRate"/>.
    /// </summary>
    /// <param name="value">The percentage value (NOT a fraction). Must be in [0, 100].</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> on success, or failure with
    /// <see cref="ErrorCodes.InvalidPercentRate"/> when out of range.
    /// </returns>
    /// <example>
    /// <code>
    /// PercentRate.TryCreate(0m);          // → Success (0%)
    /// PercentRate.TryCreate(100m);        // → Success (100%)
    /// PercentRate.TryCreate(22.5m);       // → Success (22.5%)
    /// PercentRate.TryCreate(-0.01m);      // → Failure
    /// PercentRate.TryCreate(100.01m);     // → Failure
    /// </code>
    /// </example>
    public static Result<PercentRate> TryCreate(decimal value)
    {
        if (value < 0m || value > 100m)
        {
            return Result<PercentRate>.Failure(
                ErrorCodes.InvalidPercentRate,
                $"PercentRate must be between 0 and 100 (was {value.ToString(CultureInfo.InvariantCulture)}).");
        }

        decimal rounded = Math.Round(value, 4, MidpointRounding.ToEven);
        return Result<PercentRate>.Success(new PercentRate(rounded));
    }

    /// <summary>
    /// Applies this rate to a <see cref="Money"/> amount and returns the share
    /// in the same currency, rounded to the currency's scale.
    /// </summary>
    /// <param name="m">The base monetary amount.</param>
    /// <returns>The portion of <paramref name="m"/> that corresponds to this percentage.</returns>
    /// <example>
    /// <code>
    /// var rate = PercentRate.TryCreate(22.5m).Value;
    /// var share = rate.Apply(Money.Mdl(8500m)); // 1912.50 MDL
    /// </code>
    /// </example>
    public Money Apply(Money m) => m * (Value / 100m);

    /// <summary>Returns the percentage with a trailing <c>%</c> sign in invariant culture.</summary>
    /// <returns>e.g. <c>"22.5%"</c>.</returns>
    public override string ToString() =>
        Value.ToString(CultureInfo.InvariantCulture) + "%";
}
