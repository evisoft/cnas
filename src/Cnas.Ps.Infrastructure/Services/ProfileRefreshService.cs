using System.Globalization;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ContributorProfileUpdates;
using Cnas.Ps.Application.Contributors;
using Cnas.Ps.Application.External;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0363 / TOR UC13 strategy 3 — default <see cref="IProfileRefreshService"/>
/// implementation. Routes to the matching gateway, applies each delta, persists the
/// run row, and emits the Sensitive audit event.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source codes.</b> Stable SCREAMING_SNAKE_CASE: <c>RSP</c>, <c>RSUD</c>,
/// <c>SI_SFS</c>. Comparison is case-sensitive — the controller normalises the route
/// query string before forwarding.
/// </para>
/// <para>
/// <b>Per-delta failure handling.</b> Each delta is applied independently. A failed
/// apply increments <c>RowsSkipped</c> and contributes one line to
/// <c>FailureSummary</c> (cap at 5000 chars; further failures are truncated). The run
/// outcome is computed from the aggregate at the end:
/// <list type="bullet">
///   <item><c>Success</c> — all deltas applied, none skipped.</item>
///   <item><c>NoChange</c> — gateway returned zero deltas.</item>
///   <item><c>PartialFailure</c> — at least one applied and at least one skipped.</item>
///   <item><c>Failed</c> — none applied (and either the gateway failed or all deltas were rejected).</item>
/// </list>
/// </para>
/// </remarks>
/// <param name="db">DbContext abstraction.</param>
/// <param name="contributorService">Contributor-side writer used to apply each delta.</param>
/// <param name="rsp">RSP gateway.</param>
/// <param name="rsud">RSUD gateway.</param>
/// <param name="siSfs">SI SFS gateway.</param>
/// <param name="clock">UTC clock.</param>
/// <param name="sqids">Sqid encoder.</param>
/// <param name="caller">Caller context (audit attribution).</param>
/// <param name="audit">Audit sink.</param>
/// <param name="logger">Structured logger.</param>
public sealed class ProfileRefreshService(
    ICnasDbContext db,
    IContributorLinkedEntitiesService contributorService,
    IRspGateway rsp,
    IRsudGateway rsud,
    ISiSfsGateway siSfs,
    ICnasTimeProvider clock,
    ISqidService sqids,
    ICallerContext caller,
    IAuditService audit,
    ILogger<ProfileRefreshService> logger) : IProfileRefreshService
{
    private readonly ICnasDbContext _db = db;
    private readonly IContributorLinkedEntitiesService _contributorService = contributorService;
    private readonly IRspGateway _rsp = rsp;
    private readonly IRsudGateway _rsud = rsud;
    private readonly ISiSfsGateway _siSfs = siSfs;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly ILogger<ProfileRefreshService> _logger = logger;

    /// <summary>Source code: state population register.</summary>
    public const string SourceRsp = "RSP";

    /// <summary>Source code: legal-person register.</summary>
    public const string SourceRsud = "RSUD";

    /// <summary>Source code: state tax service.</summary>
    public const string SourceSiSfs = "SI_SFS";

    private const string EvtRefreshCompleted = "PROFILE.REFRESH.COMPLETED";
    private const int FailureSummaryCap = 5000;

    /// <inheritdoc />
    public async Task<Result<ProfileRefreshRunDto>> RefreshFromSourceAsync(
        string source, long contributorId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source != SourceRsp && source != SourceRsud && source != SourceSiSfs)
        {
            return Result<ProfileRefreshRunDto>.Failure(
                ErrorCodes.ProfileRefreshUnknownSource,
                $"Unknown profile-refresh source '{source}'.");
        }

        var contributor = await _db.InsuredPersons
            .FirstOrDefaultAsync(p => p.Id == contributorId && p.IsActive, ct)
            .ConfigureAwait(false);
        if (contributor is null)
        {
            return Result<ProfileRefreshRunDto>.Failure(
                ErrorCodes.NotFound, "Target contributor not found.");
        }

        var startedUtc = _clock.UtcNow;
        var deltasResult = await DispatchAsync(source, contributor.Idnp, ct).ConfigureAwait(false);

        if (deltasResult.IsFailure)
        {
            // Persist the failed run row so the operator dashboard has a trail.
            var failedRow = await PersistRunAsync(
                source: source,
                contributorId: contributor.Id,
                outcome: ProfileRefreshOutcome.Failed,
                rowsApplied: 0,
                rowsSkipped: 0,
                startedUtc: startedUtc,
                failureSummary: Truncate($"Gateway call failed: {deltasResult.ErrorMessage}"),
                ct: ct).ConfigureAwait(false);

            await EmitCompletedAuditAsync(
                source, contributor.Id, rowsApplied: 0, outcome: ProfileRefreshOutcome.Failed, ct)
                .ConfigureAwait(false);

            return Result<ProfileRefreshRunDto>.Success(ToDto(failedRow));
        }

        var deltas = deltasResult.Value;
        if (deltas.Count == 0)
        {
            var noChangeRow = await PersistRunAsync(
                source: source,
                contributorId: contributor.Id,
                outcome: ProfileRefreshOutcome.NoChange,
                rowsApplied: 0,
                rowsSkipped: 0,
                startedUtc: startedUtc,
                failureSummary: null,
                ct: ct).ConfigureAwait(false);

            await EmitCompletedAuditAsync(
                source, contributor.Id, rowsApplied: 0, outcome: ProfileRefreshOutcome.NoChange, ct)
                .ConfigureAwait(false);

            return Result<ProfileRefreshRunDto>.Success(ToDto(noChangeRow));
        }

        var applied = 0;
        var skipped = 0;
        var failureSummary = new StringBuilder();

        foreach (var delta in deltas)
        {
            var applyResult = await ApplyDeltaAsync(contributor.Id, source, delta, ct).ConfigureAwait(false);
            if (applyResult.IsSuccess)
            {
                applied++;
            }
            else
            {
                skipped++;
                AppendFailure(failureSummary, delta, applyResult.ErrorCode, applyResult.ErrorMessage);
            }
        }

        var outcome = applied switch
        {
            > 0 when skipped == 0 => ProfileRefreshOutcome.Success,
            > 0 when skipped > 0 => ProfileRefreshOutcome.PartialFailure,
            _ => ProfileRefreshOutcome.Failed,
        };

        var row = await PersistRunAsync(
            source: source,
            contributorId: contributor.Id,
            outcome: outcome,
            rowsApplied: applied,
            rowsSkipped: skipped,
            startedUtc: startedUtc,
            failureSummary: failureSummary.Length > 0 ? Truncate(failureSummary.ToString()) : null,
            ct: ct).ConfigureAwait(false);

        await EmitCompletedAuditAsync(source, contributor.Id, applied, outcome, ct).ConfigureAwait(false);

        return Result<ProfileRefreshRunDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ProfileRefreshRunDto>>> ListRecentAsync(int take, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(take, 1, 500);
        var rows = await _db.ProfileRefreshRuns
            .OrderByDescending(r => r.StartedUtc)
            .Take(clamped)
            .ToListAsync(ct).ConfigureAwait(false);
        IReadOnlyList<ProfileRefreshRunDto> dtos = rows.Select(ToDto).ToList();
        return Result<IReadOnlyList<ProfileRefreshRunDto>>.Success(dtos);
    }

    // ─── helpers ─────────────────────

    /// <summary>
    /// Routes the refresh call to the matching gateway. Source codes have already been
    /// validated by the caller; this helper exists so the dispatch is unit-testable in
    /// isolation.
    /// </summary>
    /// <param name="source">Validated source code.</param>
    /// <param name="idnp">Contributor IDNP.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> DispatchAsync(string source, string idnp, CancellationToken ct) =>
        source switch
        {
            SourceRsp => _rsp.FetchDeltasAsync(idnp, ct),
            SourceRsud => _rsud.FetchDeltasAsync(idnp, ct),
            SourceSiSfs => _siSfs.FetchDeltasAsync(idnp, ct),
            _ => Task.FromResult(Result<IReadOnlyList<ProfileRefreshDeltaDto>>.Failure(
                ErrorCodes.ProfileRefreshUnknownSource,
                $"Unknown profile-refresh source '{source}'.")),
        };

    /// <summary>
    /// Applies a single delta by deserialising its JSON payload into the matching
    /// <c>ContributorLinkedEntitiesService</c> input DTO and calling the writer.
    /// Failures (deserialisation, validation, no-op shortcut) bubble up as Result
    /// failures and the run loop counts them as skipped.
    /// </summary>
    /// <param name="contributorId">Contributor primary key.</param>
    /// <param name="source">Source code (audit attribution).</param>
    /// <param name="delta">The delta to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<Result> ApplyDeltaAsync(
        long contributorId, string source, ProfileRefreshDeltaDto delta, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var changeReason = $"External refresh from {source}";

        try
        {
            switch (delta.ChildEntityType)
            {
                case "Address":
                {
                    var dto = JsonSerializer.Deserialize<ContributorAddressInputDto>(delta.PayloadJson, ProfilePayloadSerialization.Options)
                        ?? throw new JsonException("Address payload was null.");
                    var res = await _contributorService.UpdateAddressAsync(
                        contributorId, dto, changeReason, ct).ConfigureAwait(false);
                    return res.IsSuccess
                        ? Result.Success()
                        : Result.Failure(res.ErrorCode!, res.ErrorMessage!);
                }
                case "Contact":
                {
                    var dto = JsonSerializer.Deserialize<ContributorContactInputDto>(delta.PayloadJson, ProfilePayloadSerialization.Options)
                        ?? throw new JsonException("Contact payload was null.");
                    var res = await _contributorService.UpdateContactAsync(
                        contributorId, dto, changeReason, ct).ConfigureAwait(false);
                    return res.IsSuccess
                        ? Result.Success()
                        : Result.Failure(res.ErrorCode!, res.ErrorMessage!);
                }
                case "CivilStatus":
                {
                    var dto = JsonSerializer.Deserialize<ContributorCivilStatusInputDto>(delta.PayloadJson, ProfilePayloadSerialization.Options)
                        ?? throw new JsonException("CivilStatus payload was null.");
                    var res = await _contributorService.UpdateCivilStatusAsync(
                        contributorId, dto, changeReason, ct).ConfigureAwait(false);
                    return res.IsSuccess
                        ? Result.Success()
                        : Result.Failure(res.ErrorCode!, res.ErrorMessage!);
                }
                case "Activity":
                {
                    var dto = JsonSerializer.Deserialize<ContributorActivityPeriodInputDto>(delta.PayloadJson, ProfilePayloadSerialization.Options)
                        ?? throw new JsonException("Activity payload was null.");
                    var res = await _contributorService.AddActivityPeriodAsync(
                        contributorId, dto, changeReason, ct).ConfigureAwait(false);
                    return res.IsSuccess
                        ? Result.Success()
                        : Result.Failure(res.ErrorCode!, res.ErrorMessage!);
                }
                case "SocialInsuranceContract":
                {
                    var dto = JsonSerializer.Deserialize<ContributorSocialInsuranceContractInputDto>(delta.PayloadJson, ProfilePayloadSerialization.Options)
                        ?? throw new JsonException("Contract payload was null.");
                    var res = await _contributorService.UpdateSocialInsuranceContractAsync(
                        contributorId, dto, changeReason, ct).ConfigureAwait(false);
                    return res.IsSuccess
                        ? Result.Success()
                        : Result.Failure(res.ErrorCode!, res.ErrorMessage!);
                }
                default:
                    return Result.Failure(
                        ErrorCodes.ProfileUpdateUnknownType,
                        $"Unknown ChildEntityType '{delta.ChildEntityType}'.");
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "ProfileRefresh delta JSON could not be deserialised for child {Child}.",
                delta.ChildEntityType);
            return Result.Failure(
                ErrorCodes.ProfileUpdateInvalidPayload,
                $"Failed to deserialise delta payload: {ex.Message}");
        }
    }

    /// <summary>
    /// Persists a finalised refresh run row in a single SaveChanges. Returns the row so
    /// the caller can map it to a DTO.
    /// </summary>
    /// <param name="source">Validated source code.</param>
    /// <param name="contributorId">Contributor primary key.</param>
    /// <param name="outcome">Outcome classification.</param>
    /// <param name="rowsApplied">Successful applies.</param>
    /// <param name="rowsSkipped">Skipped/failed deltas.</param>
    /// <param name="startedUtc">UTC instant the run started.</param>
    /// <param name="failureSummary">Truncated failure summary; null for success/no-change.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<ProfileRefreshRun> PersistRunAsync(
        string source,
        long contributorId,
        ProfileRefreshOutcome outcome,
        int rowsApplied,
        int rowsSkipped,
        DateTime startedUtc,
        string? failureSummary,
        CancellationToken ct)
    {
        var completedUtc = _clock.UtcNow;
        var row = new ProfileRefreshRun
        {
            Source = source,
            TargetContributorId = contributorId,
            Outcome = outcome,
            RowsApplied = rowsApplied,
            RowsSkipped = rowsSkipped,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            FailureSummary = failureSummary,
            CreatedAtUtc = completedUtc,
            CreatedBy = _caller.UserSqid ?? "system",
            IsActive = true,
        };
        _db.ProfileRefreshRuns.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Emits the Sensitive <c>PROFILE.REFRESH.COMPLETED</c> audit row carrying
    /// <c>{ source, contributorSqid, rowsApplied, outcome }</c>.
    /// </summary>
    /// <param name="source">Source code.</param>
    /// <param name="contributorId">Contributor primary key.</param>
    /// <param name="rowsApplied">Number of deltas applied.</param>
    /// <param name="outcome">Outcome classification.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task EmitCompletedAuditAsync(
        string source, long contributorId, int rowsApplied, ProfileRefreshOutcome outcome, CancellationToken ct)
    {
        var detail = JsonSerializer.Serialize(new
        {
            source,
            contributorSqid = _sqids.Encode(contributorId),
            rowsApplied,
            outcome = outcome.ToString(),
        });
        return _audit.RecordAsync(
            EvtRefreshCompleted, AuditSeverity.Sensitive, _caller.UserSqid ?? "system",
            "ProfileRefreshRun", contributorId, detail,
            _caller.SourceIp, _caller.CorrelationId, ct);
    }

    /// <summary>
    /// Appends one failure line to the running summary buffer. Stops appending once
    /// the buffer crosses the cap so the truncation logic is monotonic.
    /// </summary>
    /// <param name="buffer">Running summary builder.</param>
    /// <param name="delta">The delta whose apply failed.</param>
    /// <param name="errorCode">Stable error code.</param>
    /// <param name="errorMessage">Human-readable detail.</param>
    private static void AppendFailure(
        StringBuilder buffer, ProfileRefreshDeltaDto delta, string? errorCode, string? errorMessage)
    {
        if (buffer.Length > FailureSummaryCap)
        {
            return;
        }
        if (buffer.Length > 0)
        {
            buffer.Append("; ");
        }
        buffer.AppendFormat(CultureInfo.InvariantCulture,
            "{0}/{1}: {2} {3}",
            delta.ChildEntityType, delta.FieldName,
            errorCode ?? "?", errorMessage ?? "");
    }

    /// <summary>Truncates <paramref name="value"/> to <see cref="FailureSummaryCap"/> chars.</summary>
    /// <param name="value">Free-form failure summary.</param>
    private static string? Truncate(string? value)
    {
        if (value is null)
        {
            return null;
        }
        return value.Length <= FailureSummaryCap
            ? value
            : string.Concat(value.AsSpan(0, FailureSummaryCap - 3), "...");
    }

    /// <summary>Maps a <see cref="ProfileRefreshRun"/> entity to its output DTO.</summary>
    /// <param name="row">The entity row.</param>
    private ProfileRefreshRunDto ToDto(ProfileRefreshRun row) => new(
        Id: _sqids.Encode(row.Id),
        Source: row.Source,
        TargetContributorSqid: row.TargetContributorId is long id ? _sqids.Encode(id) : null,
        Outcome: row.Outcome.ToString(),
        RowsApplied: row.RowsApplied,
        RowsSkipped: row.RowsSkipped,
        StartedUtc: row.StartedUtc,
        CompletedUtc: row.CompletedUtc,
        FailureSummary: row.FailureSummary);
}
