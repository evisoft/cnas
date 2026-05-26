using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Audit;

/// <summary>
/// R0191 / TOR SEC 050 / TOR ARH 028 — production implementation of
/// <see cref="IEntityHistoryService"/>. Reads through the replica
/// (<see cref="IReadOnlyCnasDbContext.EntityHistoryRows"/>) and projects rows
/// into Sqid-encoded DTOs.
/// </summary>
public sealed class EntityHistoryService : IEntityHistoryService
{
    /// <summary>
    /// Hard upper bound on rows returned by a single timeline query. Mirrors
    /// the registry-list cap used elsewhere (audit explorer, bulk actions); a
    /// future iteration can promote this to <c>IOptions</c> if the admin UI
    /// needs pagination.
    /// </summary>
    public const int MaxTimelineRows = 500;

    private readonly IReadOnlyCnasDbContext _read;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="read">Replica EF context for the read path.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    public EntityHistoryService(IReadOnlyCnasDbContext read, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(sqids);
        _read = read;
        _sqids = sqids;
    }

    /// <inheritdoc />
    public async Task<Result<EntityHistoryTimelineDto>> GetHistoryAsync(
        string entityType,
        string entitySqid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityType))
        {
            return Result<EntityHistoryTimelineDto>.Failure(
                ErrorCodes.NotFound,
                "Entity type must be supplied.");
        }

        var decoded = _sqids.TryDecode(entitySqid);
        if (decoded.IsFailure)
        {
            return Result<EntityHistoryTimelineDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var entityId = decoded.Value;

        // Trim the input to match the case-sensitive entity-type column. Callers
        // pass the CLR type name as registered by the interceptor — we accept
        // exact matches only because a fuzzy match would mask typos in admin UI
        // URLs and surface confusing empty timelines.
        var normalisedType = entityType.Trim();

        var rows = await _read.EntityHistoryRows
            .Where(r => r.EntityType == normalisedType && r.EntityId == entityId)
            .OrderByDescending(r => r.ChangedAtUtc)
            .ThenByDescending(r => r.Id)
            .Take(MaxTimelineRows)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var projected = new List<EntityHistoryRowDto>(rows.Count);
        foreach (var r in rows)
        {
            projected.Add(new EntityHistoryRowDto(
                Id: _sqids.Encode(r.Id),
                EntityType: r.EntityType,
                EntitySqid: _sqids.Encode(r.EntityId),
                ChangedAtUtc: r.ChangedAtUtc,
                Operation: r.Operation,
                PayloadJson: r.PayloadJson,
                ActorSqid: r.ActorSqid));
        }

        return Result<EntityHistoryTimelineDto>.Success(new EntityHistoryTimelineDto(
            EntityType: normalisedType,
            EntitySqid: entitySqid,
            Rows: projected));
    }
}
