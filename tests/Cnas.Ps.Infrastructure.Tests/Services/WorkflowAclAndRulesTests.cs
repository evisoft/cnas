using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Application.WorkflowAcl;
using Cnas.Ps.Application.WorkflowRules;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0124 / R0126 — TDD coverage for the workflow ACL + rule engine wiring. Each test
/// targets one observable behaviour: the super-admin override, the workflow-level
/// gate, the step-level refinement, the cache-invalidation seam, the rule engine's
/// pack-not-configured pass-through, the failure-containment translation, the
/// telemetry counter, the task-completion wiring, and the CRUD audit emission.
/// </summary>
public class WorkflowAclAndRulesTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── ACL service tests ───────────────────────

    [Fact]
    public async Task Acl_SuperAdmin_BypassesAllChecks()
    {
        // ARRANGE — workflow has a roles allow-list, step has a permission requirement,
        // and the caller carries NEITHER. The super-admin role on the user must trump
        // both checks.
        using var h = new AclHarness();
        var workflowId = await h.SeedWorkflowAsync(allowedRoles: ["cnas-decider"], allowedGroups: []);
        await h.SeedStepAclAsync(workflowId, "STEP-A", requiredRoles: ["very-strict"], requiredPermission: "Strict.Permission");
        var userId = await h.SeedUserAsync(roles: [WorkflowAclConstants.SuperAdminRole], groups: []);
        await h.Resolver.InvalidateAsync();

        // ACT
        var canHandle = await h.Resolver.CanHandleAsync(workflowId, "STEP-A", userId);

        // ASSERT
        canHandle.Should().BeTrue("super-admin bypasses every ACL check unconditionally");
    }

    [Fact]
    public async Task Acl_UserInWorkflowAllowedRoles_NoStepAcl_AllowsHandling()
    {
        using var h = new AclHarness();
        var workflowId = await h.SeedWorkflowAsync(allowedRoles: ["cnas-decider"], allowedGroups: []);
        var userId = await h.SeedUserAsync(roles: ["cnas-decider"], groups: []);
        await h.Resolver.InvalidateAsync();

        var canHandle = await h.Resolver.CanHandleAsync(workflowId, "STEP-A", userId);

        canHandle.Should().BeTrue();
    }

    [Fact]
    public async Task Acl_UserMissingFromWorkflowAllowedRoles_StepRolesNotConsulted_DeniesHandling()
    {
        // ARRANGE — caller is in the STEP-level RequiredRoles but NOT in the workflow
        // AllowedRoles. The workflow gate must fail first (conjunctive composition).
        using var h = new AclHarness();
        var workflowId = await h.SeedWorkflowAsync(allowedRoles: ["cnas-decider"], allowedGroups: []);
        await h.SeedStepAclAsync(workflowId, "STEP-A", requiredRoles: ["clerk"], requiredPermission: null);
        var userId = await h.SeedUserAsync(roles: ["clerk"], groups: []);
        await h.Resolver.InvalidateAsync();

        var canHandle = await h.Resolver.CanHandleAsync(workflowId, "STEP-A", userId);

        canHandle.Should().BeFalse(
            "workflow-level gate is evaluated first; step-level membership does not bypass it");
    }

    [Fact]
    public async Task Acl_WorkflowGateOpen_StepRequiresRoleUserMisses_DeniesHandling()
    {
        // ARRANGE — workflow has NO ACL (empty roles + groups = legacy fallback);
        // step ACL requires a role the caller lacks.
        using var h = new AclHarness();
        var workflowId = await h.SeedWorkflowAsync(allowedRoles: [], allowedGroups: []);
        await h.SeedStepAclAsync(workflowId, "STEP-A", requiredRoles: ["decider"], requiredPermission: null);
        var userId = await h.SeedUserAsync(roles: ["clerk"], groups: []);
        await h.Resolver.InvalidateAsync();

        var canHandle = await h.Resolver.CanHandleAsync(workflowId, "STEP-A", userId);

        canHandle.Should().BeFalse(
            "step ACL still applies even when the workflow-level gate is in legacy-fallback mode");
    }

    [Fact]
    public async Task Acl_RequiredPermissionMissingFromUserRoles_DeniesHandling()
    {
        using var h = new AclHarness();
        var workflowId = await h.SeedWorkflowAsync(allowedRoles: ["cnas-decider"], allowedGroups: []);
        await h.SeedStepAclAsync(workflowId, "STEP-A",
            requiredRoles: [], requiredPermission: "WorkflowTask.HandleDecisionStep");
        var userId = await h.SeedUserAsync(roles: ["cnas-decider"], groups: []);
        await h.Resolver.InvalidateAsync();

        var canHandle = await h.Resolver.CanHandleAsync(workflowId, "STEP-A", userId);

        canHandle.Should().BeFalse("the step-level required permission is absent from the user's roles");
    }

    [Fact]
    public async Task Acl_InvalidateAsync_PicksUpNewlyInsertedStepAcl()
    {
        // ARRANGE — initial empty snapshot, caller would be allowed by the workflow gate.
        using var h = new AclHarness();
        var workflowId = await h.SeedWorkflowAsync(allowedRoles: ["cnas-decider"], allowedGroups: []);
        var userId = await h.SeedUserAsync(roles: ["cnas-decider"], groups: []);
        await h.Resolver.InvalidateAsync();

        (await h.Resolver.CanHandleAsync(workflowId, "STEP-A", userId))
            .Should().BeTrue("baseline: no step ACL means workflow gate alone applies");

        // ACT — insert a step ACL the caller fails, then invalidate.
        await h.SeedStepAclAsync(workflowId, "STEP-A",
            requiredRoles: ["very-strict"], requiredPermission: null);
        await h.Resolver.InvalidateAsync();

        // ASSERT
        var canHandle = await h.Resolver.CanHandleAsync(workflowId, "STEP-A", userId);
        canHandle.Should().BeFalse("invalidation picked up the new step ACL");
    }

    // ─────────────────────── Rule engine tests ───────────────────────

    [Fact]
    public async Task RuleEngine_NoPackCode_OnTransition_ReturnsAllow()
    {
        using var h = new RuleEngineHarness();
        var workflowId = await h.SeedWorkflowAsync(transitionPack: null);
        var taskId = await h.SeedTaskAsync(workflowId);

        var result = await h.Engine.EvaluateTransitionAsync(taskId, "from", "to", context: null);

        result.Allowed.Should().BeTrue();
        result.BlockReason.Should().BeNull();
    }

    [Fact]
    public async Task RuleEngine_EvaluatorThrows_ReturnsBlocked_WithRuleEngineErrorReason()
    {
        using var h = new RuleEngineHarness(throwOnEvaluate: true);
        var workflowId = await h.SeedWorkflowAsync(transitionPack: "FAILS");
        var taskId = await h.SeedTaskAsync(workflowId);

        var result = await h.Engine.EvaluateTransitionAsync(taskId, "from", "to", context: null);

        result.Allowed.Should().BeFalse();
        result.BlockReason.Should().Be(WorkflowAclConstants.RuleEngineErrorReason);
    }

    [Fact]
    public async Task RuleEngine_AllowVerdictWithAnnotations_PropagatesAllowed()
    {
        var annotations = new Dictionary<string, string> { ["category"] = "PENSION" };
        using var h = new RuleEngineHarness(
            overrideEvaluator: WorkflowRulePackEvaluatorResult.AllowWith(annotations));
        var workflowId = await h.SeedWorkflowAsync(transitionPack: "OK");
        var taskId = await h.SeedTaskAsync(workflowId);

        var result = await h.Engine.EvaluateTransitionAsync(taskId, "from", "to", context: null);

        result.Allowed.Should().BeTrue();
        result.Annotations.Should().NotBeNull();
        result.Annotations!.Should().ContainKey("category").WhoseValue.Should().Be("PENSION");
    }

    [Fact]
    public async Task RuleEngine_Counter_IncrementsWithStageAndAllowedTag()
    {
        using var capture = new TaggedTaggedCapture("cnas.workflow.rule.evaluated");
        using var h = new RuleEngineHarness();
        var workflowId = await h.SeedWorkflowAsync(transitionPack: "OK");
        var taskId = await h.SeedTaskAsync(workflowId);

        await h.Engine.EvaluateTransitionAsync(taskId, "from", "to", context: null);

        capture.Tuples.Should().Contain(t =>
            t.Stage == WorkflowRuleStages.Transition && t.Allowed == "True");
    }

    // ─────────────────────── Task completion wiring tests ───────────────────────

    [Fact]
    public async Task TaskComplete_AclDenial_ReturnsForbiddenWithWorkflowAclDeniedCode()
    {
        using var h = new TaskCompletionHarness();
        // The user does NOT carry the role gate; ACL denies.
        await h.SeedScenarioAsync(
            allowedRoles: ["cnas-decider"],
            stepRequiredRoles: [],
            userRoles: ["cnas-user"],
            transitionPack: null);

        var result = await h.Service.CompleteAsync(h.TaskSqid, "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(WorkflowAclConstants.WorkflowAclDeniedCode);
    }

    [Fact]
    public async Task TaskComplete_RuleBlocked_ReturnsValidationWithRuleBlockedCode()
    {
        using var h = new TaskCompletionHarness(
            ruleVerdict: WorkflowRulePackEvaluatorResult.Block("SOMETHING_WRONG"));
        // ACL passes; rule engine blocks.
        await h.SeedScenarioAsync(
            allowedRoles: ["cnas-decider"],
            stepRequiredRoles: [],
            userRoles: ["cnas-decider"],
            transitionPack: "T-PACK");

        var result = await h.Service.CompleteAsync(h.TaskSqid, "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(WorkflowAclConstants.WorkflowRuleBlockedCode);
        result.ErrorMessage.Should().Contain("SOMETHING_WRONG");
    }

    // ─────────────────────── CRUD service + controller tests ───────────────────────

    [Fact]
    public async Task StepAclService_Upsert_WritesCriticalAuditRow_StepAclUpdated()
    {
        using var h = new StepAclServiceHarness();
        var workflowId = await h.SeedWorkflowAsync();
        // First upsert is CREATED.
        var create = await h.Service.UpsertAsync(
            h.WorkflowSqidFor(workflowId), "STEP-A",
            new WorkflowStepAclUpsertInput(RequiredRoles: ["r1"], RequiredGroups: [], RequiredPermission: null, Description: null));
        create.IsSuccess.Should().BeTrue();
        h.Audit.ClearReceivedCalls();

        // Second upsert is UPDATED.
        var update = await h.Service.UpsertAsync(
            h.WorkflowSqidFor(workflowId), "STEP-A",
            new WorkflowStepAclUpsertInput(RequiredRoles: ["r1", "r2"], RequiredGroups: [], RequiredPermission: null, Description: "changed"));

        update.IsSuccess.Should().BeTrue();
        await h.Audit.Received(1).RecordAsync(
            eventCode: "WORKFLOW.STEP_ACL.UPDATED",
            severity: AuditSeverity.Critical,
            actorId: Arg.Any<string>(),
            targetEntity: nameof(WorkflowStepAcl),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Validator_RequiredPermission_BadShape_FailsValidation()
    {
        var validator = new WorkflowStepAclUpsertInputValidator();

        var result = validator.Validate(new WorkflowStepAclUpsertInput(
            RequiredRoles: [],
            RequiredGroups: [],
            RequiredPermission: "lowercase.bad",
            Description: null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(WorkflowStepAclUpsertInput.RequiredPermission));
    }

    [Fact]
    public void Validator_RequiredPermission_GoodShape_Passes()
    {
        var validator = new WorkflowStepAclUpsertInputValidator();

        var result = validator.Validate(new WorkflowStepAclUpsertInput(
            RequiredRoles: ["r1"],
            RequiredGroups: [],
            RequiredPermission: "WorkflowTask.HandleDecisionStep",
            Description: null));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task StepAclService_Upsert_ReturnsDto_WithSqidRoundTrip()
    {
        using var h = new StepAclServiceHarness();
        var workflowId = await h.SeedWorkflowAsync();

        var result = await h.Service.UpsertAsync(
            h.WorkflowSqidFor(workflowId), "STEP-A",
            new WorkflowStepAclUpsertInput(
                RequiredRoles: ["cnas-decider"],
                RequiredGroups: [],
                RequiredPermission: "WorkflowTask.HandleDecisionStep",
                Description: "decider gate"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.WorkflowDefinitionId.Should().Be(h.WorkflowSqidFor(workflowId));
        result.Value.StepCode.Should().Be("STEP-A");
        result.Value.RequiredPermission.Should().Be("WorkflowTask.HandleDecisionStep");
        result.Value.Id.Should().NotBeNullOrEmpty();
    }

    // ═══════════════════════ Harnesses ═══════════════════════

    /// <summary>Harness backing the <see cref="WorkflowAclService"/> + dependency-tree.</summary>
    private sealed class AclHarness : IDisposable
    {
        private readonly ServiceProvider _provider;
        public CnasDbContext Db { get; }
        public WorkflowAclService Resolver { get; }

        public AclHarness()
        {
            var dbName = $"cnas-acl-{Guid.NewGuid():N}";
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            _provider = services.BuildServiceProvider();

            Db = new CnasDbContext(new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            Resolver = new WorkflowAclService(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorkflowAclService>.Instance);
        }

        public async Task<long> SeedWorkflowAsync(List<string> allowedRoles, List<string> allowedGroups)
        {
            var row = new WorkflowDefinition
            {
                Code = $"WF-{Guid.NewGuid():N}".Substring(0, 16),
                Version = 1,
                DefinitionJson = "{}",
                IsCurrent = true,
                AllowedRoles = allowedRoles,
                AllowedGroups = allowedGroups,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.WorkflowDefinitions.Add(row);
            await Db.SaveChangesAsync();
            return row.Id;
        }

        public async Task SeedStepAclAsync(
            long workflowId, string stepCode,
            List<string> requiredRoles, string? requiredPermission)
        {
            Db.WorkflowStepAcls.Add(new WorkflowStepAcl
            {
                WorkflowDefinitionId = workflowId,
                StepCode = stepCode,
                RequiredRoles = requiredRoles,
                RequiredGroups = new List<string>(),
                RequiredPermission = requiredPermission,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }

        public async Task<long> SeedUserAsync(List<string> roles, List<string> groups)
        {
            var u = new UserProfile
            {
                DisplayName = "Test",
                Roles = roles,
                Groups = groups,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.UserProfiles.Add(u);
            await Db.SaveChangesAsync();
            return u.Id;
        }

        public void Dispose()
        {
            Db.Dispose();
            _provider.Dispose();
        }
    }

    /// <summary>Harness backing the <see cref="WorkflowRuleEngine"/> + a controllable evaluator.</summary>
    private sealed class RuleEngineHarness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly CnasDbContext _db;
        public WorkflowRuleEngine Engine { get; }

        public RuleEngineHarness(
            bool throwOnEvaluate = false,
            WorkflowRulePackEvaluatorResult? overrideEvaluator = null)
        {
            var dbName = $"cnas-rules-{Guid.NewGuid():N}";
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            _provider = services.BuildServiceProvider();
            _db = new CnasDbContext(new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            var evaluator = Substitute.For<IWorkflowRulePackEvaluator>();
            if (throwOnEvaluate)
            {
                evaluator.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
                    .Returns<Task<WorkflowRulePackEvaluatorResult>>(_ => throw new InvalidOperationException("boom"));
            }
            else if (overrideEvaluator is not null)
            {
                evaluator.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(overrideEvaluator));
            }
            else
            {
                evaluator.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(WorkflowRulePackEvaluatorResult.Allow()));
            }

            // Engine reads via IReadOnlyCnasDbContext; resolve a fresh scope so the
            // engine and the test share the same in-memory store.
            var scopedDb = _provider.GetRequiredService<IServiceScopeFactory>().CreateScope()
                .ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();
            Engine = new WorkflowRuleEngine(scopedDb, evaluator, NullLogger<WorkflowRuleEngine>.Instance);
        }

        public async Task<long> SeedWorkflowAsync(string? transitionPack)
        {
            var w = new WorkflowDefinition
            {
                Code = $"WF-{Guid.NewGuid():N}".Substring(0, 16),
                Version = 1,
                DefinitionJson = "{}",
                IsCurrent = true,
                TransitionRulePackCode = transitionPack,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            _db.WorkflowDefinitions.Add(w);
            await _db.SaveChangesAsync();
            return w.Id;
        }

        public async Task<long> SeedTaskAsync(long workflowId)
        {
            // Build the task → dossier → application → passport → workflow chain so the
            // engine's join finds a row.
            var passport = new ServicePassport
            {
                Code = $"SP-{Guid.NewGuid():N}".Substring(0, 16),
                NameRo = "Test",
                DescriptionRo = "Test description",
                FormSchemaJson = "{}",
                WorkflowCode = (await _db.WorkflowDefinitions.SingleAsync(w => w.Id == workflowId)).Code,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            _db.ServicePassports.Add(passport);
            await _db.SaveChangesAsync();

            var application = new ServiceApplication
            {
                ServicePassportId = passport.Id,
                SolicitantId = 1L,
                FormPayloadJson = "{}",
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            _db.Applications.Add(application);
            await _db.SaveChangesAsync();

            var dossier = new Dossier
            {
                ApplicationId = application.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}".Substring(0, 16),
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            _db.Dossiers.Add(dossier);
            await _db.SaveChangesAsync();

            var task = new WorkflowTask
            {
                DossierId = dossier.Id,
                Title = "STEP-A",
                Status = WorkflowTaskStatus.Pending,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            _db.WorkflowTasks.Add(task);
            await _db.SaveChangesAsync();
            return task.Id;
        }

        public void Dispose()
        {
            _db.Dispose();
            _provider.Dispose();
        }
    }

    /// <summary>Harness exercising the full TaskInboxService.CompleteAsync path.</summary>
    private sealed class TaskCompletionHarness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly CnasDbContext _db;
        private readonly WorkflowRulePackEvaluatorResult? _ruleVerdict;
        public TaskInboxService Service { get; private set; } = null!;
        public string TaskSqid { get; private set; } = null!;
        public long UserId { get; private set; }

        public TaskCompletionHarness(WorkflowRulePackEvaluatorResult? ruleVerdict = null)
        {
            _ruleVerdict = ruleVerdict;
            var dbName = $"cnas-taskcomplete-{Guid.NewGuid():N}";
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            _provider = services.BuildServiceProvider();
            _db = new CnasDbContext(new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
        }

        public async Task SeedScenarioAsync(
            List<string> allowedRoles,
            List<string> stepRequiredRoles,
            List<string> userRoles,
            string? transitionPack)
        {
            // Seed the user.
            var user = new UserProfile
            {
                DisplayName = "Test User",
                Roles = userRoles,
                Groups = new List<string>(),
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            _db.UserProfiles.Add(user);

            // Seed workflow + passport + application + dossier + task.
            var workflow = new WorkflowDefinition
            {
                Code = "WF-COMPLETE",
                Version = 1,
                DefinitionJson = "{}",
                IsCurrent = true,
                AllowedRoles = allowedRoles,
                AllowedGroups = new List<string>(),
                TransitionRulePackCode = transitionPack,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            _db.WorkflowDefinitions.Add(workflow);
            await _db.SaveChangesAsync();

            if (stepRequiredRoles.Count > 0)
            {
                _db.WorkflowStepAcls.Add(new WorkflowStepAcl
                {
                    WorkflowDefinitionId = workflow.Id,
                    StepCode = "STEP-A",
                    RequiredRoles = stepRequiredRoles,
                    RequiredGroups = new List<string>(),
                    CreatedAtUtc = ClockNow,
                    IsActive = true,
                });
            }
            var passport = new ServicePassport
            {
                Code = "SP-COMPLETE",
                NameRo = "Test",
                DescriptionRo = "Test desc",
                FormSchemaJson = "{}",
                WorkflowCode = workflow.Code,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            _db.ServicePassports.Add(passport);
            await _db.SaveChangesAsync();
            var application = new ServiceApplication
            {
                ServicePassportId = passport.Id,
                SolicitantId = 1L,
                FormPayloadJson = "{}",
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            _db.Applications.Add(application);
            await _db.SaveChangesAsync();
            var dossier = new Dossier
            {
                ApplicationId = application.Id,
                DossierNumber = "D-COMPLETE",
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            _db.Dossiers.Add(dossier);
            await _db.SaveChangesAsync();
            var task = new WorkflowTask
            {
                DossierId = dossier.Id,
                Title = "STEP-A",
                Status = WorkflowTaskStatus.InProgress,
                AssignedUserId = user.Id,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            _db.WorkflowTasks.Add(task);
            await _db.SaveChangesAsync();

            UserId = user.Id;
            var sqids = MakeSqids();
            TaskSqid = sqids.Encode(task.Id);

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(user.Id);
            caller.UserSqid.Returns(sqids.Encode(user.Id));
            caller.Roles.Returns(userRoles);

            var acl = new WorkflowAclService(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorkflowAclService>.Instance);
            await acl.InvalidateAsync();

            var evaluator = Substitute.For<IWorkflowRulePackEvaluator>();
            var verdict = _ruleVerdict ?? WorkflowRulePackEvaluatorResult.Allow();
            evaluator.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(verdict));

            var engineDb = _provider.GetRequiredService<IServiceScopeFactory>().CreateScope()
                .ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();
            var engine = new WorkflowRuleEngine(engineDb, evaluator, NullLogger<WorkflowRuleEngine>.Instance);

            Service = new TaskInboxService(
                _db, sqids, new StubClock(ClockNow), caller,
                audit: null, notifications: null, acl: acl, ruleEngine: engine);
        }

        public void Dispose()
        {
            _db.Dispose();
            _provider.Dispose();
        }
    }

    /// <summary>Harness exercising the <see cref="WorkflowStepAclService"/> CRUD path.</summary>
    private sealed class StepAclServiceHarness : IDisposable
    {
        public CnasDbContext Db { get; }
        public WorkflowStepAclService Service { get; }
        public WorkflowAclService Resolver { get; }
        public IAuditService Audit { get; } = Substitute.For<IAuditService>();
        public ISqidService Sqids { get; }

        public StepAclServiceHarness()
        {
            var dbName = $"cnas-stepacl-{Guid.NewGuid():N}";
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            var provider = services.BuildServiceProvider();

            Db = new CnasDbContext(new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            Sqids = MakeSqids();
            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(1001L);
            caller.UserSqid.Returns("SQID-1001");
            caller.Roles.Returns(["cnas-admin"]);
            Audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            Resolver = new WorkflowAclService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorkflowAclService>.Instance);

            IValidator<WorkflowStepAclUpsertInput> validator = new WorkflowStepAclUpsertInputValidator();
            Service = new WorkflowStepAclService(
                Db, caller, Sqids, new StubClock(ClockNow), Audit, Resolver, validator);
        }

        public async Task<long> SeedWorkflowAsync()
        {
            var w = new WorkflowDefinition
            {
                Code = "WF-CRUD",
                Version = 1,
                DefinitionJson = "{}",
                IsCurrent = true,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.WorkflowDefinitions.Add(w);
            await Db.SaveChangesAsync();
            return w.Id;
        }

        public string WorkflowSqidFor(long id) => Sqids.Encode(id);

        public void Dispose() => Db.Dispose();
    }

    // ─────────────────────── Shared test helpers ───────────────────────

    private static ISqidService MakeSqids()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
        {
            var arg = call.Arg<string?>();
            if (!string.IsNullOrEmpty(arg)
                && arg.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(arg.AsSpan(5), out var n))
            {
                return Result<long>.Success(n);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// MeterListener-based capture that records (stage, allowed) tag tuples for each
    /// measurement on <c>cnas.workflow.rule.evaluated</c>. Disposes the listener at
    /// end-of-test to clean up.
    /// </summary>
    private sealed class TaggedTaggedCapture : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly List<(string Stage, string Allowed)> _tuples = new();
        private readonly object _gate = new();

        public IReadOnlyList<(string Stage, string Allowed)> Tuples
        {
            get { lock (_gate) return _tuples.ToList(); }
        }

        public TaggedTaggedCapture(string instrumentName)
        {
            _listener = new System.Diagnostics.Metrics.MeterListener
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
                string? stage = null;
                string? allowed = null;
                foreach (var t in tags)
                {
                    if (t.Key == "stage" && t.Value is string s) stage = s;
                    if (t.Key == "allowed") allowed = t.Value?.ToString();
                }
                if (stage is not null && allowed is not null)
                {
                    lock (_gate) _tuples.Add((stage, allowed));
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
