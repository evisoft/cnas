using System.Text.RegularExpressions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// Moldovan organization numeric code (Identificator Numeric al Organizației — IDNO),
/// the fiscal code assigned to legal persons (companies, NGOs, public institutions).
/// <para>
/// Format: 13 digits where the first digit is in 1..9 (zero is reserved for natural
/// persons / IDNP). The thirteenth digit is a mod-10 checksum over the first twelve
/// using the weighted pattern {7, 3, 1} cycling: check = (10 - (sum % 10)) % 10.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var result = Idno.TryCreate("1003600012345");
/// if (result.IsSuccess)
/// {
///     Idno idno = result.Value;
///     Console.WriteLine(idno); // → "1003600012345"
/// }
/// </code>
/// </example>
public readonly record struct Idno
{
    private static readonly Regex Pattern =
        new("^[1-9][0-9]{12}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly int[] Weights = { 7, 3, 1 };

    /// <summary>The canonical 13-digit IDNO string.</summary>
    public string Value { get; }

    private Idno(string value) => Value = value;

    /// <summary>
    /// Validates the input and produces an <see cref="Idno"/> on success.
    /// Whitespace at the edges is trimmed before validation.
    /// </summary>
    /// <param name="input">Candidate IDNO string. May be <c>null</c> or whitespace; both are rejected.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the validated <see cref="Idno"/>, or
    /// <see cref="Result{T}.Failure(string, string)"/> with <see cref="ErrorCodes.InvalidIdno"/>.
    /// </returns>
    /// <example>
    /// <code>
    /// Idno.TryCreate("1003600012345");          // → Success
    /// Idno.TryCreate("0123456789012");          // → Failure (must not start with 0)
    /// Idno.TryCreate("0000000000000");          // → Failure (all zeros)
    /// </code>
    /// </example>
    public static Result<Idno> TryCreate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Result<Idno>.Failure(
                ErrorCodes.InvalidIdno,
                "IDNO cannot be null, empty, or whitespace.");
        }

        string trimmed = input.Trim();

        if (!Pattern.IsMatch(trimmed))
        {
            return Result<Idno>.Failure(
                ErrorCodes.InvalidIdno,
                "IDNO must be 13 digits and start with a non-zero digit.");
        }

        int sum = 0;
        for (int i = 0; i < 12; i++)
            sum += (trimmed[i] - '0') * Weights[i % 3];

        int expected = (10 - (sum % 10)) % 10;
        int actual = trimmed[12] - '0';

        if (expected != actual)
        {
            return Result<Idno>.Failure(
                ErrorCodes.InvalidIdno,
                "IDNO checksum digit does not match the computed value.");
        }

        return Result<Idno>.Success(new Idno(trimmed));
    }

    /// <summary>Returns the canonical 13-digit representation.</summary>
    /// <returns>The IDNO digits with no separators.</returns>
    public override string ToString() => Value;
}
