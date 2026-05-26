using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.WorkflowAcl;
using Cnas.Ps.Application.WorkflowRules;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="IWorkflowRuleEngine"/> implementation. Loads the rule-pack
/// codes off the <see cref="WorkflowDefinition"/> / <see cref="WorkflowTask"/> and
/// delegates evaluation to the configured <see cref="IWorkflowRulePackEvaluator"/>.
/// Every evaluation increments
/// <see cref="CnasMeter.WorkflowRuleEvaluated"/> tagged with
/// <c>stage</c> and <c>allowed</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure containment.</b> The evaluator is third-party-shaped — it may throw
/// unexpectedly. The engine wraps every call in a try/catch, logs the exception at
/// <c>LogError</c>, and returns a blocked result carrying
/// <see cref="WorkflowAclConstants.RuleEngineErrorReason"/>. Letting the exception
/// bubble would create a non-deterministic gate.
/// </para>
/// <para>
/// <b>Pack-not-found.</b> A workflow that references an unknown rule pack is treated
/// as blocked (reason
/// <see cref="WorkflowAclConstants.RulePackNotFoundReason"/>). The opposite policy
/// (treat unknown as allow) would silently let a misconfigured workflow proceed
/// without rules and defeat the whole point of R0124.
/// </para>
/// </remarks>
public sealed class WorkflowRuleEngine : IWorkflowRuleEngine
{
    private readonly IReadOnlyCnasDbContext _db;
    private readonly IWorkflowRulePackEvaluator _evaluator;
    private readonly ILogger<WorkflowRuleEngine> _logger;

