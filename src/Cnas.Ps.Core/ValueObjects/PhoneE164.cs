using System.Text.RegularExpressions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// A phone number in canonical E.164 form: leading <c>+</c>, then a country code
/// starting with a non-zero digit, then up to 14 more digits (total 15 digits max).
/// <para>
/// Common formatting characters — spaces, dashes, parentheses — are stripped
/// during validation so that user-friendly inputs like
/// <c>"+373 22 255-555"</c> and <c>"+(373) 22-255-555"</c> both normalise to
/// <c>"+37322255555"</c>.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var phone = PhoneE164.TryCreate("+373 22 255 555").Value;
/// Console.WriteLine(phone); // → "+37322255555"
/// </code>
/// </example>
public readonly record struct PhoneE164
{
    private static readonly Regex Pattern =
        new(@"^\+[1-9][0-9]{1,14}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StripChars =
        new(@"[\s\-()]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The canonical E.164 string (leading <c>+</c> followed by 2–15 digits).</summary>
    public string Value { get; }

    private PhoneE164(string value) => Value = value;

    /// <summary>
    /// Validates and normalises the input to E.164 form.
    /// </summary>
    /// <param name="input">Phone string in any whitespace/dash/paren-decorated form.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the canonical form on success, or
    /// failure with <see cref="ErrorCodes.InvalidPhone"/>.
    /// </returns>
    /// <example>
    /// <code>
    /// PhoneE164.TryCreate("+37322255555");          // → Success
    /// PhoneE164.TryCreate("+373 22 255-555");       // → Success, normalised
    /// PhoneE164.TryCreate("37322255555");           // → Failure (missing +)
    /// PhoneE164.TryCreate("+037322255555");         // → Failure (leading 0 after +)
    /// </code>
    /// </example>
    public static Result<PhoneE164> TryCreate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Result<PhoneE164>.Failure(
                ErrorCodes.InvalidPhone,
                "Phone cannot be null, empty, or whitespace.");
        }

        string stripped = StripChars.Replace(input, string.Empty);

        if (!Pattern.IsMatch(stripped))
        {
            return Result<PhoneE164>.Failure(
                ErrorCodes.InvalidPhone,
                "Phone must be in E.164 form: '+' then 2–15 digits, first digit 1-9.");
        }

        return Result<PhoneE164>.Success(new PhoneE164(stripped));
    }

    /// <summary>Returns the canonical E.164 representation.</summary>
    /// <returns>e.g. <c>"+37322255555"</c>.</returns>
    public override string ToString() => Value;
}
