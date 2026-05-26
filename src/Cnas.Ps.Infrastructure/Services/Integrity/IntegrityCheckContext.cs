using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services.Integrity;

/// <summary>
/// R2282 / TOR SEC 036 — concrete <see cref="IIntegrityCheckContext"/>
/// implementation. A simple value envelope around the read-only DB context
/// and the clock — the job constructs ONE instance per fire and passes it to
/// every registered check.
/// </summary>
/// <remarks>
/// Reuse is intentional: each check runs against the same point-in-time
/// snapshot of the database, which keeps cross-check finding reports
/// consistent with one another (e.g. an audit reviewing both a Claim and its
/// payments sees the same row state).
/// </remarks>
public sealed class IntegrityCheckContext : IIntegrityCheckContext
{
    /// <summary>Constructs the context with its read-only collaborators.</summary>
    /// <param name="db">Read-only DB context.</param>
    /// <param name="time">UTC clock abstraction.</param>
    public IntegrityCheckContext(IReadOnlyCnasDbContext db, ICnasTimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        Db = db;
        Time = time;
    }

    /// <inheritdoc />
    public IReadOnlyCnasDbContext Db { get; }

    /// <inheritdoc />
    public ICnasTimeProvider Time { get; }
}
