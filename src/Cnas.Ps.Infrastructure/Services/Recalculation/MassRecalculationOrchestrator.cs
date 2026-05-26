using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Recalculation;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Recalculation;

/// <summary>
/// R1503 / TOR §3.7-D — internal orchestrator that drives one mass-
/// recalculation pass for a given <see cref="LegalChangeEvent"/>. The public
/// <see cref="MassRecalculationService"/> delegates the heavy lifting here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strategy dispatch.</b> The orchestrator partitions the event's
/// <see cref="LegalChangeEvent.BenefitTypesInScope"/> snapshot into kinds
/// that have a registered <see cref="IBenefitRecalculationStrategy"/> and
/// kinds that don't. Unsupported kinds are tagged
/// <see cref="RecalculationResultStatus.Skipped"/> with reason
/// <see cref="ErrorCodes.NoStrategyRegistered"/>; supported kinds are
/// dispatched to their strategy's <c>EnumerateInScopeDecisionIdsAsync</c>
/// followed by per-decision <c>RecomputeAsync</c>.
/// </para>
/// <para>
/// <b>No-PII boundary.</b> The orchestrator persists exactly what the
/// strategy returned via <see cref="BenefitRecalculationOutcome"/>. The
/// strategy is responsible for redacting plaintext identifiers from
/// <see cref="BenefitRecalculationOutcome.Reason"/> and
/// <see cref="BenefitRecalculationOutcome.RecalculationContextJson"/>.
/// </para>
/// </remarks>
public sealed class MassRecalculationOrchestrator
{
    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _readDb;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IEnumerable<IBenefitRecalculationStrategy> _strategies;
    private readonly ILogger<MassRecalculationOrchestrator> _logger;

    /// <summary>Constructs the orchestrator.</summary>
    /// <param name="db">Writer context — used to persist run + result rows.</param>
    /// <param name="readDb">Read-replica context handed to each strategy invocation.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="caller">Authenticated-caller context (used for audit attribution on result rows).</param>
    /// <param name="strategies">Registered per-benefit-kind strategies.</param>
    /// <param name="logger">Structured logger.</param>
    public MassRecalculationOrchestrator(
        ICnasDbContext db,
        IReadOnlyCnasDbContext readDb,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IEnumerable<IBenefitRecalculationStrategy> strategies,
        ILogger<MassRecalculationOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(readDb);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _readDb = readDb;
        _clock = clock;
        _caller = caller;
        _strategies = strategies;
        _logger = logger;
    }

