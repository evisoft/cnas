using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// Half-open UTC date/time interval <c>[StartUtc, EndUtc)</c> with optional open end.
/// <para>
/// The start is inclusive, the end is exclusive. An <see cref="EndUtc"/> of <c>null</c>
/// represents an unbounded (still-active) interval — used for contributor employment
/// periods, benefit entitlements, and other open-ended domain facts.
/// </para>
/// <para>
/// Both endpoints MUST have <see cref="DateTimeKind.Utc"/>. Local or unspecified
/// kinds are rejected to enforce CLAUDE.md "UTC Everywhere" principle.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var year = DateRangeUtc.TryCreate(
///     new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
///     new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Value;
///
/// year.Contains(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc)); // true
/// year.Contains(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));  // false (end excluded)
/// </code>
/// </example>
public readonly record struct DateRangeUtc
{
    /// <summary>The inclusive start instant in UTC.</summary>
    public DateTime StartUtc { get; }

    /// <summary>The exclusive end instant in UTC, or <c>null</c> for an open-ended range.</summary>
    public DateTime? EndUtc { get; }

    private DateRangeUtc(DateTime startUtc, DateTime? endUtc)
    {
        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    /// <summary>
    /// Validates and constructs a <see cref="DateRangeUtc"/>.
    /// </summary>
    /// <param name="startUtc">Inclusive start; must be <see cref="DateTimeKind.Utc"/>.</param>
    /// <param name="endUtc">
    /// Exclusive end; <c>null</c> for open-ended. When non-null, must be UTC AND strictly greater
    /// than <paramref name="startUtc"/>.
    /// </param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> on success, or failure with
    /// <see cref="ErrorCodes.InvalidDateRange"/> when kind is wrong or end &lt;= start.
    /// </returns>
    /// <example>
    /// <code>
    /// var open = DateRangeUtc.TryCreate(DateTime.UtcNow, null);                 // open-ended
    /// var closed = DateRangeUtc.TryCreate(start, end);                          // closed range
    /// var bad = DateRangeUtc.TryCreate(DateTime.Now, null);                     // Failure (Local kind)
    /// </code>
    /// </example>
    public static Result<DateRangeUtc> TryCreate(DateTime startUtc, DateTime? endUtc)
    {
        if (startUtc.Kind != DateTimeKind.Utc)
        {
            return Result<DateRangeUtc>.Failure(
                ErrorCodes.InvalidDateRange,
                $"StartUtc must have DateTimeKind.Utc (was {startUtc.Kind}).");
        }

        if (endUtc.HasValue)
        {
            if (endUtc.Value.Kind != DateTimeKind.Utc)
            {
                return Result<DateRangeUtc>.Failure(
                    ErrorCodes.InvalidDateRange,
                    $"EndUtc must have DateTimeKind.Utc (was {endUtc.Value.Kind}).");
            }

            if (endUtc.Value <= startUtc)
            {
                return Result<DateRangeUtc>.Failure(
                    ErrorCodes.InvalidDateRange,
                    "EndUtc must be strictly greater than StartUtc.");
            }
        }

        return Result<DateRangeUtc>.Success(new DateRangeUtc(startUtc, endUtc));
    }

    /// <summary>
    /// The duration of the range, or <c>null</c> if open-ended.
    /// </summary>
    public TimeSpan? Duration => EndUtc.HasValue ? EndUtc.Value - StartUtc : null;

    /// <summary>
    /// Tests whether <paramref name="instantUtc"/> falls within the half-open range
    /// <c>[StartUtc, EndUtc)</c>. For an open-ended range any instant &gt;= <see cref="StartUtc"/> qualifies.
    /// </summary>
    /// <param name="instantUtc">The UTC instant to test. Non-UTC instants return <c>false</c>.</param>
    /// <returns><c>true</c> if the instant is inside the range; otherwise <c>false</c>.</returns>
    /// <example>
    /// <code>
    /// var year = DateRangeUtc.TryCreate(startOfYear, startOfNextYear).Value;
    /// year.Contains(startOfYear);       // true
    /// year.Contains(startOfNextYear);   // false — half-open
    /// </code>
    /// </example>
    public bool Contains(DateTime instantUtc)
    {
        if (instantUtc.Kind != DateTimeKind.Utc)
            return false;

        if (instantUtc < StartUtc)
            return false;

        if (EndUtc.HasValue && instantUtc >= EndUtc.Value)
            return false;

        return true;
    }

    /// <summary>
    /// Tests whether this range shares any instant with <paramref name="other"/>.
    /// Two ranges touching only at a boundary do NOT overlap (half-open semantics).
    /// </summary>
    /// <param name="other">The other range.</param>
    /// <returns><c>true</c> if the ranges share at least one instant; otherwise <c>false</c>.</returns>
    /// <example>
    /// <code>
    /// // [Jan, Jun) and [Jun, Dec) → false (boundary touch)
    /// // [Jan, Jul) and [Jun, Dec) → true
    /// </code>
    /// </example>
    public bool Overlaps(DateRangeUtc other)
    {
        // No overlap if this ends before (or at) the other starts.
        if (EndUtc.HasValue && EndUtc.Value <= other.StartUtc)
            return false;

        // No overlap if the other ends before (or at) this starts.
        if (other.EndUtc.HasValue && other.EndUtc.Value <= StartUtc)
            return false;

        return true;
    }
}
