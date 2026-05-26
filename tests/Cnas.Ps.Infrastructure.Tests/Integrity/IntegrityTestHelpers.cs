using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Integrity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Integrity;

/// <summary>
/// R2282 / TOR SEC 036 — shared helpers for the integrity-check test suite.
/// Provides the EF Core InMemory factory + the stub clock used across the
/// Check/Service/Job test files.
/// </summary>
internal static class IntegrityTestHelpers
{
    /// <summary>Canonical "now" used across the integrity tests.</summary>
    public static readonly DateTime ClockNow = new(2026, 5, 23, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>Creates a fresh EF Core InMemory <see cref="CnasDbContext"/>.</summary>
    /// <returns>A new context backed by a uniquely named InMemory store.</returns>
    public static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-integrity-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Wraps a read-only context + stub clock into an <see cref="IIntegrityCheckContext"/>.</summary>
    /// <param name="db">DB context implementing <see cref="IReadOnlyCnasDbContext"/>.</param>
    /// <returns>The context envelope.</returns>
    public static IIntegrityCheckContext WrapContext(CnasDbContext db)
        => new IntegrityCheckContext(db, new StubClock(ClockNow));

    /// <summary>Test-only clock returning the canonical instant.</summary>
    public sealed class StubClock : ICnasTimeProvider
    {
        /// <summary>Constructs the clock with a fixed UTC instant.</summary>
        /// <param name="now">Instant returned from <see cref="UtcNow"/>.</param>
        public StubClock(DateTime now) { UtcNow = now; }

        /// <inheritdoc />
        public DateTime UtcNow { get; }
    }
}
