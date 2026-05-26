using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// A Moldovan International Bank Account Number (IBAN).
/// <para>
/// Format: <c>MD</c> + 2 mod-97 check digits + 20 alphanumeric BBAN characters
/// (total 24 characters). Validation follows ISO 13616: letters are mapped to
/// numbers (A=10..Z=35), the IBAN is rearranged with the country/check moved to
/// the end, and the resulting integer must satisfy <c>value mod 97 == 1</c>.
/// </para>
/// <para>
/// Whitespace embedded by humans (the standard "<c>MD24 AGRN 0000 ...</c>"
/// 4-character grouping) is stripped before validation. Lower-case input is
/// up-cased.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var result = IbanMd.TryCreate("MD24 AG00 0000 0123 4567 89XX");
/// if (result.IsSuccess)
/// {
///     Console.WriteLine(result.Value); // → "MD24AG000000012345678XX9" (canonical form, no spaces)
/// }
/// </code>
/// </example>
public readonly record struct IbanMd
{
    private static readonly Regex Pattern =
        new("^MD[0-9]{2}[A-Z0-9]{20}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StripWhitespace =
        new(@"\s", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The canonical 24-character IBAN with no embedded whitespace.</summary>
    public string Value { get; }

    private IbanMd(string value) => Value = value;

    /// <summary>
    /// Validates and normalises a Moldovan IBAN.
    /// </summary>
    /// <param name="input">
    /// IBAN string in any whitespace-grouped form (e.g. <c>"MD24 AG00 0000 ..."</c>).
    /// Case-insensitive.
    /// </param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the canonical form on success, or failure
    /// with <see cref="ErrorCodes.InvalidIban"/>.
    /// </returns>
    /// <example>
    /// <code>
    /// IbanMd.TryCreate("MD24AG000000022500931776");      // → Success (if checksum holds)
    /// IbanMd.TryCreate("RO49AAAA1B31007593840000");      // → Failure (country != MD)
    /// IbanMd.TryCreate("MD24AG0000000000000000");        // → Failure (wrong length)
    /// </code>
    /// </example>
    public static Result<IbanMd> TryCreate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Result<IbanMd>.Failure(
                ErrorCodes.InvalidIban,
                "IBAN cannot be null, empty, or whitespace.");
        }

        string normalized = StripWhitespace.Replace(input, string.Empty).ToUpperInvariant();

        if (!Pattern.IsMatch(normalized))
        {
            return Result<IbanMd>.Failure(
                ErrorCodes.InvalidIban,
                "IBAN must be 'MD' + 2 digits + 20 alphanumerics (24 chars total).");
        }

        // Mod-97 check: move first 4 chars to the end, map letters to digits, then test value mod 97 == 1.
        string rearranged = normalized[4..] + normalized[..4];
        string numeric = MapLettersToDigits(rearranged);

        // Numeric strings can be up to 36 digits — use BigInteger for the mod.
        if (!BigInteger.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return Result<IbanMd>.Failure(
                ErrorCodes.InvalidIban,
                "IBAN failed numeric conversion during checksum.");
        }

        if (value % 97 != BigInteger.One)
        {
            return Result<IbanMd>.Failure(
                ErrorCodes.InvalidIban,
                "IBAN checksum (ISO 13616 mod 97) does not match.");
        }

        return Result<IbanMd>.Success(new IbanMd(normalized));
    }

    /// <summary>
    /// Maps ASCII letters A..Z to their ISO 13616 two-digit numeric equivalents (A=10..Z=35),
    /// leaving digits untouched. The input is assumed to contain only [A-Z0-9].
    /// </summary>
    private static string MapLettersToDigits(string input)
    {
        var sb = new StringBuilder(input.Length * 2);
        foreach (char c in input)
        {
            if (c is >= '0' and <= '9')
            {
                sb.Append(c);
            }
            else
            {
                // c is in 'A'..'Z' by precondition.
                int v = c - 'A' + 10;
                sb.Append(v.ToString(CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    /// <summary>Returns the canonical 24-character IBAN.</summary>
    /// <returns>The IBAN string without any embedded whitespace.</returns>
    public override string ToString() => Value;
}
