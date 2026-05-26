using System.Collections.Frozen;
using Cnas.Ps.Application.QueryBudget;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.QueryBudget;

/// <summary>
/// R0167 / TOR CF 01.06 / CF 03.07-08 — production <see cref="IQueryBudgetPolicy"/>
/// resolver, seeded at construction with one <see cref="QueryBudgetPolicy"/> per known
/// registry. Registered as a singleton in <see cref="InfrastructureServiceCollectionExtensions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Static-table pattern.</b> The seed map is built once in the constructor with
/// <see cref="FrozenDictionary{TKey, TValue}"/> for O(1) <see cref="GetForRegistry"/>
/// lookup. Adding a new registry is a one-line edit to <see cref="BuildSeedMap"/> plus
/// a new constant in <see cref="QueryBudgetRegistries"/>.
/// </para>
/// <para>
/// <b>Unknown-registry behaviour.</b> A lookup for a registry not in the seed map
/// returns a default policy (budget <see cref="QueryBudgetPolicy.DefaultBudget"/>,
/// zero rules) and emits a WARNING log so operators see the missing registration. The
/// default policy is still a meaningful guard — it refuses queries above 5000 rows
/// even without registry-specific hints.
/// </para>
/// </remarks>
public sealed class StaticQueryBudgetPolicy : IQueryBudgetPolicy
{
    /// <summary>Seed table populated at construction; never mutated.</summary>
    private readonly FrozenDictionary<string, QueryBudgetPolicy> _byRegistry;

    /// <summary>Diagnostic sink for unknown-registry lookups.</summary>
    private readonly ILogger<StaticQueryBudgetPolicy> _logger;

    /// <summary>Constructs the resolver and builds the seed map.</summary>
    /// <param name="logger">Diagnostic logger for unknown-registry warnings.</param>
    public StaticQueryBudgetPolicy(ILogger<StaticQueryBudgetPolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _byRegistry = BuildSeedMap();
    }

    /// <inheritdoc />
    public QueryBudgetPolicy GetForRegistry(string registry)
    {
        var key = registry ?? string.Empty;
        if (_byRegistry.TryGetValue(key, out var policy))
        {
            return policy;
        }
        _logger.LogWarning(
            "Query budget requested for unregistered registry {Registry}; falling back to default policy.",
            key);
        return new QueryBudgetPolicy(key, QueryBudgetPolicy.DefaultBudget, Array.Empty<RefinementHintRule>());
    }