    /// <summary>Constructs the engine with its DI dependencies.</summary>
    /// <param name="db">Read-only DB context for resolving workflow / task metadata.</param>
    /// <param name="evaluator">Underlying rule-pack evaluator shim.</param>
    /// <param name="logger">Structured logger for failure diagnostics.</param>
    public WorkflowRuleEngine(
        IReadOnlyCnasDbContext db,
        IWorkflowRulePackEvaluator evaluator,
        ILogger<WorkflowRuleEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _evaluator = evaluator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RuleEvaluationResult> EvaluateStartAsync(
        long workflowDefinitionId,
        long serviceApplicationId,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct = default)
    {
        var workflow = await _db.WorkflowDefinitions
            .Where(w => w.Id == workflowDefinitionId)
            .Select(w => new { w.StartRulePackCode })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var packCode = workflow?.StartRulePackCode;
        return await EvaluateAsync(
            packCode,
            WorkflowRuleStages.Start,
            EnrichContext(context, "serviceApplicationId", serviceApplicationId),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RuleEvaluationResult> EvaluateTransitionAsync(
        long workflowTaskId,
        string fromStep,
        string toStep,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromStep);
        ArgumentException.ThrowIfNullOrWhiteSpace(toStep);

        // Resolve the rule pack via the task → dossier → application → passport →
        // workflow definition chain. Today the link only flows as far as the dossier
        // on the task entity; the workflow chain is reconstructed via the dossier's
        // application's passport.WorkflowCode and the latest matching workflow row.
        // When any link is missing we fall through to the "no rule pack" success
        // (treat as legacy/allow) — the alternative (block) would brick every legacy
        // workflow that has not yet opted into R0124.
        var query = from t in _db.WorkflowTasks
                    where t.Id == workflowTaskId
                    join d in _db.Dossiers on t.DossierId equals d.Id into dossiers
                    from d in dossiers.DefaultIfEmpty()
                    join a in _db.Applications on d.ApplicationId equals a.Id into apps
                    from a in apps.DefaultIfEmpty()
                    join p in _db.ServicePassports on a.ServicePassportId equals p.Id into passports
                    from p in passports.DefaultIfEmpty()
                    join w in _db.WorkflowDefinitions on p.WorkflowCode equals w.Code into wfs
                    from w in wfs.DefaultIfEmpty()
                    where w == null || w.IsCurrent
                    select new { Pack = w == null ? null : w.TransitionRulePackCode };
        var row = await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var packCode = row?.Pack;
        var enriched = EnrichContext(context, "workflowTaskId", workflowTaskId);
        enriched = EnrichContext(enriched, "fromStep", fromStep);
        enriched = EnrichContext(enriched, "toStep", toStep);
        return await EvaluateAsync(packCode, WorkflowRuleStages.Transition, enriched, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RuleEvaluationResult> EvaluateCompletionAsync(
        long workflowDefinitionId,
        long serviceApplicationId,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct = default)
    {
        var workflow = await _db.WorkflowDefinitions
            .Where(w => w.Id == workflowDefinitionId)
            .Select(w => new { w.CompletionRulePackCode })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var packCode = workflow?.CompletionRulePackCode;
        return await EvaluateAsync(
            packCode,
            WorkflowRuleStages.Completion,
            EnrichContext(context, "serviceApplicationId", serviceApplicationId),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Single entry point that handles pack-code-null pass-through, evaluator
    /// invocation, exception containment, and counter emission. Centralised so the
    /// three lifecycle hooks share identical failure semantics.
    /// </summary>
    /// <param name="rulePackCode">Pack code from the workflow definition; may be null.</param>
    /// <param name="stage">Stage label (also the counter tag value).</param>
    /// <param name="context">Enriched context bag forwarded to the evaluator.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The translated evaluation result.</returns>
    private async Task<RuleEvaluationResult> EvaluateAsync(
        string? rulePackCode,
        string stage,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rulePackCode))
        {
            // Pack-less stage — pass-through allow. Still emit the counter so operators
            // can see the call shape on the dashboard even when no rules fire.
            CnasMeter.WorkflowRuleEvaluated.Add(1,
                new KeyValuePair<string, object?>("stage", stage),
                new KeyValuePair<string, object?>("allowed", true));
            return RuleEvaluationResult.Allow();
        }

        WorkflowRulePackEvaluatorResult shimResult;
        try
        {
            shimResult = await _evaluator
                .EvaluateAsync(rulePackCode, stage, context, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "WorkflowRuleEngine evaluation failed for pack {RulePackCode} stage {Stage}.",
                rulePackCode, stage);
            CnasMeter.WorkflowRuleEvaluated.Add(1,
                new KeyValuePair<string, object?>("stage", stage),
                new KeyValuePair<string, object?>("allowed", false));
            return RuleEvaluationResult.Block(WorkflowAclConstants.RuleEngineErrorReason);
        }

        var allowed = shimResult.Outcome == WorkflowRulePackOutcome.Allow;
        CnasMeter.WorkflowRuleEvaluated.Add(1,
            new KeyValuePair<string, object?>("stage", stage),
            new KeyValuePair<string, object?>("allowed", allowed));

        return shimResult.Outcome switch
        {
            WorkflowRulePackOutcome.Allow => new RuleEvaluationResult(
                Allowed: true,
                BlockReason: null,
                Annotations: shimResult.Annotations),
            WorkflowRulePackOutcome.Block => RuleEvaluationResult.Block(
                shimResult.Reason ?? "RULE_BLOCKED"),
            WorkflowRulePackOutcome.NotFound => RuleEvaluationResult.Block(
                WorkflowAclConstants.RulePackNotFoundReason),
            _ => RuleEvaluationResult.Block(WorkflowAclConstants.RuleEngineErrorReason),
        };
    }

    /// <summary>
    /// Returns a new dictionary that includes everything from <paramref name="context"/>
    /// plus the supplied key/value pair (overwriting any existing key). Null contexts
    /// produce a single-entry dictionary. The engine forwards the enriched bag to the
    /// evaluator so rule packs can read facts they did not have to set up explicitly
    /// (the workflow task id, the application id, the from/to step codes).
    /// </summary>
    /// <param name="context">Optional caller-supplied context bag.</param>
    /// <param name="key">Key to add.</param>
    /// <param name="value">Value to add.</param>
    /// <returns>A new readonly dictionary with the extra entry merged in.</returns>
    private static IReadOnlyDictionary<string, object>? EnrichContext(
        IReadOnlyDictionary<string, object>? context, string key, object value)
    {
        var merged = new Dictionary<string, object>(StringComparer.Ordinal);
        if (context is not null)
        {
            foreach (var kvp in context)
            {
                merged[kvp.Key] = kvp.Value;
            }
        }
        merged[key] = value;
        return merged;
    }
}

/// <summary>
/// R0124 — placeholder reference implementation of
/// <see cref="IWorkflowRulePackEvaluator"/> used until the full
/// <c>IDecisionEngine</c> rebinding lands. The placeholder returns
/// <see cref="WorkflowRulePackEvaluatorResult.Allow"/> for every rule-pack code (so a
/// workflow that opts into rules does not regress while the registry is being
/// built); the production binding will route to a per-environment rule-pack store.
/// </summary>
/// <remarks>
/// The shim contract is intentionally narrow so the placeholder can be swapped out
/// without touching <see cref="WorkflowRuleEngine"/> or any caller. Iteration report
/// flags the swap as deferred work.
/// </remarks>
public sealed class InMemoryWorkflowRulePackEvaluator : IWorkflowRulePackEvaluator
{
    /// <inheritdoc />
    public Task<WorkflowRulePackEvaluatorResult> EvaluateAsync(
        string rulePackCode,
        string stage,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulePackCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        // Placeholder: every known pack code allows. Future revisions will look the
        // code up in a registry; today the registry is empty so we permit.
        return Task.FromResult(WorkflowRulePackEvaluatorResult.Allow());
    }
}
