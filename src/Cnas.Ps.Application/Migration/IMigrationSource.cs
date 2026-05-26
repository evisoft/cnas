using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Migration;

/// <summary>
/// R2430 / R2431 / TOR M4 — pluggable adapter that streams legacy-system
/// records for a <see cref="MigrationPlan"/>. The framework consumes the
/// adapter via <see cref="StreamAsync"/> for the row-by-row import path and
/// via <see cref="CountAsync"/> for the source-side reconciliation count.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only one adapter is wired in this iteration:</b> the
/// <c>InMemoryMigrationSource</c> test fixture. Production adapters
/// (LegacySqlServer / LegacyOracle / Csv) ship in later iterations once the
/// source schema is finalised.
/// </para>
/// <para>
/// <b>Source-kind agreement.</b> Implementations advertise their kind via
/// <see cref="SourceKind"/>. The importer resolves the appropriate adapter
/// from the registered <see cref="IEnumerable{T}"/> by matching the plan's
/// <see cref="MigrationPlan.SourceKind"/>.
/// </para>
/// </remarks>
public interface IMigrationSource
{
    /// <summary>Kind of source this adapter speaks to.</summary>
    MigrationSourceKind SourceKind { get; }

    /// <summary>
    /// Streams every source record covered by <paramref name="plan"/>. The
    /// stream MUST be fully enumerable — back-pressure is the importer's
    /// responsibility, not the source's.
    /// </summary>
    /// <param name="plan">The plan controlling the stream (mapping descriptor, batch size).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>An async sequence of source records.</returns>
    IAsyncEnumerable<MigrationSourceRecord> StreamAsync(
        MigrationPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total number of records the source would emit for
    /// <paramref name="plan"/>. Used by the reconciler to compare against
    /// the persisted staging-row count.
    /// </summary>
    /// <param name="plan">The plan controlling the count.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Total source-row count.</returns>
    Task<long> CountAsync(MigrationPlan plan, CancellationToken cancellationToken = default);
}
