using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Application.Prefill;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Prefill;

/// <summary>
/// R0552 / R0562 / TOR CF 06.03 + CF 07.03 — default <see cref="IPrefillService"/>
/// implementation. Fans out to RSP / RSUD / SI SFS in parallel, merges per-field
/// candidates by source priority, and surfaces conflicts + per-gateway failures as
/// Warnings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Adapter cast.</b> The three injected gateway interfaces
/// (<see cref="IRspGateway"/> / <see cref="IRsudGateway"/> / <see cref="ISiSfsGateway"/>)
/// govern the existing R0363 profile-refresh pipeline. Pre-fill needs the same upstream
/// data shaped as a field-name dictionary, so each gateway implementation also
/// implements <see cref="IPrefillSourceAdapter"/>. The service casts on the way in; an
/// implementation that fails the cast falls back to "no data" with a Warning rather
/// than crashing the citizen form. Today's <c>MockRsp/Rsud/SiSfsGateway</c> double-
/// implement both contracts.
/// </para>
/// <para>
/// <b>Per-gateway timeout.</b> Each fanout call is wrapped in a 5-second linked
/// <see cref="CancellationTokenSource"/> so a slow upstream cannot stall the whole
/// response. A timeout / network failure converts to a Warning + zero contribution;
/// it does NOT fail the entire call. The caller's CT is honored — cancelling the
/// outer call cancels every fanout in flight.
/// </para>
/// <para>
/// <b>Merge.</b> Per field we collect <c>(source, value)</c> candidates from each
/// adapter (filtered through the per-source allow-list); we sort by
/// <c>PrefillSourcePriority</c> DESC and pick the head. Conflicts (≥ 2 distinct
/// candidate values for one field) emit a Warning of the form
/// <c>"FieldX: RSP=Y, RSUD=Z — RSP used"</c>.
/// </para>
/// <para>
/// <b>PII discipline.</b> The audit row carries the solicitant Sqid, the source list,
/// and the field count — never the field values themselves. Logs use the same
/// discipline. The Warning strings DO carry both the kept and the discarded values
/// because conflict diagnosis is impossible without them; this is the only place in
/// the pipeline where PII leaves the field map.
/// </para>
/// </remarks>
/// <param name="db">DbContext abstraction.</param>
/// <param name="rsp">RSP gateway (also exposes <see cref="IPrefillSourceAdapter"/>).</param>
/// <param name="rsud">RSUD gateway (also exposes <see cref="IPrefillSourceAdapter"/>).</param>
/// <param name="siSfs">SI SFS gateway (also exposes <see cref="IPrefillSourceAdapter"/>).</param>
/// <param name="clock">UTC clock used to stamp the response and audit row.</param>
/// <param name="sqids">Sqid encoder for the response payload.</param>
/// <param name="caller">Caller context (audit attribution + permission gate).</param>
/// <param name="audit">Audit sink.</param>
/// <param name="logger">Structured logger.</param>
public sealed class PrefillService(
    ICnasDbContext db,
    IRspGateway rsp,
    IRsudGateway rsud,
    ISiSfsGateway siSfs,
    ICnasTimeProvider clock,
    ISqidService sqids,
    ICallerContext caller,
    IAuditService audit,
    ILogger<PrefillService> logger) : IPrefillService
{
    private readonly ICnasDbContext _db = db;
    private readonly IRspGateway _rsp = rsp;
    private readonly IRsudGateway _rsud = rsud;
    private readonly ISiSfsGateway _siSfs = siSfs;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly ILogger<PrefillService> _logger = logger;

    /// <summary>Stable audit event code emitted on every successful pre-fill.</summary>
    public const string AuditEventCode = "PREFILL.RETRIEVED";

    /// <summary>Per-gateway timeout — beyond this the source contributes a Warning, no data.</summary>
    public static readonly TimeSpan PerGatewayTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<Result<PrefillPayloadDto>> PrefillForCurrentUserAsync(
        PrefillRequestDto request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Defense in depth — controller's [Authorize] is the primary check, but
        // internal callers could bypass it (e.g. background jobs that wire the
        // service in).
        if (_caller.UserId is not long userId)
        {
            return Result<PrefillPayloadDto>.Failure(
                ErrorCodes.Unauthorized,
                "Pre-fill requires an authenticated caller.");
        }

        // Resolve the caller's Solicitant via the canonical UserProfile → Solicitant
        // identity link (matched on the deterministic NationalIdHash shadow column).
        // Mirrors PersonalAccountExtractService.GetForCurrentUserAsync.
        var nationalIdHash = await _db.UserProfiles
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => u.NationalIdHash)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(nationalIdHash))
        {
            return Result<PrefillPayloadDto>.Failure(
                ErrorCodes.NotFound,
                "No Solicitant is linked to the calling user.");
        }

        var solicitant = await _db.Solicitants
            .Where(s => s.NationalIdHash == nationalIdHash && s.IsActive)
            .Select(s => new { s.Id, s.NationalId })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (solicitant is null)
        {
            return Result<PrefillPayloadDto>.Failure(
                ErrorCodes.NotFound,
                "No Solicitant is linked to the calling user.");
        }

        return await BuildPayloadAsync(solicitant.Id, solicitant.NationalId, request, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<PrefillPayloadDto>> PrefillForSolicitantAsync(
        long solicitantId,
        PrefillRequestDto request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Permission gate — only callers carrying the explicit ForAnyApplicant
        // permission may pull arbitrary citizen pre-fill data.
        if (!_caller.Roles.Contains(IPrefillService.ForAnyApplicantPermission, StringComparer.Ordinal))
        {
            return Result<PrefillPayloadDto>.Failure(
                ErrorCodes.Forbidden,
                $"Permission '{IPrefillService.ForAnyApplicantPermission}' is required to pre-fill on behalf of another citizen.");
        }

        var solicitant = await _db.Solicitants
            .Where(s => s.Id == solicitantId && s.IsActive)
            .Select(s => new { s.Id, s.NationalId })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (solicitant is null)
        {
            return Result<PrefillPayloadDto>.Failure(
                ErrorCodes.NotFound,
                "Target Solicitant not found.");
        }

        return await BuildPayloadAsync(solicitant.Id, solicitant.NationalId, request, ct).ConfigureAwait(false);
    }

    // ─── core merge pipeline ─────────────────────

    /// <summary>
    /// Fans out to every requested source, applies the per-source allow-list, merges
    /// candidates by priority, writes the audit row, and assembles the response DTO.
    /// </summary>
    /// <param name="solicitantId">Raw bigint id of the target Solicitant.</param>
    /// <param name="idnp">IDNP forwarded to each gateway as the lookup key.</param>
    /// <param name="request">Caller-supplied source / field allow-list (defaults applied here).</param>
    /// <param name="ct">Standard cancellation token (propagates to every fanout).</param>
    private async Task<Result<PrefillPayloadDto>> BuildPayloadAsync(
        long solicitantId,
        string idnp,
        PrefillRequestDto request,
        CancellationToken ct)
    {
        // 1. Resolve defaults.
        FrozenSet<string> requestedSources = ResolveSources(request.Sources);
        FrozenSet<string>? requestedFields = ResolveFieldFilter(request.Fields);

        // 2. Fan out to the requested sources in parallel. Each call gets its own
        //    per-gateway timeout linked off the caller's CT so the slow source
        //    cannot stall the whole response.
        var warnings = new List<string>();
        var perSourceResults = await FanoutAsync(idnp, requestedSources, warnings, ct).ConfigureAwait(false);

        // 3. Merge candidates per field, honoring the source allow-list AND the
        //    field allow-list (when supplied).
        var (fields, sourceUsedPerField) = MergeCandidates(
            perSourceResults, requestedFields, warnings);

        // 4. Emit the Sensitive audit row — sources queried + field count, no
        //    PII values.
        var solicitantSqid = _sqids.Encode(solicitantId);
        await EmitAuditAsync(solicitantId, solicitantSqid, requestedSources, fields.Count, ct).ConfigureAwait(false);

        var payload = new PrefillPayloadDto(
            SolicitantSqid: solicitantSqid,
            Fields: fields,
            Warnings: warnings,
            GeneratedAtUtc: _clock.UtcNow,
            SourceUsedPerField: sourceUsedPerField);
        return Result<PrefillPayloadDto>.Success(payload);
    }

    /// <summary>
    /// Returns the source allow-list to query. A null / empty supplied list defaults
    /// to "all three"; otherwise we filter the supplied list through the known set
    /// (the validator already rejects unknowns, but this is defense-in-depth so
    /// internal callers cannot widen the surface).
    /// </summary>
    /// <param name="supplied">Caller-supplied source codes.</param>
    private static FrozenSet<string> ResolveSources(IReadOnlyList<string>? supplied)
    {
        if (supplied is null || supplied.Count == 0)
        {
            return PrefillSources.All;
        }
        return supplied
            .Where(s => PrefillSources.All.Contains(s))
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the field allow-list to keep, or <c>null</c> when no filter is in
    /// effect (the caller wants every field the queried sources are willing to
    /// give).
    /// </summary>
    /// <param name="supplied">Caller-supplied field names.</param>
    private static FrozenSet<string>? ResolveFieldFilter(IReadOnlyList<string>? supplied)
    {
        if (supplied is null || supplied.Count == 0)
        {
            return null;
        }
        return supplied
            .Where(f => PrefillFields.All.Contains(f))
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Calls every queried gateway in parallel, respecting per-gateway timeouts
    /// and converting failures to Warnings. Returns one row per source with the
    /// retrieval timestamp captured at completion-of-call time.
    /// </summary>
    /// <param name="idnp">Lookup key passed to each gateway.</param>
    /// <param name="requestedSources">Sources to query (already defaulted).</param>
    /// <param name="warnings">Warning sink — failure messages are appended here.</param>
    /// <param name="ct">Caller's cancellation token (propagated to every fanout).</param>
    private async Task<IReadOnlyList<SourceFetchResult>> FanoutAsync(
        string idnp,
        FrozenSet<string> requestedSources,
        List<string> warnings,
        CancellationToken ct)
    {
        var tasks = new List<Task<SourceFetchResult>>(requestedSources.Count);
        foreach (var source in requestedSources)
        {
            if (TryGetAdapter(source, out var adapter))
            {
                tasks.Add(InvokeAdapterAsync(adapter, idnp, ct));
            }
            else
            {
                // The injected gateway didn't double-implement IPrefillSourceAdapter
                // — possible during transitional periods when only one of the three
                // production gateways has been upgraded.
                warnings.Add($"{source}: adapter not available — source skipped.");
            }
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var r in results)
        {
            if (r.FailureWarning is not null)
            {
                warnings.Add(r.FailureWarning);
            }
        }
        return results;
    }

    /// <summary>
    /// Wraps the adapter call in a per-gateway timeout. Returns a result row that
    /// is either successful (values populated) or carries a Warning string.
    /// </summary>
    /// <param name="adapter">The source adapter to call.</param>
    /// <param name="idnp">Lookup key.</param>
    /// <param name="ct">Caller's CT (linked with the per-gateway timeout CT).</param>
    private async Task<SourceFetchResult> InvokeAdapterAsync(
        IPrefillSourceAdapter adapter, string idnp, CancellationToken ct)
    {
        using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perCallCts.CancelAfter(PerGatewayTimeout);
        try
        {
            var values = await adapter.FetchPrefillAsync(idnp, perCallCts.Token).ConfigureAwait(false);
            return new SourceFetchResult(
                SourceCode: adapter.SourceCode,
                Values: values,
                RetrievedAtUtc: _clock.UtcNow,
                FailureWarning: null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Per-gateway timeout fired (the linked CTS, not the caller's CT).
            _logger.LogWarning("Pre-fill gateway {Source} timed out after {Timeout}s.",
                adapter.SourceCode, PerGatewayTimeout.TotalSeconds);
            return new SourceFetchResult(
                SourceCode: adapter.SourceCode,
                Values: ImmutableEmpty,
                RetrievedAtUtc: _clock.UtcNow,
                FailureWarning: $"{adapter.SourceCode}: gateway timed out after {PerGatewayTimeout.TotalSeconds:F0}s — source skipped.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SourceFetchResult(
                SourceCode: adapter.SourceCode,
                Values: ImmutableEmpty,
                RetrievedAtUtc: _clock.UtcNow,
                FailureWarning: $"{adapter.SourceCode}: gateway call cancelled — source skipped.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pre-fill gateway {Source} failed with {ExceptionType}.",
                adapter.SourceCode, ex.GetType().Name);
            return new SourceFetchResult(
                SourceCode: adapter.SourceCode,
                Values: ImmutableEmpty,
                RetrievedAtUtc: _clock.UtcNow,
                FailureWarning: $"{adapter.SourceCode}: gateway error ({ex.GetType().Name}) — source skipped.");
        }
    }

    /// <summary>
    /// Resolves the matching gateway and casts it to <see cref="IPrefillSourceAdapter"/>.
    /// Returns false when the cast fails (no pre-fill support on that gateway).
    /// </summary>
    /// <param name="source">Stable source code.</param>
    /// <param name="adapter">Receives the adapter on success; null otherwise.</param>
    private bool TryGetAdapter(string source, [NotNullWhen(true)] out IPrefillSourceAdapter? adapter)
    {
        object gateway = source switch
        {
            PrefillSources.Rsp => _rsp,
            PrefillSources.Rsud => _rsud,
            PrefillSources.SiSfs => _siSfs,
            _ => null!,
        };
        if (gateway is IPrefillSourceAdapter ad)
        {
            adapter = ad;
            return true;
        }
        adapter = null;
        return false;
    }

    /// <summary>
    /// Merges per-source dictionaries into a single field-name → winning-value map.
    /// Applies the per-source allow-list (silently drops fields the source isn't
    /// permitted to answer for), the caller-supplied field filter (when present),
    /// and the priority-based conflict resolution (writing one Warning per
    /// conflict).
    /// </summary>
    /// <param name="perSourceResults">One row per queried source (in any order).</param>
    /// <param name="requestedFields">Caller field filter; null = no filter.</param>
    /// <param name="warnings">Warning sink (mutated in-place).</param>
    /// <returns>(field map, source-used map).</returns>
    private static (IReadOnlyDictionary<string, PrefillFieldDto> Fields, IReadOnlyDictionary<string, string> SourceUsed)
        MergeCandidates(
            IReadOnlyList<SourceFetchResult> perSourceResults,
            FrozenSet<string>? requestedFields,
            List<string> warnings)
    {
        // candidates: fieldName → list of (source, value, retrievedAtUtc).
        var candidates = new Dictionary<string, List<(string Source, string Value, DateTime RetrievedAtUtc)>>(StringComparer.Ordinal);

        // First pass — collect candidates. Apply per-source allow-list AND
        // requested-field filter; track allow-list misses for the Warning surface.
        foreach (var result in perSourceResults)
        {
            var sourceAllow = PrefillSourceAllowList.For(result.SourceCode);
            foreach (var kvp in result.Values)
            {
                var fieldName = kvp.Key;
                var value = kvp.Value;

                // Drop unknown fields silently (defensive — bad data in production).
                if (!PrefillFields.All.Contains(fieldName))
                {
                    continue;
                }

                // Apply per-source allow-list — emit a Warning when the source
                // returns a value for a field it isn't supposed to govern, BUT
                // only when the caller explicitly asked for that field (so the
                // "give me everything" case doesn't generate spurious warnings).
                if (!sourceAllow.Contains(fieldName))
                {
                    if (requestedFields is not null && requestedFields.Contains(fieldName))
                    {
                        warnings.Add(string.Create(CultureInfo.InvariantCulture,
                            $"{fieldName}: source {result.SourceCode} does not govern this field — value skipped."));
                    }
                    continue;
                }

                // Apply caller-supplied field filter.
                if (requestedFields is not null && !requestedFields.Contains(fieldName))
                {
                    continue;
                }

                if (!candidates.TryGetValue(fieldName, out var list))
                {
                    list = new List<(string, string, DateTime)>();
                    candidates[fieldName] = list;
                }
                list.Add((result.SourceCode, value, result.RetrievedAtUtc));
            }
        }

        // Second pass — pick the winner per field, emit Warnings on conflicts.
        var fields = new Dictionary<string, PrefillFieldDto>(StringComparer.Ordinal);
        var sourceUsed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (fieldName, candidateList) in candidates)
        {
            // Distinct values only — same-value-different-sources is not a
            // conflict (both sources agreed). Sort by priority DESC and pick the head.
            var sorted = candidateList
                .OrderByDescending(c => PrefillSourcePriority.For(c.Source))
                .ToList();
            var winner = sorted[0];

            fields[fieldName] = new PrefillFieldDto(
                Value: winner.Value,
                Source: winner.Source,
                RetrievedAtUtc: winner.RetrievedAtUtc);
            sourceUsed[fieldName] = winner.Source;

            // Conflict detection — at least one other candidate had a DIFFERENT value.
            var conflictingLosers = sorted
                .Skip(1)
                .Where(c => !string.Equals(c.Value, winner.Value, StringComparison.Ordinal))
                .ToList();
            if (conflictingLosers.Count > 0)
            {
                var loserDescr = string.Join(", ",
                    conflictingLosers.Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Source}={l.Value}")));
                warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{fieldName}: {winner.Source}={winner.Value}, {loserDescr} — {winner.Source} used."));
            }
        }
        return (fields, sourceUsed);
    }

    /// <summary>
    /// Writes the Sensitive <c>PREFILL.RETRIEVED</c> audit row. Carries the
    /// solicitant Sqid, the queried-source list, and the field count — never the
    /// field values (PII).
    /// </summary>
    /// <param name="solicitantId">Target Solicitant primary key.</param>
    /// <param name="solicitantSqid">Pre-computed Sqid for the same id.</param>
    /// <param name="sourcesUsed">Source codes actually queried.</param>
    /// <param name="fieldCount">Number of fields the response will carry.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task EmitAuditAsync(
        long solicitantId, string solicitantSqid, FrozenSet<string> sourcesUsed, int fieldCount, CancellationToken ct)
    {
        var detail = JsonSerializer.Serialize(new
        {
            solicitantSqid,
            sourcesUsed = sourcesUsed.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            fieldCount,
        });
        return _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Sensitive,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(Solicitant),
            targetEntityId: solicitantId,
            detailsJson: detail,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct);
    }

    /// <summary>
    /// Shared empty dictionary instance used by failed gateway returns — avoids
    /// allocating a fresh empty dictionary per failed call.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ImmutableEmpty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Per-source fanout outcome. Either holds the retrieved values (success) or a
    /// non-null <see cref="FailureWarning"/> describing why no values were retrieved.
    /// </summary>
    /// <param name="SourceCode">Stable source code.</param>
    /// <param name="Values">Returned field-name → value dictionary (empty on failure).</param>
    /// <param name="RetrievedAtUtc">Completion-of-call timestamp.</param>
    /// <param name="FailureWarning">Optional Warning string to surface; null on success.</param>
    private sealed record SourceFetchResult(
        string SourceCode,
        IReadOnlyDictionary<string, string> Values,
        DateTime RetrievedAtUtc,
        string? FailureWarning);
}
