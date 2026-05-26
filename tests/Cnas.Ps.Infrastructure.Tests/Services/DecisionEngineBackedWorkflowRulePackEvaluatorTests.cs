using System.Diagnostics.Metrics;
using Cnas.Ps.Application.WorkflowRules;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0124 (continuation) — RED-first coverage for
/// <see cref="DecisionEngineBackedWorkflowRulePackEvaluator"/>. Each test pins down one
/// observable behaviour of the new evaluator bridge: pack-code pass-through to the
/// backend, allow/deny/error translation, the
/// <c>cnas.workflow.rule.decision_engine_invoked{outcome}</c> counter emission, and
/// the no-op backend's always-allow contract.
/// </summary>
public class DecisionEngineBackedWorkflowRulePackEvaluatorTests
{
    [Fact]
    public async Task Evaluate_RoutesRulePackCodeAndStage_ToBackend()
    {
        // ARRANGE — capture the (code, stage, context) tuple the backend receives.
        var backend = Substitute.For<IRulePackBackend>();
        backend.EvaluateAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RulePackBackendResult.Allow()));
        var evaluator = new DecisionEngineBackedWorkflowRulePackEvaluator(
            backend, NullLogger<DecisionEngineBackedWorkflowRulePackEvaluator>.Instance);

        // ACT
        await evaluator.EvaluateAsync(
            "PACK-START-V1", WorkflowRuleStages.Start,
            context: null, ct: default);

        // ASSERT
        await backend.Received(1).EvaluateAsync(
            "PACK-START-V1", WorkflowRuleStages.Start,
            Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Evaluate_BackendAllows_ReturnsAllowVerdict()
    {
        var backend = StubBackend(RulePackBackendResult.Allow());
        var evaluator = new DecisionEngineBackedWorkflowRulePackEvaluator(
            backend, NullLogger<DecisionEngineBackedWorkflowRulePackEvaluator>.Instance);

        var result = await evaluator.EvaluateAsync(
            "PACK-X", WorkflowRuleStages.Start, context: null);

        result.Outcome.Should().Be(WorkflowRulePackOutcome.Allow);
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task Evaluate_BackendBlocks_PropagatesReason()
    {
        var backend = StubBackend(RulePackBackendResult.Block("SUBSIDY_CAP_EXCEEDED"));
        var evaluator = new DecisionEngineBackedWorkflowRulePackEvaluator(
            backend, NullLogger<DecisionEngineBackedWorkflowRulePackEvaluator>.Instance);

        var result = await evaluator.EvaluateAsync(
            "PACK-X", WorkflowRuleStages.Transition, context: null);

        result.Outcome.Should().Be(WorkflowRulePackOutcome.Block);
        result.Reason.Should().Be("SUBSIDY_CAP_EXCEEDED");
    }

    [Fact]
    public async Task Evaluate_BackendThrows_ReturnsBlockWithRuleEngineErrorReason()
    {
        var backend = Substitute.For<IRulePackBackend>();
        backend.EvaluateAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<RulePackBackendResult>>(_ => throw new InvalidOperationException("boom"));
        var evaluator = new DecisionEngineBackedWorkflowRulePackEvaluator(
            backend, NullLogger<DecisionEngineBackedWorkflowRulePackEvaluator>.Instance);

        var result = await evaluator.EvaluateAsync(
            "PACK-X", WorkflowRuleStages.Start, context: null);

        result.Outcome.Should().Be(WorkflowRulePackOutcome.Block);
        result.Reason.Should().Be("RULE_ENGINE_ERROR");
    }

    [Fact]
    public async Task Evaluate_BackendAllows_IncrementsCounterTaggedAllow()
    {
        using var capture = new OutcomeCapture("cnas.workflow.rule.decision_engine_invoked");
        var evaluator = new DecisionEngineBackedWorkflowRulePackEvaluator(
            StubBackend(RulePackBackendResult.Allow()),
            NullLogger<DecisionEngineBackedWorkflowRulePackEvaluator>.Instance);

        await evaluator.EvaluateAsync("PACK-X", WorkflowRuleStages.Start, context: null);

        capture.Outcomes.Should().ContainSingle().Which.Should().Be("allow");
    }

    [Fact]
    public async Task Evaluate_BackendBlocks_IncrementsCounterTaggedDeny()
    {
        using var capture = new OutcomeCapture("cnas.workflow.rule.decision_engine_invoked");
        var evaluator = new DecisionEngineBackedWorkflowRulePackEvaluator(
            StubBackend(RulePackBackendResult.Block("R")),
            NullLogger<DecisionEngineBackedWorkflowRulePackEvaluator>.Instance);

        await evaluator.EvaluateAsync("PACK-X", WorkflowRuleStages.Start, context: null);

        capture.Outcomes.Should().ContainSingle().Which.Should().Be("deny");
    }

    [Fact]
    public async Task Evaluate_BackendThrows_IncrementsCounterTaggedError()
    {
        using var capture = new OutcomeCapture("cnas.workflow.rule.decision_engine_invoked");
        var backend = Substitute.For<IRulePackBackend>();
        backend.EvaluateAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<RulePackBackendResult>>(_ => throw new InvalidOperationException("boom"));
        var evaluator = new DecisionEngineBackedWorkflowRulePackEvaluator(
            backend, NullLogger<DecisionEngineBackedWorkflowRulePackEvaluator>.Instance);

        await evaluator.EvaluateAsync("PACK-X", WorkflowRuleStages.Start, context: null);

        capture.Outcomes.Should().ContainSingle().Which.Should().Be("error");
    }

    [Fact]
    public async Task Evaluate_ContextPassThrough_BackendReceivesSameKeysAndValues()
    {
        IReadOnlyDictionary<string, object>? observed = null;
        var backend = Substitute.For<IRulePackBackend>();
        backend.EvaluateAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observed = call.Arg<IReadOnlyDictionary<string, object>?>();
                return Task.FromResult(RulePackBackendResult.Allow());
            });
        var evaluator = new DecisionEngineBackedWorkflowRulePackEvaluator(
            backend, NullLogger<DecisionEngineBackedWorkflowRulePackEvaluator>.Instance);

        var ctx = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["serviceApplicationId"] = 42L,
            ["claimDateUtc"] = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc),
            ["isInsured"] = true,
        };
        await evaluator.EvaluateAsync(
            "PACK-X", WorkflowRuleStages.Completion, ctx, default);

        observed.Should().NotBeNull();
        observed!.Should().ContainKey("serviceApplicationId").WhoseValue.Should().Be(42L);
        observed.Should().ContainKey("claimDateUtc");
        observed.Should().ContainKey("isInsured").WhoseValue.Should().Be(true);
    }

    [Fact]
    public async Task NoopBackend_AllStages_AlwaysAllow()
    {
        var backend = new NoopRulePackBackend(NullLogger<NoopRulePackBackend>.Instance);
        var evaluator = new DecisionEngineBackedWorkflowRulePackEvaluator(
            backend, NullLogger<DecisionEngineBackedWorkflowRulePackEvaluator>.Instance);

        var s = await evaluator.EvaluateAsync("ANY-PACK", WorkflowRuleStages.Start, context: null);
        var t = await evaluator.EvaluateAsync("ANY-PACK", WorkflowRuleStages.Transition, context: null);
        var c = await evaluator.EvaluateAsync("ANY-PACK", WorkflowRuleStages.Completion, context: null);

        s.Outcome.Should().Be(WorkflowRulePackOutcome.Allow);
        t.Outcome.Should().Be(WorkflowRulePackOutcome.Allow);
        c.Outcome.Should().Be(WorkflowRulePackOutcome.Allow);
    }

    [Fact]
    public async Task NoopBackend_CounterStillTracksOutcomes()
    {
        using var capture = new OutcomeCapture("cnas.workflow.rule.decision_engine_invoked");
        var evaluator = new DecisionEngineBackedWorkflowRulePackEvaluator(
            new NoopRulePackBackend(NullLogger<NoopRulePackBackend>.Instance),
            NullLogger<DecisionEngineBackedWorkflowRulePackEvaluator>.Instance);

        await evaluator.EvaluateAsync("ANY-PACK", WorkflowRuleStages.Start, context: null);
        await evaluator.EvaluateAsync("ANY-PACK", WorkflowRuleStages.Transition, context: null);

        capture.Outcomes.Should().HaveCount(2);
        capture.Outcomes.Should().AllBeEquivalentTo("allow");
    }

    [Fact]
    public async Task Evaluate_BackendAllowsWithAnnotations_PropagatesAnnotations()
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["recomputedCategory"] = "DISABILITY-II",
        };
        var backend = StubBackend(RulePackBackendResult.AllowWith(annotations));
        var evaluator = new DecisionEngineBackedWorkflowRulePackEvaluator(
            backend, NullLogger<DecisionEngineBackedWorkflowRulePackEvaluator>.Instance);

        var result = await evaluator.EvaluateAsync(
            "PACK-X", WorkflowRuleStages.Completion, context: null);

        result.Outcome.Should().Be(WorkflowRulePackOutcome.Allow);
        result.Annotations.Should().NotBeNull();
        result.Annotations!.Should().ContainKey("recomputedCategory")
            .WhoseValue.Should().Be("DISABILITY-II");
    }

    // ───────────────────────── helpers ─────────────────────────

    private static IRulePackBackend StubBackend(RulePackBackendResult result)
    {
        var backend = Substitute.For<IRulePackBackend>();
        backend.EvaluateAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
        return backend;
    }

    /// <summary>
    /// MeterListener-based capture that records the <c>outcome</c> tag from each
    /// measurement on the named instrument. Disposes the listener at end-of-test
    /// to clean up.
    /// </summary>
    private sealed class OutcomeCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<string> _outcomes = new();
        private readonly object _gate = new();

        public IReadOnlyList<string> Outcomes
        {
            get { lock (_gate) return _outcomes.ToList(); }
        }

        public OutcomeCapture(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName
                        && instrument.Name == instrumentName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                foreach (var t in tags)
                {
                    if (t.Key == "outcome" && t.Value is string s)
                    {
                        lock (_gate) _outcomes.Add(s);
                    }
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
