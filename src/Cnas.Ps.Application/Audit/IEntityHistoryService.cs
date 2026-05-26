using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// R0191 / TOR SEC 050 / TOR ARH 028 — application surface over the
/// <c>EntityHistoryRow</c> projection. Backs the admin REST surface at
/// <c>GET /api/admin/history</c>.
/// </summary>
/// <remarks>
/// <para>
/// The read path queries through the replica
/// (<see cref="Cnas.Ps.Application.Abstractions.IReadOnlyCnasDbContext"/>) so
/// timeline drilldowns don't burden the primary. Rows are returned ordered
/// <c>ChangedAtUtc DESC</c> (most-recent first) up to the configured cap.
/// </para>
/// </remarks>
public interface IEntityHistoryService
{
    /// <summary>
    /// Returns the most-recent history snapshots for one
    /// <see cref="Cnas.Ps.Core.Domain.EntityHistoryRow"/> series identified by
    /// <paramref name="entityType"/> + <paramref name="entitySqid"/>.
    /// </summary>
    /// <param name="entityType">CLR type name of the tracked entity (case-sensitive).</param>
    /// <param name="entitySqid">Sqid-encoded id of the tracked entity.</param>
    /// <param name="cancellationToken">Cancellation token honoured throughout.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the timeline; an empty
    /// <c>Rows</c> list is a valid successful result. Failures:
    /// <see cref="ErrorCodes.InvalidSqid"/> when the Sqid cannot be decoded,
    /// <see cref="ErrorCodes.NotFound"/> when the entity-type string is empty
    /// or whitespace.
    /// </returns>
    Task<Result<EntityHistoryTimelineDto>> GetHistoryAsync(
        string entityType,
        string entitySqid,
        CancellationToken cancellationToken = default);
}