    /// <summary>
    /// Seeds one <see cref="QueryBudgetPolicy"/> per known registry. The hint declarations
    /// here are the canonical refinement prompts the UI binds against — see each
    /// builder chain's inline rationale.
    /// </summary>
    /// <returns>Frozen registry → policy map.</returns>
    private static FrozenDictionary<string, QueryBudgetPolicy> BuildSeedMap()
    {
        var entries = new[]
        {
            // Solicitant: 5000 budget. The registry has no date or status column the UI
            // commonly filters by, so the primary nudge is a free-text query (Q) or a
            // national-identifier substring. Suggested fallback narrows by creation date.
            QueryBudgetPolicyBuilder
                .For(QueryBudgetRegistries.Solicitant)
                .WithBudget(QueryBudgetPolicy.DefaultBudget)
                .RequireWhen(
                    "Q",
                    RefinementHintReasons.AddFreeTextFilter,
                    ctx => !ctx.Has("Q") && !ctx.Has("NationalIdHash"))
                .Suggest("CreatedFromUtc", RefinementHintReasons.AddDateFilter)
                .Suggest("CreatedToUtc", RefinementHintReasons.AddDateFilter)
                .Build(),

            // Cerere: Required Status (so workers don't accidentally scan completed-
            // application long tail). Suggested date range + assigned user for triage.
            QueryBudgetPolicyBuilder
                .For(QueryBudgetRegistries.Cerere)
                .WithBudget(QueryBudgetPolicy.DefaultBudget)
                .Require("Status", RefinementHintReasons.AddStatusFilter)
                .Suggest("CreatedFromUtc", RefinementHintReasons.AddDateFilter)
                .Suggest("CreatedToUtc", RefinementHintReasons.AddDateFilter)
                .Suggest("AssignedUserId", RefinementHintReasons.AddOwnerFilter)
                .Build(),

            // WorkflowTask: Required Status excluding Completed (unless a date range
            // is present — historic-task queries are legitimate but must be bounded).
            QueryBudgetPolicyBuilder
                .For(QueryBudgetRegistries.WorkflowTask)
                .WithBudget(QueryBudgetPolicy.DefaultBudget)
                .RequireWhen(
                    "Status",
                    RefinementHintReasons.AddStatusFilter,
                    ctx => !ctx.Has("Status") && !ctx.Has("CreatedFromUtc"))
                .Suggest("AssigneeUserId", RefinementHintReasons.AddOwnerFilter)
                .Build(),

            // Decision: Required date range when no other filter is present (decisions
            // accumulate over time; without bounds the registry is unbounded).
            QueryBudgetPolicyBuilder
                .For(QueryBudgetRegistries.Decision)
                .WithBudget(QueryBudgetPolicy.DefaultBudget)
                .RequireWhen(
                    "CreatedFromUtc",
                    RefinementHintReasons.AddDateFilter,
                    ctx => !ctx.Has("CreatedFromUtc") && !ctx.Has("CreatedToUtc") && !ctx.Has("Q"))
                .Build(),

            // Document: Required free-text or owner-solicitant pin. Documents pile up
            // over years; either narrowing dimension is acceptable.
            QueryBudgetPolicyBuilder
                .For(QueryBudgetRegistries.Document)
                .WithBudget(QueryBudgetPolicy.DefaultBudget)
                .RequireWhen(
                    "Q",
                    RefinementHintReasons.AddFreeTextFilter,
                    ctx => !ctx.Has("Q") && !ctx.Has("OwnerSolicitantId"))
                .Build(),

            // AuditLog: 1000 budget (tighter — audit rows are heavier and operators
            // browse via the SIEM at scale). Required EventCode, ActorUserId, or
            // bounded date range.
            QueryBudgetPolicyBuilder
                .For(QueryBudgetRegistries.AuditLog)
                .WithBudget(1000)
                .RequireWhen(
                    "EventCode",
                    RefinementHintReasons.AddIdentifierFilter,
                    ctx =>
                        !ctx.Has("EventCode")
                        && !ctx.Has("ActorUserId")
                        && !(ctx.Has("CreatedFromUtc") && ctx.Has("CreatedToUtc")))
                .Build(),

            // PublicCatalog (R0504 / TOR CF 01.06): 1000 budget. The catalogue is
            // anonymous-accessible — the tight budget protects it from scraper-style
            // "give me everything" enumeration that would otherwise materialise the
            // entire passport set in one call. Required Q or Category; suggested
            // Category when Q is the active filter so the UI prompts further
            // narrowing.
            QueryBudgetPolicyBuilder
                .For(QueryBudgetRegistries.PublicCatalog)
                .WithBudget(1000)
                .RequireWhen(
                    "Q",
                    RefinementHintReasons.AddFreeTextFilter,
                    ctx => !ctx.Has("Q") && !ctx.Has("Category"))
                .Suggest("Category", RefinementHintReasons.AddIdentifierFilter)
                .Build(),

            // Declaration (R0822 / TOR Annex 8 BP 1.2-M): 5000 budget — the
            // explorer surface mirrors Annex 1 §8.1.3 search criteria. The
            // canonical narrowing dimensions are payer + kind + reporting
            // window: refuse a wide-open list call by requiring EITHER a
            // contributor pin OR a kind filter when no QBE narrowing is
            // present. Suggested date-range nudges so an operator who picks
            // "all Sfs rows" still gets prompted to add the period.
            QueryBudgetPolicyBuilder
                .For(QueryBudgetRegistries.Declaration)
                .WithBudget(QueryBudgetPolicy.DefaultBudget)
                .RequireWhen(
                    "Kind",
                    RefinementHintReasons.AddIdentifierFilter,
                    ctx => !ctx.Has("Kind") && !ctx.Has("ContributorId") && !ctx.Has("Qbe"))
                .Suggest("FromUtc", RefinementHintReasons.AddDateFilter)
                .Suggest("ToUtc", RefinementHintReasons.AddDateFilter)
                .Build(),
        };

        return entries.ToFrozenDictionary(p => p.Registry, StringComparer.Ordinal);
    }
}
