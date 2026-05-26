using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using Cnas.Ps.Application.Abac;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Abac;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — tests for <see cref="AbacRuleEvaluator"/>. Covers the
/// first-match wins semantic, the default-effect fallback, malformed-rule
/// safe-by-default discipline, the parse cache, and registry-driven cache
/// invalidation.
/// </summary>
public sealed class AbacRuleEvaluatorTests
{
    private static CnasDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-abac-eval-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static ISqidService NewSqidStub()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        s.TryDecode(Arg.Any<string?>()).Returns(call =>
        {
            var v = call.Arg<string?>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "n/a");
        });
        return s;
    }

    private static (AbacRuleEvaluator Evaluator, CnasDbContext Db, IServiceProvider Provider) Build()
    {
        var db = NewContext();
        var services = new ServiceCollection();
        services.AddSingleton<ICnasDbContext>(db);
        var provider = services.BuildServiceProvider();
        var parser = new AbacExpressionParser();
        var evaluator = new AbacRuleEvaluator(provider, parser, NewSqidStub(), NullLogger<AbacRuleEvaluator>.Instance);
        return (evaluator, db, provider);
    }

    private static async System.Threading.Tasks.Task<AbacRuleSet> SeedRuleSetAsync(
        CnasDbContext db,
        string policyName,
        AbacEffect defaultEffect,
        params (int Order, AbacEffect Effect, string Expression)[] rules)
    {
        var rs = new AbacRuleSet
        {
            PolicyName = policyName,
            DisplayName = policyName,
            DefaultEffect = defaultEffect,
            IsActive = true,
            RegisteredByUserId = 1L,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "test",
        };
        foreach (var (order, effect, expr) in rules)
        {
            rs.Rules.Add(new AbacRule
            {
                OrderIndex = order,
                Effect = effect,
                ConditionExpression = expr,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = "test",
            });
        }
        db.AbacRuleSets.Add(rs);
        await db.SaveChangesAsync(CancellationToken.None);
        return rs;
    }

    [Fact]
    public async System.Threading.Tasks.Task FirstMatchingRuleWins()
    {
        var (evaluator, db, _) = Build();
        await SeedRuleSetAsync(db, "DOSSIER.READ", AbacEffect.Deny,
            (Order: 0, Effect: AbacEffect.Deny, Expression: "subject.role == \"BAD\""),
            (Order: 1, Effect: AbacEffect.Allow, Expression: "subject.role == \"GOOD\""));

        var ctx = new AbacEvaluationContext(
            new Dictionary<string, object?> { ["role"] = "GOOD" },
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>());
        var decision = await evaluator.EvaluateAsync("DOSSIER.READ", ctx, CancellationToken.None);

        decision.IsSuccess.Should().BeTrue();
        decision.Value.Effect.Should().Be(nameof(AbacEffect.Allow));
        decision.Value.MatchedRuleOrderIndex.Should().Be(1);
    }

    [Fact]
    public async System.Threading.Tasks.Task NoMatch_FallsBackToDefaultEffect()
    {
        var (evaluator, db, _) = Build();
        await SeedRuleSetAsync(db, "DOSSIER.READ", AbacEffect.Deny,
            (Order: 0, Effect: AbacEffect.Allow, Expression: "subject.role == \"ADMIN\""));

        var ctx = new AbacEvaluationContext(
            new Dictionary<string, object?> { ["role"] = "GUEST" },
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>());
        var decision = await evaluator.EvaluateAsync("DOSSIER.READ", ctx, CancellationToken.None);

        decision.IsSuccess.Should().BeTrue();
        decision.Value.Effect.Should().Be(nameof(AbacEffect.Deny));
        decision.Value.MatchedRuleSqid.Should().BeNull();
    }

    [Fact]
    public async System.Threading.Tasks.Task MalformedRule_TreatedAsNonMatching_AndEmitsCounter()
    {
        var (evaluator, db, _) = Build();
        // Inject a malformed rule directly past the validator — defends the
        // safe-by-default invariant: even if a bad rule slips into the table
        // (e.g. via a manual DB edit), the evaluator must NOT silently match.
        await SeedRuleSetAsync(db, "DOSSIER.READ", AbacEffect.Deny,
            (Order: 0, Effect: AbacEffect.Allow, Expression: "this is not valid"),
            (Order: 1, Effect: AbacEffect.Allow, Expression: "subject.role == \"OK\""));

        var observed = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CnasMeter.MeterName && instr.Name == "cnas.abac.rule.eval_error")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref observed, value));
        listener.Start();

        var ctx = new AbacEvaluationContext(
            new Dictionary<string, object?> { ["role"] = "OK" },
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>());
        var decision = await evaluator.EvaluateAsync("DOSSIER.READ", ctx, CancellationToken.None);

        decision.IsSuccess.Should().BeTrue();
        // Second rule matched — first rule was skipped due to parse error,
        // NOT treated as "matched with Allow".
        decision.Value.Effect.Should().Be(nameof(AbacEffect.Allow));
        decision.Value.MatchedRuleOrderIndex.Should().Be(1);
        observed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async System.Threading.Tasks.Task ParsedAst_IsCached_AcrossEvaluations()
    {
        var (evaluator, db, _) = Build();
        var rs = await SeedRuleSetAsync(db, "DOSSIER.READ", AbacEffect.Deny,
            (Order: 0, Effect: AbacEffect.Allow, Expression: "subject.role == \"GOOD\""));

        var ctx = new AbacEvaluationContext(
            new Dictionary<string, object?> { ["role"] = "GOOD" },
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>());
        var first = await evaluator.EvaluateAsync("DOSSIER.READ", ctx, CancellationToken.None);
        var second = await evaluator.EvaluateAsync("DOSSIER.READ", ctx, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        first.Value.Effect.Should().Be(second.Value.Effect);
        first.Value.MatchedRuleSqid.Should().Be(second.Value.MatchedRuleSqid);

        // Now mutate the in-memory rule's expression to a non-matching one
        // WITHOUT invalidating the cache. The evaluator must still return
        // Allow because the cached AST is the old expression — proves the
        // cache is honoured.
        var rule = rs.Rules.Single();
        rule.ConditionExpression = "subject.role == \"NEVER\"";
        await db.SaveChangesAsync();
        var third = await evaluator.EvaluateAsync("DOSSIER.READ", ctx, CancellationToken.None);
        third.Value.Effect.Should().Be(nameof(AbacEffect.Allow));

        // Explicitly invalidate; now the updated expression takes effect.
        evaluator.InvalidateCache();
        var fourth = await evaluator.EvaluateAsync("DOSSIER.READ", ctx, CancellationToken.None);
        fourth.Value.Effect.Should().Be(nameof(AbacEffect.Deny));
    }

    [Fact]
    public async System.Threading.Tasks.Task UnknownPolicy_ReturnsAbacNotFound()
    {
        var (evaluator, _, _) = Build();
        var ctx = new AbacEvaluationContext(
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>());

        var decision = await evaluator.EvaluateAsync("DOES.NOT_EXIST", ctx, CancellationToken.None);

        decision.IsFailure.Should().BeTrue();
        decision.ErrorCode.Should().Be(ErrorCodes.AbacNotFound);
    }
}
