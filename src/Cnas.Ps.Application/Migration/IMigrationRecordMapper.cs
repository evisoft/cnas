using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Migration;

/// <summary>
/// R2431 / TOR M4 — pluggable per-target-entity mapper. Each
/// implementation translates a raw <see cref="MigrationSourceRecord"/>
/// into the JSON-encoded shape expected by the destination aggregate,
/// emitting findings for any data-quality issues encountered along the
/// way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selection.</b> The importer resolves the mapper from the registered
/// <see cref="System.Collections.Generic.IEnumerable{T}"/> by matching
/// <see cref="TargetEntityName"/> against the plan's
/// <see cref="MigrationPlan.TargetEntityName"/>. When no concrete mapper
/// matches, the framework falls back to the
/// <c>IdentityMigrationRecordMapper</c> placeholder.
/// </para>
/// <para>
/// <b>Failure semantics.</b> A successful map returns
/// <see cref="Result{T}.Success(T)"/> with the populated
/// <see cref="MigrationMappedRecord"/>. A blocking validation error
/// returns <see cref="Result{T}.Failure(string, string)"/> — the importer
/// counts the row as failed and persists a Critical
/// <see cref="MigrationFinding"/>. Non-blocking issues ride along on the
/// successful record via <see cref="MigrationMappedRecord.Findings"/>.
/// </para>
/// </remarks>
public interface IMigrationRecordMapper
{
    /// <summary>
    /// Symbolic target-entity name this mapper handles. The framework
    /// matches against <see cref="MigrationPlan.TargetEntityName"/>; the
    /// wildcard <c>"*"</c> is reserved for the identity fallback.
    /// </summary>
    string TargetEntityName { get; }

    /// <summary>
    /// Maps <paramref name="source"/> to a <see cref="MigrationMappedRecord"/>
    /// in the shape expected by the destination aggregate.
    /// </summary>
    /// <param name="source">Raw source record streamed by the adapter.</param>
    /// <param name="plan">The plan controlling the mapping (for descriptor lookup).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Mapped record on success; failure on a blocking validation error.</returns>
    Task<Result<MigrationMappedRecord>> MapAsync(
        MigrationSourceRecord source,
        MigrationPlan plan,
        CancellationToken cancellationToken = default);
}
