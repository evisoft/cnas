using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.AccessScope;

/// <summary>
/// R0671 continuation / TOR CF 18.06 — reference implementation of
/// <see cref="IAccessScopeBackfillService"/>. Resolves the row set via a QBE filter
/// AND/OR an explicit Sqid list, applies the cap, performs the bulk update, emits
/// the summary audit row + meter, and returns a structured result envelope.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selection semantics.</b> The QBE path and the explicit-Sqid path are
/// UNIONED on the database — each surviving entity is updated exactly once.
/// </para>
/// <para>
/// <b>Cap.</b> Hard 5000-row cap (<see cref="MaxRowsPerCall"/>). The cap is the
/// last gate before the bulk write so a permissive QBE filter can never silently
/// flag more rows than ops intended.
/// </para>
/// <para>
/// <b>Scoped lifetime.</b> Wraps the per-request <see cref="ICnasDbContext"/> +
/// <see cref="ICallerContext"/> + <see cref="IAuditService"/>; registered with the
/// scoped lifetime by <c>InfrastructureServiceCollectionExtensions</c>.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="clock">Time provider — all UpdatedAtUtc stamps go through it.</param>
/// <param name="sqids">Sqid encoder/decoder for inbound id resolution.</param>
/// <param name="audit">Audit service consulted for the Critical summary row.</param>
/// <param name="qbeConverter">QBE converter for the optional filter path.</param>
/// <param name="caller">Request-scoped caller context (for audit actor + correlation).</param>
public sealed class AccessScopeBackfillService(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ISqidService sqids,
    IAuditService audit,
    IQbeToLinqConverter qbeConverter,
    ICallerContext caller) : IAccessScopeBackfillService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly IAuditService _audit = audit;
    private readonly IQbeToLinqConverter _qbeConverter = qbeConverter;
    private readonly ICallerContext _caller = caller;

    /// <summary>
    /// Hard per-call row cap. Operators that need to back-fill more rows must
    /// issue multiple narrower calls.
    /// </summary>
    public const int MaxRowsPerCall = 5000;

    /// <summary>
    /// Audit event code for the Solicitant back-fill summary row. Stable contract
    /// — renaming is a breaking change for audit-log consumers.
    /// </summary>
    public const string SolicitantAuditCode = "ACCESS_SCOPE.BACKFILL.SOLICITANT";

    /// <summary>Audit event code for the ServiceApplication back-fill summary row.</summary>
    public const string ApplicationAuditCode = "ACCESS_SCOPE.BACKFILL.APPLICATION";

    /// <summary>Counter tag value for the Solicitant axis.</summary>
    private const string KindSolicitant = "Solicitant";

    /// <summary>Counter tag value for the ServiceApplication axis.</summary>
    private const string KindApplication = "ServiceApplication";

    /// <inheritdoc />
    public async Task<Result<AccessScopeBackfillResultDto>> AssignSolicitantRegionByPatternAsync(
        AccessScopeSolicitantBackfillInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Decode the Sqid list first so per-row failures are captured even when
        //    the QBE path is invalid — callers see both classes of error in one
        //    round-trip.
        var (decodedIds, failures) = DecodeSqidList(input.ExplicitSolicitantSqids);

        // 2. If a non-trivial QBE filter is supplied, translate it now and surface
        //    the converter's QBE_* failure codes verbatim. An empty Conditions
        //    list is a no-op (matches everything per the converter contract).
        System.Linq.Expressions.Expression<Func<Solicitant, bool>>? qbePredicate = null;
        if (input.Filter is { Conditions.Count: > 0 })
        {
            var serverFilter = MapQbe(input.Filter);
            var predicate = _qbeConverter.Convert<Solicitant>(
                QueryBudgetRegistries.Solicitant, serverFilter);
            if (predicate.IsFailure)
            {
                return Result<AccessScopeBackfillResultDto>.Failure(
                    predicate.ErrorCode!, predicate.ErrorMessage!);
            }
            qbePredicate = predicate.Value;
        }

        // 3. Build the combined queryable. The two selection paths are unioned at
        //    the database via IQueryable.Union — every relational provider
        //    translates this to UNION and the InMemory provider executes it
        //    in-process. We materialise on the explicit-id branch as a
        //    Where(IsActive && ids.Contains) clause so we can also tolerate a
        //    decoded id that resolved to a soft-deleted row (it just drops out
        //    silently — the matched-Sqid book-keeper records that as a failure
        //    later).
        var ids = decodedIds.ToList();
        IQueryable<Solicitant> combined;
        if (qbePredicate is not null && input.ExplicitSolicitantSqids is not null)
        {
            var qbeQuery = _db.Solicitants.Where(s => s.IsActive).Where(qbePredicate);
            var idsQuery = _db.Solicitants.Where(s => s.IsActive && ids.Contains(s.Id));
            combined = qbeQuery.Union(idsQuery);
        }
        else if (qbePredicate is not null)
        {
            combined = _db.Solicitants.Where(s => s.IsActive).Where(qbePredicate);
        }
        else
        {
            combined = _db.Solicitants.Where(s => s.IsActive && ids.Contains(s.Id));
        }

        // 4. Count the resolved set; refuse if it would exceed the per-call cap.
        var matched = await combined.CountAsync(cancellationToken).ConfigureAwait(false);
        if (matched > MaxRowsPerCall)
        {
            return Result<AccessScopeBackfillResultDto>.Failure(
                ErrorCodes.BackfillQuotaExceeded,
                $"Resolved row count {matched} exceeds the per-call cap of {MaxRowsPerCall}.");
        }

        // 5. Materialise the entities (the cap keeps this bounded) and assign
        //    the column. We avoid ExecuteUpdate to keep the InMemory provider —
        //    used by every test in this batch — fully supported.
        var rows = await combined.ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid;
        var rowsUpdated = 0;
        foreach (var row in rows)
        {
            if (!string.Equals(row.RegionCode, input.RegionCode, StringComparison.Ordinal))
            {
                row.RegionCode = input.RegionCode;
                row.UpdatedAtUtc = now;
                row.UpdatedBy = actor;
                rowsUpdated++;
            }
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // 5. Build the matched-Sqid count: only rows whose id was on the inbound
        //    explicit list count toward MatchedSqidCount. We compare against the
        //    materialised set so a Sqid that decoded but referenced an inactive
        //    or QBE-filtered-out row is counted as a failure, not a match.
        var matchedSqidCount = ComputeMatchedSqidCount(rows.Select(r => r.Id), decodedIds, failures);

        // 6. Critical summary audit. The bulk shape is what auditors care about;
        //    we deliberately do NOT emit per-row Notice records.
        var details = SerializeDetails(input.RegionCode, rowsUpdated, matched, KindSolicitant);
        await _audit.RecordAsync(
            SolicitantAuditCode,
            AuditSeverity.Critical,
            actor ?? "?",
            nameof(Solicitant),
            targetEntityId: null,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        // 7. Meter — tag the kind so the cumulative throughput dashboard breaks
        //    down by axis. We add the row count (not 1) so the counter tracks
        //    cumulative back-fill volume.
        if (rowsUpdated > 0)
        {
            CnasMeter.AccessScopeBackfilled.Add(
                rowsUpdated,
                new KeyValuePair<string, object?>("kind", KindSolicitant));
        }

        return Result<AccessScopeBackfillResultDto>.Success(
            new AccessScopeBackfillResultDto(rowsUpdated, matchedSqidCount, failures));
    }

    /// <inheritdoc />
    public async Task<Result<AccessScopeBackfillResultDto>> AssignServiceApplicationSubdivisionByPatternAsync(
        AccessScopeApplicationBackfillInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Validate SubdivisionCode against an active CnasBranch row (R0512).
        // We do this BEFORE touching the row set so a typo never reaches the
        // database — fails loud rather than silently flagging rows with a
        // dead code.
        var branchExists = await _db.CnasBranches
            .AnyAsync(b => b.IsActive && b.Code == input.SubdivisionCode, cancellationToken)
            .ConfigureAwait(false);
        if (!branchExists)
        {
            return Result<AccessScopeBackfillResultDto>.Failure(
                ErrorCodes.BranchNotFound,
                $"No active CnasBranch with Code '{input.SubdivisionCode}'.");
        }

        var (decodedIds, failures) = DecodeSqidList(input.ExplicitApplicationSqids);

        System.Linq.Expressions.Expression<Func<ServiceApplication, bool>>? qbePredicate = null;
        if (input.Filter is { Conditions.Count: > 0 })
        {
            var serverFilter = MapQbe(input.Filter);
            var predicate = _qbeConverter.Convert<ServiceApplication>(
                QueryBudgetRegistries.Cerere, serverFilter);
            if (predicate.IsFailure)
            {
                return Result<AccessScopeBackfillResultDto>.Failure(
                    predicate.ErrorCode!, predicate.ErrorMessage!);
            }
            qbePredicate = predicate.Value;
        }

        var ids = decodedIds.ToList();
        IQueryable<ServiceApplication> combined;
        if (qbePredicate is not null && input.ExplicitApplicationSqids is not null)
        {
            var qbeQuery = _db.Applications.Where(a => a.IsActive).Where(qbePredicate);
            var idsQuery = _db.Applications.Where(a => a.IsActive && ids.Contains(a.Id));
            combined = qbeQuery.Union(idsQuery);
        }
        else if (qbePredicate is not null)
        {
            combined = _db.Applications.Where(a => a.IsActive).Where(qbePredicate);
        }
        else
        {
            combined = _db.Applications.Where(a => a.IsActive && ids.Contains(a.Id));
        }

        var matched = await combined.CountAsync(cancellationToken).ConfigureAwait(false);
        if (matched > MaxRowsPerCall)
        {
            return Result<AccessScopeBackfillResultDto>.Failure(
                ErrorCodes.BackfillQuotaExceeded,
                $"Resolved row count {matched} exceeds the per-call cap of {MaxRowsPerCall}.");
        }

        var rows = await combined.ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid;
        var rowsUpdated = 0;
        foreach (var row in rows)
        {
            if (!string.Equals(row.SubdivisionCode, input.SubdivisionCode, StringComparison.Ordinal))
            {
                row.SubdivisionCode = input.SubdivisionCode;
                row.UpdatedAtUtc = now;
                row.UpdatedBy = actor;
                rowsUpdated++;
            }
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var matchedSqidCount = ComputeMatchedSqidCount(rows.Select(r => r.Id), decodedIds, failures);

        var details = SerializeDetails(input.SubdivisionCode, rowsUpdated, matched, KindApplication);
        await _audit.RecordAsync(
            ApplicationAuditCode,
            AuditSeverity.Critical,
            actor ?? "?",
            nameof(ServiceApplication),
            targetEntityId: null,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        if (rowsUpdated > 0)
        {
            CnasMeter.AccessScopeBackfilled.Add(
                rowsUpdated,
                new KeyValuePair<string, object?>("kind", KindApplication));
        }

        return Result<AccessScopeBackfillResultDto>.Success(
            new AccessScopeBackfillResultDto(rowsUpdated, matchedSqidCount, failures));
    }

    /// <summary>
    /// Decodes an inbound Sqid list, splitting it into (decoded ids, per-row
    /// failures). A null/empty input list yields an empty id set and no
    /// failures.
    /// </summary>
    /// <param name="sqidList">Optional inbound Sqid list.</param>
    /// <returns>(decoded ids, per-row failures).</returns>
    private (List<long> Ids, List<AccessScopeBackfillFailureDto> Failures) DecodeSqidList(
        IReadOnlyList<string>? sqidList)
    {
        var ids = new List<long>();
        var failures = new List<AccessScopeBackfillFailureDto>();
        if (sqidList is null || sqidList.Count == 0)
        {
            return (ids, failures);
        }
        foreach (var sqid in sqidList)
        {
            if (string.IsNullOrWhiteSpace(sqid))
            {
                failures.Add(new AccessScopeBackfillFailureDto(
                    sqid ?? string.Empty,
                    ErrorCodes.InvalidSqid,
                    "Sqid was null or whitespace."));
                continue;
            }
            var decoded = _sqids.TryDecode(sqid);
            if (decoded.IsFailure)
            {
                failures.Add(new AccessScopeBackfillFailureDto(
                    sqid, decoded.ErrorCode ?? ErrorCodes.InvalidSqid,
                    decoded.ErrorMessage ?? "Sqid could not be decoded."));
                continue;
            }
            ids.Add(decoded.Value);
        }
        return (ids, failures);
    }

    /// <summary>
    /// Computes <c>MatchedSqidCount</c> as the number of decoded ids that
    /// actually surfaced in the materialised row set; ids that decoded but did
    /// not resolve (soft-deleted / out-of-filter) are appended to
    /// <paramref name="failures"/> with <see cref="ErrorCodes.InvalidId"/>.
    /// </summary>
    /// <param name="materialisedIds">Ids of the rows we actually loaded.</param>
    /// <param name="decodedIds">Ids decoded from the inbound Sqid list.</param>
    /// <param name="failures">Mutable per-row failure list to append to.</param>
    /// <returns>Number of Sqids that matched a row.</returns>
    private static int ComputeMatchedSqidCount(
        IEnumerable<long> materialisedIds,
        List<long> decodedIds,
        List<AccessScopeBackfillFailureDto> failures)
    {
        if (decodedIds.Count == 0)
        {
            return 0;
        }
        var materialised = new HashSet<long>(materialisedIds);
        var matched = 0;
        foreach (var id in decodedIds)
        {
            if (materialised.Contains(id))
            {
                matched++;
            }
            else
            {
                // The Sqid decoded successfully but the underlying row did not
                // resolve — either soft-deleted or filtered out by QBE / branch
                // gate. Surface as a per-row failure so the caller can correct
                // the input on a retry.
                failures.Add(new AccessScopeBackfillFailureDto(
                    Sqid: id.ToString(CultureInfo.InvariantCulture),
                    ErrorCode: ErrorCodes.InvalidId,
                    Message: $"Id {id} did not resolve to an active row."));
            }
        }
        return matched;
    }

    /// <summary>
    /// Serialises the audit details payload. Kept as a static helper so unit
    /// tests can assert the exact JSON shape if a future audit-consumer
    /// regression demands it.
    /// </summary>
    /// <param name="code">Region or subdivision code being assigned.</param>
    /// <param name="rowsUpdated">Number of rows actually updated.</param>
    /// <param name="rowsMatched">Resolved row count before the equality check.</param>
    /// <param name="kind">Axis identifier (<c>Solicitant</c> / <c>ServiceApplication</c>).</param>
    /// <returns>UTF-8 JSON-encoded string.</returns>
    private static string SerializeDetails(string code, int rowsUpdated, int rowsMatched, string kind)
    {
        var payload = new
        {
            kind,
            code,
            rowsUpdated,
            rowsMatched,
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Maps the wire <see cref="QbeFilterDto"/> envelope onto the server-side
    /// <see cref="QbeFilter"/>. Mirrors the helper used in
    /// <c>DocumentServiceImpl</c>; unknown operator literals are folded to a
    /// sentinel value the converter rejects with a stable
    /// <see cref="ErrorCodes.QbeOperatorNotSupported"/> code.
    /// </summary>
    /// <param name="dto">Wire envelope.</param>
    /// <returns>Server-side filter.</returns>
    private static QbeFilter MapQbe(QbeFilterDto dto)
    {
        var conds = new List<QbeCondition>(dto.Conditions.Count);
        foreach (var c in dto.Conditions)
        {
            if (!Enum.TryParse<QbeOperator>(c.Operator, ignoreCase: false, out var op))
            {
                op = (QbeOperator)int.MinValue;
            }
            conds.Add(new QbeCondition(c.FieldName, op, c.Value, c.Value2));
        }
        return new QbeFilter(
            string.IsNullOrEmpty(dto.Combinator) ? QbeFilter.CombinatorAnd : dto.Combinator,
            conds);
    }
}
