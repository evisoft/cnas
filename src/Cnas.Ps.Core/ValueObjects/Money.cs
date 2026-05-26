using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// Monetary amount expressed in a specific ISO-4217 currency.
/// <para>
/// The amount is automatically rounded to the currency's defined scale using
/// banker's rounding (<see cref="MidpointRounding.ToEven"/>): 1.005 MDL → 1.00,
/// 1.015 MDL → 1.02. This avoids systematic upward bias when summing many values.
/// </para>
/// <para>
/// Arithmetic operators are defined for the same currency only; mixing
/// currencies is a programmer error and throws <see cref="InvalidOperationException"/>
/// rather than returning a <see cref="Result{T}"/>, because mixed-currency arithmetic
/// has no meaningful business semantics at this layer.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var salary = Money.Mdl(8500m);
/// var contribution = salary * 0.225m;     // 1912.50 MDL
/// var bonus = Money.Mdl(500m);
/// var total = salary + bonus;             // 9000.00 MDL
/// // var bad = salary + Money.TryCreate(10m, "EUR").Value; // throws
/// </code>
/// </example>
public readonly record struct Money
{
    /// <summary>
    /// Supported ISO-4217 currencies and their decimal scale (digits after the point).
    /// Restricted to currencies actually used in CNAS workflows (Moldovan leu is the default;
    /// EUR/USD for international cooperation; JPY included as a scale-0 representative;
    /// RON for cross-border payments with Romania).
    /// </summary>
    private static readonly FrozenDictionary<string, int> ScaleTable =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["MDL"] = 2,
            ["EUR"] = 2,
            ["USD"] = 2,
            ["RON"] = 2,
            ["JPY"] = 0,
        }.ToFrozenDictionary();

    private static readonly Regex CurrencyPattern =
        new("^[A-Z]{3}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The signed amount, rounded to the currency's defined scale.</summary>
    public decimal Amount { get; }

    /// <summary>The 3-letter uppercase ISO-4217 currency code.</summary>
    public string CurrencyCode { get; }

    private Money(decimal amount, string currencyCode)
    {
        Amount = amount;
        CurrencyCode = currencyCode;
    }

    /// <summary>
    /// Validates the currency code and constructs a <see cref="Money"/> with the amount
    /// rounded to the currency's scale.
    /// </summary>
    /// <param name="amount">The amount; may be negative (refunds/corrections) or zero.</param>
    /// <param name="currencyCode">A 3-letter ISO-4217 code (case-insensitive); must be supported.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> on success, or failure with
    /// <see cref="ErrorCodes.InvalidMoneyCurrency"/> when the code is missing, malformed, or unsupported.
    /// </returns>
    /// <example>
    /// <code>
    /// Money.TryCreate(100.555m, "MDL"); // → Success, Amount = 100.56
    /// Money.TryCreate(10m, "eur");      // → Success, CurrencyCode = "EUR"
    /// Money.TryCreate(10m, "EURO");     // → Failure (not 3 letters)
    /// Money.TryCreate(10m, "ZZZ");      // → Failure (unsupported)
    /// </code>
    /// </example>
    public static Result<Money> TryCreate(decimal amount, string? currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            return Result<Money>.Failure(
                ErrorCodes.InvalidMoneyCurrency,
                "Currency code cannot be null, empty, or whitespace.");
        }

        string normalized = currencyCode.Trim().ToUpperInvariant();

        if (!CurrencyPattern.IsMatch(normalized))
        {
            return Result<Money>.Failure(
                ErrorCodes.InvalidMoneyCurrency,
                "Currency code must be exactly 3 ASCII letters (ISO-4217).");
        }

        if (!ScaleTable.TryGetValue(normalized, out int scale))
        {
            return Result<Money>.Failure(
                ErrorCodes.InvalidMoneyCurrency,
                $"Currency '{normalized}' is not supported by Cnas.Ps.");
        }

        decimal rounded = Math.Round(amount, scale, MidpointRounding.ToEven);
        return Result<Money>.Success(new Money(rounded, normalized));
    }

    /// <summary>
    /// Convenience factory for Moldovan leu (the default currency of the system).
    /// </summary>
    /// <param name="amount">The amount in MDL. Will be rounded to 2 decimals.</param>
    /// <returns>A <see cref="Money"/> in MDL.</returns>
    /// <example>
    /// <code>
    /// var salary = Money.Mdl(8500m);
    /// </code>
    /// </example>
    public static Money Mdl(decimal amount) =>
        new(Math.Round(amount, 2, MidpointRounding.ToEven), "MDL");

    /// <summary>
    /// Adds two amounts in the same currency.
    /// </summary>
    /// <param name="left">First operand.</param>
    /// <param name="right">Second operand; must share <paramref name="left"/>'s currency.</param>
    /// <returns>A new <see cref="Money"/> with the summed amount, rounded to the currency scale.</returns>
    /// <exception cref="InvalidOperationException">Currencies differ.</exception>
    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right, "+");
        return new Money(
            Math.Round(left.Amount + right.Amount, ScaleTable[left.CurrencyCode], MidpointRounding.ToEven),
            left.CurrencyCode);
    }

    /// <summary>
    /// Subtracts <paramref name="right"/> from <paramref name="left"/> in the same currency.
    /// </summary>
    /// <param name="left">Minuend.</param>
    /// <param name="right">Subtrahend; must share <paramref name="left"/>'s currency.</param>
    /// <returns>A new <see cref="Money"/> with the difference, rounded to the currency scale.</returns>
    /// <exception cref="InvalidOperationException">Currencies differ.</exception>
    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right, "-");
        return new Money(
            Math.Round(left.Amount - right.Amount, ScaleTable[left.CurrencyCode], MidpointRounding.ToEven),
            left.CurrencyCode);
    }

    /// <summary>
    /// Multiplies an amount by a scalar (e.g. a contribution rate fraction).
    /// </summary>
    /// <param name="left">The monetary amount.</param>
    /// <param name="factor">The scalar factor.</param>
    /// <returns>A new <see cref="Money"/> rounded to the currency scale.</returns>
    public static Money operator *(Money left, decimal factor) =>
        new(
            Math.Round(left.Amount * factor, ScaleTable[left.CurrencyCode], MidpointRounding.ToEven),
            left.CurrencyCode);

    /// <summary>
    /// Divides an amount by a scalar.
    /// </summary>
    /// <param name="left">The monetary amount.</param>
    /// <param name="divisor">The scalar divisor; must not be zero.</param>
    /// <returns>A new <see cref="Money"/> rounded to the currency scale.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is zero.</exception>
    public static Money operator /(Money left, decimal divisor) =>
        new(
            Math.Round(left.Amount / divisor, ScaleTable[left.CurrencyCode], MidpointRounding.ToEven),
            left.CurrencyCode);

    private static void EnsureSameCurrency(Money left, Money right, string op)
    {
        if (!string.Equals(left.CurrencyCode, right.CurrencyCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot apply '{op}' to Money values in different currencies " +
                $"({left.CurrencyCode} vs {right.CurrencyCode}).");
        }
    }

    /// <summary>Returns <c>"{amount} {currency}"</c> formatted with invariant culture.</summary>
    /// <returns>e.g. <c>"123.45 MDL"</c>.</returns>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Amount.ToString("F" + ScaleTable[CurrencyCode], CultureInfo.InvariantCulture)} {CurrencyCode}");
}
