using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0574 / R0592 — integration tests for the multi-level forward + return-to-
/// previous branches on <see cref="DecisionWorkflowService"/>. Uses EF Core
/// InMemory + NSubstitute fakes (mirrors the iter-128 harness pattern in
/// <see cref="DecisionWorkflowServiceTests"/>).
/// </summary>
public sealed class DecisionWorkflowServiceMultiLevelTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string[] DeciderRoles = ["cnas-decider"];

    [Fact]
    public async Task ForwardToNextLevelAsync_FromPendingApproval_AdvancesToSignedByDirector()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(ApplicationStatus.PendingApproval);

        var result = await harness.Service.ForwardToNextLevelAsync("APP-SQID", "Forwarded for director review.");

        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.SignedByDirector);
        await harness.Audit.Received(1).RecordAsync(
            "WORKFLOW.FORWARDED_TO_DIRECTOROFDIRECTORATE",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(ServiceApplication), seeded.AppId,
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForwardToNextLevelAsync_FromSignedByDirector_AdvancesToApproved()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(ApplicationStatus.SignedByDirector);

        var result = await harness.Service.ForwardToNextLevelAsync("APP-SQID", "Final approval.");

        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Approved);
    }

    [Fact]
    public async Task ForwardToNextLevelAsync_FromApproved_ReturnsAlreadyAtTop()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(ApplicationStatus.Approved);

        var result = await harness.Service.ForwardToNextLevelAsync("APP-SQID", "Try forwarding from top.");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.WorkflowAlreadyAtTop);
    }

    [Fact]
    public async Task ForwardToNextLevelAsync_FromUnderExamination_ReturnsNotOnApprovalChain()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(ApplicationStatus.UnderExamination);

        var result = await harness.Service.ForwardToNextLevelAsync("APP-SQID", "Premature forward.");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.WorkflowNotOnApprovalChain);
    }

    [Fact]
    public async Task ForwardToNextLevelAsync_NonDecider_RejectedByRoleGate()
    {
        var harness = Harness.Create(roles: ["cnas-examiner"]);
        await harness.SeedAsync(ApplicationStatus.PendingApproval);

        var result = await harness.Service.ForwardToNextLevelAsync("APP-SQID", "ok");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.WorkflowNotDecider);
    }

    [Fact]
    public async Task ReturnToPreviousStepAsync_FromApproved_ReturnsToSignedByDirector()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(ApplicationStatus.Approved);

        var result = await harness.Service.ReturnToPreviousStepAsync("APP-SQID", "Send back to director.");

        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.SignedByDirector);
        await harness.Audit.Received(1).RecordAsync(
            "WORKFLOW.RETURNED",
            AuditSeverity.Notice,
            Arg.Any<string>(), nameof(ServiceApplication), seeded.AppId,
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnToPreviousStepAsync_FromSignedByDirector_ReturnsToPendingApproval()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(ApplicationStatus.SignedByDirector);

        var result = await harness.Service.ReturnToPreviousStepAsync("APP-SQID", "Director sends back.");

        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.PendingApproval);
    }

    [Fact]
    public async Task ReturnToPreviousStepAsync_FromPendingApproval_AtFloor_RejectsApplication()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(ApplicationStatus.PendingApproval);

        var result = await harness.Service.ReturnToPreviousStepAsync("APP-SQID", "Nowhere to return.");

        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Rejected);
        await harness.Audit.Received(1).RecordAsync(
            "WORKFLOW.RETURNED_AT_FLOOR",
            AuditSeverity.Notice,
            Arg.Any<string>(), nameof(ServiceApplication), seeded.AppId,
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnToPreviousStepAsync_FromDraft_ReturnsNotOnApprovalChain()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(ApplicationStatus.Draft);

        var result = await harness.Service.ReturnToPreviousStepAsync("APP-SQID", "Bad state.");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.WorkflowNotOnApprovalChain);
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-decision-multi-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record SeedResult(long AppId);

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required DecisionWorkflowService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required ISqidService Sqids { get; init; }

        public static Harness Create(IReadOnlyCollection<string>? roles = null)
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-CALLER");
            caller.UserId.Returns(1L);
            caller.Roles.Returns(roles ?? DeciderRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-multi");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var service = new DecisionWorkflowService(
                db, sqids, clock, caller, audit, mcabinet, NullLogger<DecisionWorkflowService>.Instance);
            return new Harness { Db = db, Service = service, Audit = audit, Sqids = sqids };
        }

        public async Task<SeedResult> SeedAsync(ApplicationStatus initialStatus)
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = "2000000000007",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Test",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP-MULTI",
                NameRo = "Test multi",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-TEST",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow,
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = initialStatus,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                SubmittedAtUtc = ClockNow.AddDays(-1),
                ReferenceNumber = "PS-MULTI-0001",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Sqids.TryDecode("APP-SQID").Returns(Result<long>.Success(app.Id));
            return new SeedResult(app.Id);
        }
    }
}
