using System.Text.RegularExpressions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// Moldovan personal numeric code (Identificator Numeric Personal — IDNP).
/// <para>
/// Format: 13 digits. The first digit is a century code (0, 1, or 2) and the
/// remaining 12 digits encode date of birth, gender and a serial.
/// The thirteenth digit is a mod-10 checksum over the first twelve digits using
/// the weighted pattern {7, 3, 1} cycling: check = (10 - (sum % 10)) % 10.
/// </para>
/// <para>
/// IDNP values are domain primitives — they identify natural persons in CNAS
/// processes (Cerere intake, dossiers, beneficiary records). They are NEVER
/// exposed in API DTOs directly (see CLAUDE.md RULE 3 — Sqids); when the IDNP
/// itself is the subject of an API operation (e.g. citizen lookup) it travels
/// inside an audited request body, never as a URL segment.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var result = Idnp.TryCreate("2000123456782");
/// if (result.IsSuccess)
/// {
///     Idnp idnp = result.Value;
///     Console.WriteLine(idnp); // → "2000123456782"
/// }
/// </code>
/// </example>
public readonly record struct Idnp
{
    private static readonly Regex Pattern =
        new("^[012][0-9]{12}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly int[] Weights = { 7, 3, 1 };

    /// <summary>The canonical 13-digit IDNP string.</summary>
    public string Value { get; }

    private Idnp(string value) => Value = value;

    /// <summary>
    /// Validates the input and produces an <see cref="Idnp"/> on success.
    /// Whitespace at the edges is trimmed before validation; embedded whitespace
    /// or other formatting characters are rejected.
    /// </summary>
    /// <param name="input">Candidate IDNP string. May be <c>null</c> or whitespace; both are rejected.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the validated <see cref="Idnp"/>, or
    /// <see cref="Result{T}.Failure(string, string)"/> with <see cref="ErrorCodes.InvalidIdnp"/>.
    /// </returns>
    /// <example>
    /// <code>
    /// Idnp.TryCreate("2000123456782");          // → Success
    /// Idnp.TryCreate("0000000000000");          // → Failure (all-zero rejected)
    /// Idnp.TryCreate(null);                      // → Failure (null)
    /// Idnp.TryCreate("123");                     // → Failure (wrong length)
    /// </code>
    /// </example>
    public static Result<Idnp> TryCreate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Result<Idnp>.Failure(
                ErrorCodes.InvalidIdnp,
                "IDNP cannot be null, empty, or whitespace.");
        }

        string trimmed = input.Trim();

        if (!Pattern.IsMatch(trimmed))
        {
            return Result<Idnp>.Failure(
                ErrorCodes.InvalidIdnp,
                "IDNP must be 13 digits and start with 0, 1, or 2.");
        }

        // Reject sentinel values — all-zero is a placeholder, not a real IDNP.
        if (trimmed == "0000000000000")
        {
            return Result<Idnp>.Failure(
                ErrorCodes.InvalidIdnp,
                "IDNP cannot be all zeros.");
        }

        // Mod-10 weighted checksum over the first 12 digits.
        int sum = 0;
        for (int i = 0; i < 12; i++)
            sum += (trimmed[i] - '0') * Weights[i % 3];

        int expected = (10 - (sum % 10)) % 10;
        int actual = trimmed[12] - '0';

        if (expected != actual)
        {
            return Result<Idnp>.Failure(
                ErrorCodes.InvalidIdnp,
                "IDNP checksum digit does not match the computed value.");
        }

        return Result<Idnp>.Success(new Idnp(trimmed));
    }

    /// <summary>Returns the canonical 13-digit representation.</summary>
    /// <returns>The IDNP digits with no separators.</returns>
    public override string ToString() => Value;
}
