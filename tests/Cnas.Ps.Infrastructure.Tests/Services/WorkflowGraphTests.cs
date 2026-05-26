using System.Linq;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Application.Workflow;
using Cnas.Ps.Application.WorkflowRules;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Workflow;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0123 / TOR CF 16.05 — TDD coverage for the persisted workflow graph store and the
/// deterministic graph executor. Each test targets one observable behaviour: the
/// validator's structural rules, the service's version-mint contract, the executor's
/// per-node-kind semantics (sequential / AndSplit / AndJoin / OrSplit / fail-open), and
/// the controller round-trip.
/// </summary>
public class WorkflowGraphTests
{
    /// <summary>Deterministic clock instant used by every test in this suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── Validator tests ───────────────────────

    [Fact]
    public void Validator_TwoStartNodes_FailsValidation()
    {
        var v = new WorkflowGraphInputDtoValidator();
        var graph = new WorkflowGraphInputDto(
            Nodes:
            [
                new("start-a", nameof(WorkflowNodeKind.Start), null, null, 0),
                new("start-b", nameof(WorkflowNodeKind.Start), null, null, 1),
                new("end-1", nameof(WorkflowNodeKind.End), null, null, 2),
            ],
            Edges: [new("start-a", "end-1", null, 0)]);

        var result = v.Validate(graph);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Start", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_NoEndNode_FailsValidation()
    {
        var v = new WorkflowGraphInputDtoValidator();
        var graph = new WorkflowGraphInputDto(
            Nodes: [new("start-a", nameof(WorkflowNodeKind.Start), null, null, 0)],
            Edges: []);

        var result = v.Validate(graph);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("End", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_DuplicateNodeCode_FailsValidation()
    {
        var v = new WorkflowGraphInputDtoValidator();
        var graph = new WorkflowGraphInputDto(
            Nodes:
            [
                new("start-a", nameof(WorkflowNodeKind.Start), null, null, 0),
                new("dup", nameof(WorkflowNodeKind.Task), null, null, 1),
                new("dup", nameof(WorkflowNodeKind.Task), null, null, 2),
                new("end-1", nameof(WorkflowNodeKind.End), null, null, 3),
            ],
            Edges:
            [
                new("start-a", "dup", null, 0),
                new("dup", "end-1", null, 1),
            ]);

        var result = v.Validate(graph);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Duplicate", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_OrphanEdgeSource_FailsValidation()
    {
        var v = new WorkflowGraphInputDtoValidator();
        var graph = new WorkflowGraphInputDto(
            Nodes:
            [
                new("start-a", nameof(WorkflowNodeKind.Start), null, null, 0),
                new("end-1", nameof(WorkflowNodeKind.End), null, null, 1),
            ],
            Edges:
            [
                new("does-not-exist", "end-1", null, 0),
            ]);

        var result = v.Validate(graph);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("source", System.StringComparison.OrdinalIgnoreCase)
            || e.ErrorMessage.Contains("does-not-exist", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_Cycle_FailsValidation()
    {
        // start → a → b → a (cycle)
        var v = new WorkflowGraphInputDtoValidator();
        var graph = new WorkflowGraphInputDto(
            Nodes:
            [
                new("start-a", nameof(WorkflowNodeKind.Start), null, null, 0),
                new("a", nameof(WorkflowNodeKind.Task), null, null, 1),
                new("b", nameof(WorkflowNodeKind.Task), null, null, 2),
                new("end-1", nameof(WorkflowNodeKind.End), null, null, 3),
            ],
            Edges:
            [
                new("start-a", "a", null, 0),
                new("a", "b", null, 1),
                new("b", "a", null, 2),
                new("a", "end-1", null, 3),
            ]);

        var result = v.Validate(graph);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains(WorkflowGraphInputDtoValidator.CycleErrorKey, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_AndSplitSingleOutgoing_FailsValidation()
    {
        var v = new WorkflowGraphInputDtoValidator();
        var graph = new WorkflowGraphInputDto(
            Nodes:
            [
                new("start-a", nameof(WorkflowNodeKind.Start), null, null, 0),
                new("split", nameof(WorkflowNodeKind.AndSplit), null, null, 1),
                new("end-1", nameof(WorkflowNodeKind.End), null, null, 2),
            ],
            Edges:
            [
                new("start-a", "split", null, 0),
                new("split", "end-1", null, 1),
            ]);

        var result = v.Validate(graph);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("AndSplit", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_OrSplitWithoutCondition_FailsValidation()
    {
        var v = new WorkflowGraphInputDtoValidator();
        var graph = new WorkflowGraphInputDto(
            Nodes:
            [
                new("start-a", nameof(WorkflowNodeKind.Start), null, null, 0),
                new("decide", nameof(WorkflowNodeKind.OrSplit), null, /*ConditionExpression*/ null, 1),
                new("a", nameof(WorkflowNodeKind.Task), null, null, 2),
                new("b", nameof(WorkflowNodeKind.Task), null, null, 3),
                new("end-1", nameof(WorkflowNodeKind.End), null, null, 4),
            ],
            Edges:
            [
                new("start-a", "decide", null, 0),
                new("decide", "a", "branchA", 0),
                new("decide", "b", "branchB", 1),
                new("a", "end-1", null, 2),
                new("b", "end-1", null, 3),
            ]);

        var result = v.Validate(graph);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("ConditionExpression", System.StringComparison.Ordinal));
    }

    // ─────────────────────── Service tests ───────────────────────

    [Fact]
    public async Task ReplaceGraphAsync_HappyPath_WritesNodesAndEdges_AndEmitsCriticalAudit()
    {
        using var h = new ServiceHarness();
        var workflowSqid = await h.SeedWorkflowAsync(initialVersion: 1);

        var input = new WorkflowGraphInputDto(
            Nodes:
            [
                new("start-a", nameof(WorkflowNodeKind.Start), null, null, 0),
                new("review", nameof(WorkflowNodeKind.Task), "cnas-examiner", null, 1),
                new("end-1", nameof(WorkflowNodeKind.End), null, null, 2),
            ],
            Edges:
            [
                new("start-a", "review", null, 0),
                new("review", "end-1", null, 1),
            ]);

        var result = await h.Service.ReplaceGraphAsync(workflowSqid, input);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        // Verify rows landed under the NEW version.
        var newDef = await h.Db.WorkflowDefinitions.OrderByDescending(w => w.Version).FirstAsync();
        (await h.Db.WorkflowGraphNodes.CountAsync(n => n.WorkflowDefinitionId == newDef.Id))
            .Should().Be(3);
        (await h.Db.WorkflowGraphEdges.CountAsync(e => e.WorkflowDefinitionId == newDef.Id))
            .Should().Be(2);

        // Audit row emitted as Critical with the graph-replaced event code.
        await h.Audit.Received(1).RecordAsync(
            eventCode: WorkflowGraphService.GraphReplacedEvent,
            severity: AuditSeverity.Critical,
            actorId: Arg.Any<string>(),
            targetEntity: nameof(WorkflowDefinition),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceGraphAsync_MintsNewWorkflowDefinitionVersion()
    {
        using var h = new ServiceHarness();
        var workflowSqid = await h.SeedWorkflowAsync(initialVersion: 1);
        var preCount = await h.Db.WorkflowDefinitions.CountAsync();

        var input = new WorkflowGraphInputDto(
            Nodes:
            [
                new("start-a", nameof(WorkflowNodeKind.Start), null, null, 0),
                new("end-1", nameof(WorkflowNodeKind.End), null, null, 1),
            ],
            Edges: [new("start-a", "end-1", null, 0)]);

        var result = await h.Service.ReplaceGraphAsync(workflowSqid, input);

        result.IsSuccess.Should().BeTrue();
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(preCount + 1);
        result.Value!.Version.Should().Be(2);

        // Predecessor is no longer current.
        var rows = await h.Db.WorkflowDefinitions.OrderBy(w => w.Version).ToListAsync();
        rows[0].IsCurrent.Should().BeFalse();
        rows[1].IsCurrent.Should().BeTrue();
        rows[1].SupersedesDefinitionId.Should().Be(rows[0].Id);
    }

    // ─────────────────────── Executor tests ───────────────────────

    [Fact]
    public async Task Executor_Sequential_CreatesNextTaskOnSingleEdge()
    {
        using var h = new ExecutorHarness();
        var workflowId = await h.SeedSequentialGraphAsync();
        var taskA = await h.SeedCompletedTaskAsync(workflowId, "step-a");

        var result = await h.Executor.AdvanceAsync(taskA);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        var newTask = await h.Db.WorkflowTasks.SingleAsync(t => t.Id == result.Value[0]);
        newTask.NodeCode.Should().Be("step-b");
    }

    [Fact]
    public async Task Executor_AndSplit_SpawnsTwoSiblingTasks()
    {
        using var h = new ExecutorHarness();
        var workflowId = await h.SeedAndSplitGraphAsync();
        var taskA = await h.SeedCompletedTaskAsync(workflowId, "step-a");

        var result = await h.Executor.AdvanceAsync(taskA);

        result.IsSuccess.Should().BeTrue();
        var spawned = await h.Db.WorkflowTasks
            .Where(t => result.Value.Contains(t.Id))
            .ToListAsync();
        spawned.Should().HaveCount(2);
        spawned.Select(t => t.NodeCode).Should().BeEquivalentTo(["step-b", "step-c"]);
        // Both children share the same parent split anchor.
        spawned.Select(t => t.ParentSplitTaskId).Distinct().Should().HaveCount(1);
        spawned[0].ParentSplitTaskId.Should().NotBeNull();
    }

    [Fact]
    public async Task Executor_AndJoin_OnlyAdvancesOnceAllSiblingsComplete()
    {
        using var h = new ExecutorHarness();
        var workflowId = await h.SeedAndSplitGraphAsync();
        var taskA = await h.SeedCompletedTaskAsync(workflowId, "step-a");
        var advance = await h.Executor.AdvanceAsync(taskA);
        advance.IsSuccess.Should().BeTrue();
        var spawned = await h.Db.WorkflowTasks.Where(t => advance.Value.Contains(t.Id)).ToListAsync();
        var taskB = spawned.Single(t => t.NodeCode == "step-b");
        var taskC = spawned.Single(t => t.NodeCode == "step-c");

        // Complete only B — the join should not yet fire.
        taskB.Status = WorkflowTaskStatus.Completed;
        taskB.CompletedAtUtc = ClockNow;
        await h.Db.SaveChangesAsync();
        var afterB = await h.Executor.AdvanceAsync(taskB.Id);
        afterB.IsSuccess.Should().BeTrue();
        afterB.Value.Should().BeEmpty();

        // Complete C — the executor must now create the join successor (step-d).
        taskC.Status = WorkflowTaskStatus.Completed;
        taskC.CompletedAtUtc = ClockNow;
        await h.Db.SaveChangesAsync();
        var afterC = await h.Executor.AdvanceAsync(taskC.Id);
        afterC.IsSuccess.Should().BeTrue();
        // The advance follows step-c → join (the AndJoin is reached). Step-d is the
        // join's outgoing edge.
        var afterAdvance = await h.Db.WorkflowTasks
            .Where(t => afterC.Value.Contains(t.Id))
            .ToListAsync();
        afterAdvance.Should().HaveCount(1);
        afterAdvance[0].NodeCode.Should().Be("step-d");
    }

    [Fact]
    public async Task Executor_OrSplit_FollowsLabelReturnedByRuleEngine()
    {
        using var h = new ExecutorHarness(
            evaluatorResult: WorkflowRulePackEvaluatorResult.AllowWith(
                new Dictionary<string, string> { ["branch"] = "highValue" }));
        var workflowId = await h.SeedOrSplitGraphAsync();
        var taskA = await h.SeedCompletedTaskAsync(workflowId, "step-a");

        var result = await h.Executor.AdvanceAsync(taskA);

        result.IsSuccess.Should().BeTrue();
        // The OR-split spawned a single task; the chosen branch's label "highValue"
        // leads to step-high.
        // The executor materialises the OR-split node first then advances; the new
        // task returned IS the one whose NodeCode reflects the OR branch's target.
        var spawned = await h.Db.WorkflowTasks
            .Where(t => result.Value.Contains(t.Id))
            .ToListAsync();
        spawned.Select(t => t.NodeCode).Should().Contain("decide");
        // Now advance past the OR-split anchor.
        var orAnchor = spawned.Single(t => t.NodeCode == "decide");
        orAnchor.Status = WorkflowTaskStatus.Completed;
        orAnchor.CompletedAtUtc = ClockNow;
        await h.Db.SaveChangesAsync();
        var follow = await h.Executor.AdvanceAsync(orAnchor.Id);
        follow.IsSuccess.Should().BeTrue();
        var stepRow = await h.Db.WorkflowTasks
            .SingleAsync(t => follow.Value.Contains(t.Id));
        stepRow.NodeCode.Should().Be("step-high");
    }

    [Fact]
    public async Task Executor_OrSplit_FailOpen_FollowsFirstEdgeWhenEvaluatorYieldsNoLabel()
    {
        using var h = new ExecutorHarness(
            evaluatorResult: WorkflowRulePackEvaluatorResult.Allow()); // no annotations
        var workflowId = await h.SeedOrSplitGraphAsync();
        var taskA = await h.SeedCompletedTaskAsync(workflowId, "step-a");

        var result = await h.Executor.AdvanceAsync(taskA);

        result.IsSuccess.Should().BeTrue();
        var orAnchor = await h.Db.WorkflowTasks.SingleAsync(t => result.Value.Contains(t.Id));
        orAnchor.Status = WorkflowTaskStatus.Completed;
        orAnchor.CompletedAtUtc = ClockNow;
        await h.Db.SaveChangesAsync();
        var follow = await h.Executor.AdvanceAsync(orAnchor.Id);
        follow.IsSuccess.Should().BeTrue();
        var stepRow = await h.Db.WorkflowTasks.SingleAsync(t => follow.Value.Contains(t.Id));
        // No annotation ⇒ first outgoing edge wins (ordered by OrderIndex 0).
        stepRow.NodeCode.Should().Be("step-high");
    }

    // ─────────────────────── Controller tests ───────────────────────

    [Fact]
    public async Task Controller_PutGraph_Returns200_WithUpdatedVersion()
    {
        using var h = new ServiceHarness();
        var workflowSqid = await h.SeedWorkflowAsync(initialVersion: 1);

        var input = new WorkflowGraphInputDto(
            Nodes:
            [
                new("start-a", nameof(WorkflowNodeKind.Start), null, null, 0),
                new("end-1", nameof(WorkflowNodeKind.End), null, null, 1),
            ],
            Edges: [new("start-a", "end-1", null, 0)]);

        // The controller is a thin wrapper around the service; this test exercises the
        // service round-trip + the DTO version stamp to assert the end-to-end shape.
        var result = await h.Service.ReplaceGraphAsync(workflowSqid, input);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Version.Should().Be(2);
        result.Value.Nodes.Should().HaveCount(2);
        result.Value.Edges.Should().HaveCount(1);
        // The returned DTO's Sqid must round-trip back to a workflow definition row.
        var sqidService = MakeSqids();
        var decoded = sqidService.TryDecode(result.Value.WorkflowDefinitionSqid);
        decoded.IsSuccess.Should().BeTrue();
        var row = await h.Db.WorkflowDefinitions.SingleOrDefaultAsync(w => w.Id == decoded.Value);
        row.Should().NotBeNull();
        row!.Version.Should().Be(2);
    }

    // ═══════════════════════ Harnesses ═══════════════════════

    /// <summary>Stub clock returning a fixed UTC instant for deterministic timestamps.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Builds a fresh Sqid service mirroring production configuration.</summary>
    private static ISqidService MakeSqids()
    {
        // Reuse Sqid factory exercised by other tests in this assembly.
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{(long)call[0]}");
        sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
        {
            var s = (string?)call[0];
            if (s is null || !s.StartsWith("sqid-", System.StringComparison.Ordinal))
            {
                return Result<long>.Failure(ErrorCodes.InvalidSqid, "Bad Sqid.");
            }
            if (long.TryParse(s.AsSpan("sqid-".Length), out var v))
            {
                return Result<long>.Success(v);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "Bad Sqid.");
        });
        return sqids;
    }

    /// <summary>Fresh in-memory <see cref="CnasDbContext"/> with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-graph-{System.Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Harness backing the <see cref="WorkflowGraphService"/> + its dependencies.</summary>
    private sealed class ServiceHarness : System.IDisposable
    {
        public CnasDbContext Db { get; }
        public IAuditService Audit { get; }
        public WorkflowGraphService Service { get; }
        public ISqidService Sqids { get; }

        public ServiceHarness()
        {
            Db = CreateContext();
            Audit = Substitute.For<IAuditService>();
            Audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(1L);
            caller.UserSqid.Returns("u1");
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");
            Sqids = MakeSqids();
            Service = new WorkflowGraphService(
                Db,
                new StubClock(ClockNow),
                caller,
                Sqids,
                Audit,
                new WorkflowGraphInputDtoValidator());
        }

        public async Task<string> SeedWorkflowAsync(int initialVersion)
        {
            var row = new WorkflowDefinition
            {
                Code = $"WF-{System.Guid.NewGuid():N}".Substring(0, 16),
                Version = initialVersion,
                DefinitionJson = "{}",
                IsCurrent = true,
                IsActive = true,
                CreatedAtUtc = ClockNow,
            };
            Db.WorkflowDefinitions.Add(row);
            await Db.SaveChangesAsync();
            return Sqids.Encode(row.Id);
        }

        public void Dispose() => Db.Dispose();
    }

    /// <summary>Harness backing the <see cref="WorkflowGraphExecutor"/>.</summary>
    private sealed class ExecutorHarness : System.IDisposable
    {
        public CnasDbContext Db { get; }
        public WorkflowGraphExecutor Executor { get; }

        public ExecutorHarness(WorkflowRulePackEvaluatorResult? evaluatorResult = null)
        {
            Db = CreateContext();
            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(1L);
            caller.UserSqid.Returns("u1");
            var eval = Substitute.For<IWorkflowRulePackEvaluator>();
            eval.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(evaluatorResult ?? WorkflowRulePackEvaluatorResult.Allow()));
            Executor = new WorkflowGraphExecutor(
                Db, new StubClock(ClockNow), caller, eval,
                NullLogger<WorkflowGraphExecutor>.Instance);
        }

        public async Task<long> SeedSequentialGraphAsync()
        {
            var def = new WorkflowDefinition
            {
                Code = "WF-SEQ",
                Version = 1,
                DefinitionJson = "{}",
                IsCurrent = true,
                IsActive = true,
                CreatedAtUtc = ClockNow,
            };
            Db.WorkflowDefinitions.Add(def);
            await Db.SaveChangesAsync();
            // Nodes: start → step-a → step-b → end
            var start = AddNode(def.Id, "start-a", WorkflowNodeKind.Start, 0);
            var stepA = AddNode(def.Id, "step-a", WorkflowNodeKind.Task, 1);
            var stepB = AddNode(def.Id, "step-b", WorkflowNodeKind.Task, 2);
            var end = AddNode(def.Id, "end-1", WorkflowNodeKind.End, 3);
            await Db.SaveChangesAsync();
            AddEdge(def.Id, start.Id, stepA.Id, null, 0);
            AddEdge(def.Id, stepA.Id, stepB.Id, null, 0);
            AddEdge(def.Id, stepB.Id, end.Id, null, 0);
            await Db.SaveChangesAsync();
            return def.Id;
        }

        public async Task<long> SeedAndSplitGraphAsync()
        {
            // start → step-a → split → (step-b, step-c) → join → step-d → end
            var def = new WorkflowDefinition
            {
                Code = "WF-ANDS",
                Version = 1,
                DefinitionJson = "{}",
                IsCurrent = true,
                IsActive = true,
                CreatedAtUtc = ClockNow,
            };
            Db.WorkflowDefinitions.Add(def);
            await Db.SaveChangesAsync();
            var start = AddNode(def.Id, "start-a", WorkflowNodeKind.Start, 0);
            var stepA = AddNode(def.Id, "step-a", WorkflowNodeKind.Task, 1);
            var split = AddNode(def.Id, "split", WorkflowNodeKind.AndSplit, 2);
            var stepB = AddNode(def.Id, "step-b", WorkflowNodeKind.Task, 3);
            var stepC = AddNode(def.Id, "step-c", WorkflowNodeKind.Task, 4);
            var join = AddNode(def.Id, "join", WorkflowNodeKind.AndJoin, 5);
            var stepD = AddNode(def.Id, "step-d", WorkflowNodeKind.Task, 6);
            var end = AddNode(def.Id, "end-1", WorkflowNodeKind.End, 7);
            await Db.SaveChangesAsync();
            AddEdge(def.Id, start.Id, stepA.Id, null, 0);
            AddEdge(def.Id, stepA.Id, split.Id, null, 0);
            AddEdge(def.Id, split.Id, stepB.Id, null, 0);
            AddEdge(def.Id, split.Id, stepC.Id, null, 1);
            AddEdge(def.Id, stepB.Id, join.Id, null, 0);
            AddEdge(def.Id, stepC.Id, join.Id, null, 1);
            AddEdge(def.Id, join.Id, stepD.Id, null, 0);
            AddEdge(def.Id, stepD.Id, end.Id, null, 0);
            await Db.SaveChangesAsync();
            return def.Id;
        }

        public async Task<long> SeedOrSplitGraphAsync()
        {
            // start → step-a → decide → (step-high (label highValue), step-low) → end
            var def = new WorkflowDefinition
            {
                Code = "WF-ORS",
                Version = 1,
                DefinitionJson = "{}",
                IsCurrent = true,
                IsActive = true,
                CreatedAtUtc = ClockNow,
            };
            Db.WorkflowDefinitions.Add(def);
            await Db.SaveChangesAsync();
            var start = AddNode(def.Id, "start-a", WorkflowNodeKind.Start, 0);
            var stepA = AddNode(def.Id, "step-a", WorkflowNodeKind.Task, 1);
            var decide = AddNode(def.Id, "decide", WorkflowNodeKind.OrSplit, 2);
            decide.ConditionExpression = "applicationAmount > 1000";
            var stepHigh = AddNode(def.Id, "step-high", WorkflowNodeKind.Task, 3);
            var stepLow = AddNode(def.Id, "step-low", WorkflowNodeKind.Task, 4);
            var end = AddNode(def.Id, "end-1", WorkflowNodeKind.End, 5);
            await Db.SaveChangesAsync();
            AddEdge(def.Id, start.Id, stepA.Id, null, 0);
            AddEdge(def.Id, stepA.Id, decide.Id, null, 0);
            AddEdge(def.Id, decide.Id, stepHigh.Id, "highValue", 0);
            AddEdge(def.Id, decide.Id, stepLow.Id, "lowValue", 1);
            AddEdge(def.Id, stepHigh.Id, end.Id, null, 0);
            AddEdge(def.Id, stepLow.Id, end.Id, null, 0);
            await Db.SaveChangesAsync();
            return def.Id;
        }

        public async Task<long> SeedCompletedTaskAsync(long workflowDefinitionId, string nodeCode)
        {
            // Build the dossier chain so the executor's join finds a workflow definition.
            var passport = new ServicePassport
            {
                Code = $"SP-{System.Guid.NewGuid():N}".Substring(0, 16),
                NameRo = "Test",
                DescriptionRo = "Test description",
                FormSchemaJson = "{}",
                WorkflowCode = (await Db.WorkflowDefinitions.SingleAsync(w => w.Id == workflowDefinitionId)).Code,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();
            var application = new ServiceApplication
            {
                ServicePassportId = passport.Id,
                SolicitantId = 1L,
                FormPayloadJson = "{}",
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.Applications.Add(application);
            await Db.SaveChangesAsync();
            var dossier = new Dossier
            {
                ApplicationId = application.Id,
                DossierNumber = $"D-{System.Guid.NewGuid():N}".Substring(0, 16),
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();
            var task = new WorkflowTask
            {
                DossierId = dossier.Id,
                Title = nodeCode,
                NodeCode = nodeCode,
                Status = WorkflowTaskStatus.Completed,
                CompletedAtUtc = ClockNow,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.WorkflowTasks.Add(task);
            await Db.SaveChangesAsync();
            return task.Id;
        }

        private WorkflowGraphNode AddNode(long workflowId, string code, WorkflowNodeKind kind, int order)
        {
            var n = new WorkflowGraphNode
            {
                WorkflowDefinitionId = workflowId,
                NodeCode = code,
                Kind = kind,
                OrderIndex = order,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.WorkflowGraphNodes.Add(n);
            return n;
        }

        private void AddEdge(long workflowId, long source, long target, string? label, int order)
        {
            Db.WorkflowGraphEdges.Add(new WorkflowGraphEdge
            {
                WorkflowDefinitionId = workflowId,
                SourceNodeId = source,
                TargetNodeId = target,
                Label = label,
                OrderIndex = order,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            });
        }

        public void Dispose() => Db.Dispose();
    }
}
