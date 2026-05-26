using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Integrity;

/// <summary>
/// R2282 / TOR SEC 036 — collaborator envelope handed to every
/// <see cref="IIntegrityCheck"/> when the job calls
/// <see cref="IIntegrityCheck.RunAsync"/>. Exposes a read-only DB context and
/// the system clock; checks are pure readers and must never mutate state.
/// </summary>
public interface IIntegrityCheckContext
{
    /// <summary>Read-only DB context used to query the aggregate under audit.</summary>
    IReadOnlyCnasDbContext Db { get; }

    /// <summary>Clock abstraction (CLAUDE.md RULE 4) — checks that need "now" use this.</summary>
    ICnasTimeProvider Time { get; }
}
