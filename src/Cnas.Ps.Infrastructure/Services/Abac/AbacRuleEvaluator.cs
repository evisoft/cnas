using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abac;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — production implementation of
/// <see cref="IAbacRuleEvaluator"/>. Walks the active rules of the named rule
/// set in <see cref="AbacRule.OrderIndex"/> ascending order; the first rule
/// whose condition matches dictates the verdict.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safe-by-default failure modes.</b> Any per-rule parse / runtime failure
/// is logged + audited (<c>ABAC.RULE_EVAL_ERROR</c> via the
/// <see cref="CnasMeter.AbacRuleEvalError"/> counter) and treated as "did not
/// match" — the offending rule is skipped, the substrate moves to the next
/// rule. A malformed rule MUST NEVER silently grant access.
/// </para>
/// <para>
/// <b>Caching.</b> Parsed ASTs are cached in a thread-safe
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by rule id; the cache
/// is purged via <see cref="InvalidateCache"/> after any rule mutation. The
/// evaluator is registered as a Singleton so the cache survives the
/// scope/request lifecycle.
/// </para>
/// </remarks>
public sealed class AbacRuleEvaluator : IAbacRuleEvaluator
{
    private readonly IServiceProvider _services;
    private readonly IAbacExpressionParser _parser;
    private readonly ISqidService _sqids;
    private readonly ILogger<AbacRuleEvaluator> _logger;
    private readonly ConcurrentDictionary<long, AbacExpression> _cache = new();

    /// <summary>Constructs the evaluator with its singleton collaborators.</summary>
    /// <param name="services">Root service provider — scoped DB context is resolved per evaluation.</param>
    /// <param name="parser">Shared expression parser.</param>
    /// <param name="sqids">Sqid encoder for boundary id translation.</param>
    /// <param name="logger">Logger sink for audit + diagnostic trail.</param>
    public AbacRuleEvaluator(
        IServiceProvider services,
        IAbacExpressionParser parser,
        ISqidService sqids,
        ILogger<AbacRuleEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _parser = parser;
        _sqids = sqids;
        _logger = logger;
    }

    /// <inheritdoc />
    public void InvalidateCache() => _cache.Clear();

    /// <inheritdoc />
    public async Task<Result<AbacDecisionDto>> EvaluateAsync(
        string policyName,
        AbacEvaluationContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(context);

        // Scope a writable DbContext so the evaluator can run from singleton
        // lifetime against per-request scoped state. EvaluateAsync only reads
        // — but the writable interface is what the registry uses, so reusing
        // it keeps EF model identity stable.
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();

        var ruleSet = await db.AbacRuleSets
            .AsNoTracking()
            .Include(rs => rs.Rules.Where(r => r.IsActive))
            .Where(rs => rs.PolicyName == policyName && rs.IsActive)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (ruleSet is null)
        {
            return Result<AbacDecisionDto>.Failure(
                ErrorCodes.AbacNotFound,
                $"No active ABAC rule set found for policy '{policyName}'.");
        }

        return Result<AbacDecisionDto>.Success(EvaluateRuleSet(ruleSet, context));
    }

    /// <summary>
    /// Walks <paramref name="ruleSet"/>'s active rules in OrderIndex ASC and
    /// returns the verdict. Visible to the registry service so dry-run tests
    /// can share the exact evaluation path used in production.
    /// </summary>
    /// <param name="ruleSet">The rule set (with its active <see cref="AbacRuleSet.Rules"/> loaded).</param>
    /// <param name="context">The attribute payload.</param>
    /// <returns>The decision envelope.</returns>
    public AbacDecisionDto EvaluateRuleSet(AbacRuleSet ruleSet, AbacEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(context);

        var rules = new List<AbacRule>(ruleSet.Rules);
        rules.Sort((a, b) => a.OrderIndex.CompareTo(b.OrderIndex));

        var traceEntries = new List<TraceEntry>(rules.Count);
        AbacRule? matchedRule = null;

        foreach (var rule in rules)
        {
            if (!rule.IsActive)
            {
                continue;
            }
            // Try to fetch a cached AST; parse + cache on miss. Parse failure is
            // safe-by-default: log + counter + treat as non-match.
            if (!_cache.TryGetValue(rule.Id, out var ast))
            {
                var parse = _parser.Parse(rule.ConditionExpression);
                if (parse.IsFailure)
                {
                    _logger.LogWarning(
                        "ABAC.RULE_EVAL_ERROR: rule id {RuleId} of policy {PolicyName} failed to parse: {Detail}",
                        rule.Id,
                        ruleSet.PolicyName,
                        parse.ErrorMessage);
                    CnasMeter.AbacRuleEvalError.Add(1,
                        new KeyValuePair<string, object?>("policy_name", ruleSet.PolicyName));
                    traceEntries.Add(new TraceEntry(_sqids.Encode(rule.Id), rule.OrderIndex, Matched: false, ErrorIfAny: parse.ErrorCode));
                    continue;
                }
                ast = parse.Value;
                _cache[rule.Id] = ast;
            }

            bool matches;
            try
            {
                matches = ast.Evaluate(context);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ABAC.RULE_EVAL_ERROR: rule id {RuleId} of policy {PolicyName} threw at evaluation.",
                    rule.Id,
                    ruleSet.PolicyName);
                CnasMeter.AbacRuleEvalError.Add(1,
                    new KeyValuePair<string, object?>("policy_name", ruleSet.PolicyName));
                traceEntries.Add(new TraceEntry(_sqids.Encode(rule.Id), rule.OrderIndex, Matched: false, ErrorIfAny: "ABAC.RUNTIME_ERROR"));
                continue;
            }

            traceEntries.Add(new TraceEntry(_sqids.Encode(rule.Id), rule.OrderIndex, Matched: matches, ErrorIfAny: null));
            if (matches)
            {
                matchedRule = rule;
                break;
            }
        }

        var effect = matchedRule?.Effect ?? ruleSet.DefaultEffect;
        CnasMeter.AbacDecisionEvaluated.Add(1,
            new KeyValuePair<string, object?>("policy_name", ruleSet.PolicyName),
            new KeyValuePair<string, object?>("effect", effect.ToString()));

        var traceJson = JsonSerializer.Serialize(traceEntries);
        return new AbacDecisionDto(
            Effect: effect.ToString(),
            MatchedRuleSqid: matchedRule is null ? null : _sqids.Encode(matchedRule.Id),
            MatchedRuleOrderIndex: matchedRule?.OrderIndex,
            EvaluationTraceJson: traceJson);
    }

    /// <summary>Structured trace row appended to <see cref="AbacDecisionDto.EvaluationTraceJson"/>.</summary>
    /// <param name="RuleSqid">Sqid of the evaluated rule.</param>
    /// <param name="OrderIndex">Order index at evaluation time.</param>
    /// <param name="Matched">Whether the rule matched.</param>
    /// <param name="ErrorIfAny">Stable error code when the rule failed to evaluate; null otherwise.</param>
    private sealed record TraceEntry(string RuleSqid, int OrderIndex, bool Matched, string? ErrorIfAny);
}
