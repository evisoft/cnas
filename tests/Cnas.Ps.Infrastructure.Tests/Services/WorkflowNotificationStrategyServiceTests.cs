using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
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
/// R0128 / R0173 — CRUD service tests for <see cref="WorkflowNotificationStrategyService"/>.
/// Covers upsert create + update flows, audit emission, validator integration, and the
/// resolver invalidation side-effect.
/// </summary>
public class WorkflowNotificationStrategyServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
    private const string ValidEvent = "Task.Assigned";

    private static readonly IReadOnlyList<string> EmailInAppChannels = new[] { "Email", "InApp" };
    private static readonly IReadOnlyList<string> InAppOnlyChannels = new[] { "InApp" };
    private static readonly IReadOnlyList<string> EmailOnlyChannels = new[] { "Email" };
    private static readonly IReadOnlyList<string> EmptyChannels = Array.Empty<string>();
    private static readonly IReadOnlyList<string> AssigneeRole = new[] { "Assignee" };
    private static readonly IReadOnlyList<string> AssigneeApplicantRoles = new[] { "Assignee", "Applicant" };
    private static readonly IReadOnlyList<string> CustomGroupNoCodeRole = new[] { "CustomGroup:" };
    private static readonly IReadOnlyList<string> BogusRole = new[] { "BogusRole" };

    private static WorkflowNotificationStrategyUpsertInput ValidUpsert() => new(
        IsEnabled: true,
        Channels: EmailInAppChannels,
        RecipientRoles: AssigneeRole,
        TemplateCodeOverride: null,
        QuietHoursStart: null,
        QuietHoursEnd: null,
        Description: "test");

    [Fact]
    public async Task Upsert_WritesCriticalAuditRow_OnCreate()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.UpsertAsync(
            harness.WorkflowSqid, ValidEvent, ValidUpsert());

        result.IsSuccess.Should().BeTrue();
        await harness.Audit.Received(1).RecordAsync(
            eventCode: "WORKFLOW.NOTIFY.STRATEGY.CREATED",
            severity: AuditSeverity.Critical,
            actorId: Arg.Any<string>(),
            targetEntity: nameof(WorkflowNotificationStrategy),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upsert_OnSecondCall_WritesUpdatedAuditRow()
    {
        var harness = await Harness.CreateAsync();
        await harness.Service.UpsertAsync(harness.WorkflowSqid, ValidEvent, ValidUpsert());
        harness.Audit.ClearReceivedCalls();

        var second = await harness.Service.UpsertAsync(
            harness.WorkflowSqid,
            ValidEvent,
            new WorkflowNotificationStrategyUpsertInput(
                IsEnabled: true,
                Channels: InAppOnlyChannels,
                RecipientRoles: AssigneeApplicantRoles,
                TemplateCodeOverride: "OVERRIDE_T1",
                QuietHoursStart: null,
                QuietHoursEnd: null,
                Description: "updated"));

        second.IsSuccess.Should().BeTrue();
        await harness.Audit.Received(1).RecordAsync(
            eventCode: "WORKFLOW.NOTIFY.STRATEGY.UPDATED",
            severity: AuditSeverity.Critical,
            actorId: Arg.Any<string>(),
            targetEntity: nameof(WorkflowNotificationStrategy),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());

        // Verify the row's template override was applied — confirms update path took effect.
        var row = await harness.Db.WorkflowNotificationStrategies.SingleAsync();
        row.TemplateCodeOverride.Should().Be("OVERRIDE_T1");
        row.RecipientRoles.Should().BeEquivalentTo(AssigneeApplicantRoles);
    }

    [Fact]
    public async Task Upsert_UnknownEventCode_ReturnsValidationFailed()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.UpsertAsync(
            harness.WorkflowSqid, "Bogus.Event", ValidUpsert());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Upsert_RoleListWithCustomGroupMissingCode_FailsValidation()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.UpsertAsync(
            harness.WorkflowSqid,
            ValidEvent,
            new WorkflowNotificationStrategyUpsertInput(
                IsEnabled: true,
                Channels: EmailOnlyChannels,
                RecipientRoles: CustomGroupNoCodeRole, // missing code suffix
                TemplateCodeOverride: null,
                QuietHoursStart: null,
                QuietHoursEnd: null,
                Description: null));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Upsert_EnabledStrategyWithEmptyChannels_FailsValidation()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.UpsertAsync(
            harness.WorkflowSqid,
            ValidEvent,
            new WorkflowNotificationStrategyUpsertInput(
                IsEnabled: true,
                Channels: EmptyChannels,
                RecipientRoles: AssigneeRole,
                TemplateCodeOverride: null,
                QuietHoursStart: null,
                QuietHoursEnd: null,
                Description: null));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>Deterministic clock used across tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Wires the service under test against an in-memory DB shared with the resolver.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required WorkflowNotificationStrategyService Service { get; init; }
        public required WorkflowNotificationStrategyResolver Resolver { get; init; }
        public required IAuditService Audit { get; init; }
        public required string WorkflowSqid { get; init; }
        public required long WorkflowId { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var dbName = $"cnas-wf-notify-svc-{Guid.NewGuid():N}";
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            var provider = services.BuildServiceProvider();

            var standaloneOpts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(standaloneOpts);

            // Seed a workflow definition so the upsert FK check passes.
            var workflow = new WorkflowDefinition
            {
                Code = "WF-TEST",
                Version = 1,
                DefinitionJson = "{}",
                IsCurrent = true,
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            };
            db.WorkflowDefinitions.Add(workflow);
            await db.SaveChangesAsync();

            // Sqid mock — encodes the workflow id as a predictable string.
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

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(1001L);
            caller.UserSqid.Returns("SQID-1001");
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var resolver = new WorkflowNotificationStrategyResolver(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorkflowNotificationStrategyResolver>.Instance);

            IValidator<WorkflowNotificationStrategyUpsertInput> validator =
                new WorkflowNotificationStrategyUpsertInputValidator();

            var service = new WorkflowNotificationStrategyService(
                db, caller, sqids, new StubClock(ClockNow), audit, resolver, validator);

            return new Harness
            {
                Db = db,
                Service = service,
                Resolver = resolver,
                Audit = audit,
                WorkflowSqid = $"SQID-{workflow.Id}",
                WorkflowId = workflow.Id,
            };
        }
    }
}
