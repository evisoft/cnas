namespace Cnas.Ps.Core.Common;

/// <summary>
/// Abstraction over the system clock. All timestamps in SI "Protecția Socială" are
/// stored and exchanged in UTC; local timezone conversion happens at the presentation
/// layer only (CLAUDE.md cross-cutting — UTC Everywhere).
/// </summary>
public interface ICnasTimeProvider
{
    /// <summary>Current instant in UTC.</summary>
    DateTime UtcNow { get; }

    /// <summary>Current date (UTC midnight).</summary>
    DateOnly TodayUtc => DateOnly.FromDateTime(UtcNow);
}

/// <summary>Default implementation that defers to <see cref="DateTime.UtcNow"/>.</summary>
public sealed class SystemTimeProvider : ICnasTimeProvider
{
    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;
}
