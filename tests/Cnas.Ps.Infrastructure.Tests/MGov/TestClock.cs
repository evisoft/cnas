using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Deterministic <see cref="ICnasTimeProvider"/> for unit tests. Defaults to a stable
/// reference instant (2026-01-15T08:00:00Z) so that any header derived from
/// <see cref="ICnasTimeProvider.UtcNow"/> is reproducible across runs.
/// </summary>
internal sealed class TestClock : ICnasTimeProvider
{
    /// <inheritdoc />
    public DateTime UtcNow { get; set; } =
        new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc);
}