    /// <summary>
    /// Executes one orchestrator pass against the supplied legal-change event.
    /// Creates the <see cref="RecalculationRun"/> row, dispatches strategies,
    /// persists per-decision result rows, finalises the run, and returns the
    /// persisted row.
    /// </summary>
    /// <param name="evt">The legal-change event driving the run.</param>
    /// <param name="mode">DryRun or Apply (Apply additionally writes amounts back).</param>
    /// <param name="trigger">Origin of the run.</param>
    /// <param name="actor">Audit-attribution identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted run row.</returns>
    public async Task<RecalculationRun> ExecuteAsync(
        LegalChangeEvent evt,
        RecalculationMode mode,
        RecalculationTriggerKind trigger,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var now = _clock.UtcNow;
        var run = new RecalculationRun
        {
            LegalChangeEventId = evt.Id,
            TriggerKind = trigger,
            Mode = mode,
            Status = RecalculationRunStatus.Running,
            StartedAt = now,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.RecalculationRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.MassRecalculationRunStarted.Add(1,
            new KeyValuePair<string, object?>("mode", mode.ToString()));

        long scanned = 0L;
        long recalculated = 0L;
        long skipped = 0L;
        long failed = 0L;
        decimal totalDelta = 0m;

        try
        {
            // Build a strategy lookup keyed by stable BenefitType enum-name.
            // A duplicate strategy registration is a startup-time programming
            // error; we surface it as RunStatus=Failed so the operator sees
            // the issue rather than silently picking the first registration.
            var strategyByType = new Dictionary<string, IBenefitRecalculationStrategy>(StringComparer.Ordinal);
            foreach (var s in _strategies)
            {
                if (!strategyByType.TryAdd(s.BenefitType, s))
                {
                    throw new InvalidOperationException(
                        $"Multiple IBenefitRecalculationStrategy registrations for BenefitType '{s.BenefitType}'.");
                }
            }

            foreach (var benefitType in evt.BenefitTypesInScope)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!strategyByType.TryGetValue(benefitType, out var strategy))
                {
                    // No strategy — there's nothing to enumerate. The
                    // orchestrator still emits a single Skipped marker row
                    // per benefit kind (BenefitDecisionId=0) so operators can
                    // see WHICH kinds were not covered.
                    var marker = new RecalculationDecisionResult
                    {
                        RunId = run.Id,
                        BenefitDecisionId = 0L,
                        BenefitType = benefitType,
                        BeneficiaryIdnpHash = string.Empty,
                        OldAmountMdl = 0m,
                        NewAmountMdl = 0m,
                        DeltaMdl = 0m,
                        Status = RecalculationResultStatus.Skipped,
                        Reason = ErrorCodes.NoStrategyRegistered,
                        CreatedAtUtc = _clock.UtcNow,
                        CreatedBy = actor,
                        IsActive = true,
                    };
                    _db.RecalculationDecisionResults.Add(marker);
                    skipped += 1;
                    scanned += 1;
                    CnasMeter.MassRecalculationDecisionProcessed.Add(1,
                        new KeyValuePair<string, object?>("mode", mode.ToString()),
                        new KeyValuePair<string, object?>("status", nameof(RecalculationResultStatus.Skipped)));
                    continue;
                }

                IReadOnlyList<long> decisionIds;
                try
                {
                    decisionIds = await strategy
                        .EnumerateInScopeDecisionIdsAsync(evt, _readDb, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "Mass-recalc enumeration failed for benefitType={BenefitType}, runId={RunId}.",
                        benefitType, run.Id);
                    var errRow = new RecalculationDecisionResult
                    {
                        RunId = run.Id,
                        BenefitDecisionId = 0L,
                        BenefitType = benefitType,
                        BeneficiaryIdnpHash = string.Empty,
                        OldAmountMdl = 0m,
                        NewAmountMdl = 0m,
                        DeltaMdl = 0m,
                        Status = RecalculationResultStatus.Failed,
                        Reason = $"{ex.GetType().Name}: {ex.Message}",
                        CreatedAtUtc = _clock.UtcNow,
                        CreatedBy = actor,
                        IsActive = true,
                    };
                    _db.RecalculationDecisionResults.Add(errRow);
                    failed += 1;
                    scanned += 1;
                    CnasMeter.MassRecalculationDecisionProcessed.Add(1,
                        new KeyValuePair<string, object?>("mode", mode.ToString()),
                        new KeyValuePair<string, object?>("status", nameof(RecalculationResultStatus.Failed)));
                    continue;
                }

                foreach (var decisionId in decisionIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanned += 1;

                    BenefitRecalculationOutcome outcome;
                    try
                    {
                        outcome = await strategy
                            .RecomputeAsync(decisionId, evt, _readDb, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex,
                            "Mass-recalc strategy.RecomputeAsync threw for benefitType={BenefitType}, decisionId={DecisionId}, runId={RunId}.",
                            benefitType, decisionId, run.Id);
                        _db.RecalculationDecisionResults.Add(new RecalculationDecisionResult
                        {
                            RunId = run.Id,
                            BenefitDecisionId = decisionId,
                            BenefitType = benefitType,
                            BeneficiaryIdnpHash = string.Empty,
                            OldAmountMdl = 0m,
                            NewAmountMdl = 0m,
                            DeltaMdl = 0m,
                            Status = RecalculationResultStatus.Failed,
                            Reason = $"{ex.GetType().Name}: {ex.Message}",
                            CreatedAtUtc = _clock.UtcNow,
                            CreatedBy = actor,
                            IsActive = true,
                        });
                        failed += 1;
                        CnasMeter.MassRecalculationDecisionProcessed.Add(1,
                            new KeyValuePair<string, object?>("mode", mode.ToString()),
                            new KeyValuePair<string, object?>("status", nameof(RecalculationResultStatus.Failed)));
                        continue;
                    }

                    var delta = outcome.NewAmountMdl - outcome.OldAmountMdl;
                    var status = outcome.Status switch
                    {
                        RecalculationResultStatus.Computed => RecalculationResultStatus.Computed,
                        RecalculationResultStatus.Skipped => RecalculationResultStatus.Skipped,
                        RecalculationResultStatus.Failed => RecalculationResultStatus.Failed,
                        // The orchestrator NEVER stamps Applied/Rejected from a Recompute outcome.
                        // A strategy returning either is reclassified to Computed (a no-op safety).
                        _ => RecalculationResultStatus.Computed,
                    };

                    _db.RecalculationDecisionResults.Add(new RecalculationDecisionResult
                    {
                        RunId = run.Id,
                        BenefitDecisionId = decisionId,
                        BenefitType = benefitType,
                        BeneficiaryIdnpHash = outcome.BeneficiaryIdnpHash,
                        OldAmountMdl = outcome.OldAmountMdl,
                        NewAmountMdl = outcome.NewAmountMdl,
                        DeltaMdl = delta,
                        Status = status,
                        Reason = outcome.Reason,
                        RecalculationContextJson = outcome.RecalculationContextJson,
                        CreatedAtUtc = _clock.UtcNow,
                        CreatedBy = actor,
                        IsActive = true,
                    });

                    switch (status)
                    {
                        case RecalculationResultStatus.Computed:
                            recalculated += 1;
                            totalDelta += delta;
                            break;
                        case RecalculationResultStatus.Skipped:
                            skipped += 1;
                            break;
                        case RecalculationResultStatus.Failed:
                            failed += 1;
                            break;
                    }

                    CnasMeter.MassRecalculationDecisionProcessed.Add(1,
                        new KeyValuePair<string, object?>("mode", mode.ToString()),
                        new KeyValuePair<string, object?>("status", status.ToString()));
                }
            }

            var completion = _clock.UtcNow;
            run.Status = RecalculationRunStatus.Completed;
            run.CompletedAt = completion;
            run.TotalDecisionsScanned = scanned;
            run.TotalDecisionsRecalculated = recalculated;
            run.TotalSkipped = skipped;
            run.TotalFailed = failed;
            run.TotalDeltaMdl = totalDelta;
            run.UpdatedAtUtc = completion;
            run.UpdatedBy = actor;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            CnasMeter.MassRecalculationRunCompleted.Add(1,
                new KeyValuePair<string, object?>("mode", mode.ToString()),
                new KeyValuePair<string, object?>("status", nameof(RecalculationRunStatus.Completed)));

            return run;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Mass-recalc orchestrator aborted for runId={RunId}, eventId={EventId}.",
                run.Id, evt.Id);
            var completion = _clock.UtcNow;
            run.Status = RecalculationRunStatus.Failed;
            run.CompletedAt = completion;
            run.TotalDecisionsScanned = scanned;
            run.TotalDecisionsRecalculated = recalculated;
            run.TotalSkipped = skipped;
            run.TotalFailed = failed;
            run.TotalDeltaMdl = totalDelta;
            run.FailureReason = $"{ex.GetType().Name}: {ex.Message}";
            run.UpdatedAtUtc = completion;
            run.UpdatedBy = actor;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            CnasMeter.MassRecalculationRunCompleted.Add(1,
                new KeyValuePair<string, object?>("mode", mode.ToString()),
                new KeyValuePair<string, object?>("status", nameof(RecalculationRunStatus.Failed)));

            return run;
        }
    }

    /// <summary>
    /// Calls <c>strategy.ApplyAsync</c> on every Computed result tied to the
    /// supplied run. Rows whose strategy is missing transition to Skipped;
    /// rows that throw transition to Failed; successful rows transition to
    /// Applied with <c>AppliedAt</c> stamped.
    /// </summary>
    /// <param name="run">The run carrying the Computed result rows.</param>
    /// <param name="actor">Audit-attribution identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mutated run row (counters re-snapshotted).</returns>
    public async Task<RecalculationRun> ApplyApprovedAsync(
        RecalculationRun run,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var strategyByType = new Dictionary<string, IBenefitRecalculationStrategy>(StringComparer.Ordinal);
        foreach (var s in _strategies)
        {
            strategyByType[s.BenefitType] = s;
        }

        var computed = await ListComputedRowsAsync(run.Id, cancellationToken).ConfigureAwait(false);

        foreach (var row in computed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!strategyByType.TryGetValue(row.BenefitType, out var strategy))
            {
                row.Status = RecalculationResultStatus.Skipped;
                row.Reason = ErrorCodes.NoStrategyRegistered;
                row.UpdatedAtUtc = _clock.UtcNow;
                row.UpdatedBy = actor;
                continue;
            }

            try
            {
                var applyResult = await strategy.ApplyAsync(row, _db, cancellationToken).ConfigureAwait(false);
                if (applyResult.IsSuccess)
                {
                    row.Status = RecalculationResultStatus.Applied;
                    row.AppliedAt = _clock.UtcNow;
                    row.UpdatedAtUtc = _clock.UtcNow;
                    row.UpdatedBy = actor;
                }
                else
                {
                    row.Status = RecalculationResultStatus.Failed;
                    row.Reason = applyResult.ErrorCode + ": " + applyResult.ErrorMessage;
                    row.UpdatedAtUtc = _clock.UtcNow;
                    row.UpdatedBy = actor;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Mass-recalc strategy.ApplyAsync threw for resultId={ResultId}, runId={RunId}.",
                    row.Id, run.Id);
                row.Status = RecalculationResultStatus.Failed;
                row.Reason = $"{ex.GetType().Name}: {ex.Message}";
                row.UpdatedAtUtc = _clock.UtcNow;
                row.UpdatedBy = actor;
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Recount the totals from the fresh row state.
        await RecountAsync(run, actor, cancellationToken).ConfigureAwait(false);
        return run;
    }

    /// <summary>Returns every <see cref="RecalculationResultStatus.Computed"/> row tracked by EF for the run.</summary>
    /// <param name="runId">Run id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tracked entity list.</returns>
    private async Task<List<RecalculationDecisionResult>> ListComputedRowsAsync(long runId, CancellationToken ct)
    {
        // Use Microsoft.EntityFrameworkCore's ToListAsync via the writer set —
        // these rows MUST be tracked so the mutations below land on save.
        var query = _db.RecalculationDecisionResults
            .Where(r => r.RunId == runId
                && r.IsActive
                && r.Status == RecalculationResultStatus.Computed);
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(query, ct).ConfigureAwait(false);
    }

    /// <summary>Recomputes the run-level totals after an apply pass.</summary>
    /// <param name="run">Run row.</param>
    /// <param name="actor">Audit-attribution identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RecountAsync(RecalculationRun run, string actor, CancellationToken ct)
    {
        long applied = 0L;
        long recalculated = 0L;
        long skipped = 0L;
        long failed = 0L;
        long rejected = 0L;
        long scanned = 0L;
        decimal totalDelta = 0m;

        var allRows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(_db.RecalculationDecisionResults
                .Where(r => r.RunId == run.Id && r.IsActive), ct)
            .ConfigureAwait(false);
        foreach (var r in allRows)
        {
            scanned += 1;
            switch (r.Status)
            {
                case RecalculationResultStatus.Computed: recalculated += 1; totalDelta += r.DeltaMdl; break;
                case RecalculationResultStatus.Applied: applied += 1; totalDelta += r.DeltaMdl; break;
                case RecalculationResultStatus.Skipped: skipped += 1; break;
                case RecalculationResultStatus.Failed: failed += 1; break;
                case RecalculationResultStatus.Rejected: rejected += 1; break;
            }
        }

        run.TotalDecisionsScanned = scanned;
        run.TotalDecisionsRecalculated = recalculated + applied;
        run.TotalSkipped = skipped + rejected;
        run.TotalFailed = failed;
        run.TotalDeltaMdl = totalDelta;
        run.UpdatedAtUtc = _clock.UtcNow;
        run.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
