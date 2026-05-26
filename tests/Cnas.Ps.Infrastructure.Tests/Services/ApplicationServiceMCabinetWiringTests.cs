using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests covering the MCabinet outbound publish wiring on the
/// <see cref="ApplicationServiceImpl.SubmitAsync"/> path. The publisher is substituted via
/// NSubstitute so that the tests assert orchestration (was it called, with what card) and
/// the best-effort failure semantics (a failed publish must not break the main flow).
/// </summary>
/// <remarks>
/// CLAUDE.md cross-cutting: "Idempotent Callbacks" plus the project rule that publish
/// failures are best-effort projections — the dossier state change is the source of truth,
/// not the citizen-portal card.
/// </remarks>
public class ApplicationServiceMCabinetWiringTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);
    private const string OperatorIdnp = "2000000000007";
    private const string PassportCode = "SP-001-BIRTH";
    private const string PassportNameRo = "Test passport";

    private static readonly string[] CallerRoles = ["cnas-operator"];

    // ─────────────────────── Tests ───────────────────────

    [Fact]
    public async Task SubmitAsync_Success_PublishesSubmittedCard()
    {
        var harness = Harness.Create();
        await harness.SeedAsync();

        var input = new SubmitApplicationInput(
            ServicePassportId: "PASSPORT-SQID",
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>());

        var result = await harness.Service.SubmitAsync(input);

        result.IsSuccess.Should().BeTrue();
        await harness.MCabinet.Received(1).PublishCardAsync(
            Arg.Is<MCabinetCard>(c =>
                !string.IsNullOrWhiteSpace(c.ExternalId)
                && c.CitizenIdnp == OperatorIdnp
                && c.ServiceCode == PassportCode
                && c.Status == MCabinetStatus.Submitted
                && c.TitleRo == PassportNameRo
                && c.EventUtc == ClockNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitAsync_PublisherFails_StillReturnsSuccess()
    {
        var harness = Harness.Create();
        await harness.SeedAsync();
        harness.MCabinet
            .PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.MCabinetPublishFailed, "Upstream MCabinet down."));

        var input = new SubmitApplicationInput(
            ServicePassportId: "PASSPORT-SQID",
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>());

        var result = await harness.Service.SubmitAsync(input);

        // Best-effort projection — main flow must succeed despite publisher failure.
        result.IsSuccess.Should().BeTrue();
        (await harness.Db.Applications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SubmitAsync_PersistFails_DoesNotPublish()
    {
        var harness = Harness.Create();
        // Caller is authenticated, but the requested passport Sqid maps to a row that
        // does NOT exist in the database. The service should short-circuit with NotFound
        // before persisting an application — so MCabinet must NOT be called.
        await harness.SeedSolicitantOnlyAsync();

        var input = new SubmitApplicationInput(
            ServicePassportId: "PASSPORT-SQID",
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>());

        var result = await harness.Service.SubmitAsync(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        (await harness.Db.Applications.CountAsync()).Should().Be(0);
        await harness.MCabinet.DidNotReceive().PublishCardAsync(
            Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitAsync_PublisherThrows_DoesNotCrash()
    {
        var harness = Harness.Create();
        await harness.SeedAsync();
        // Defense in depth — even if the publisher throws an unhandled exception
        // (e.g. a misconfigured Polly retry pipeline) the dossier state machine must not
        // crash. The wiring wraps the call in try/catch and logs a warning.
        harness.MCabinet
            .PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new InvalidOperationException("boom"));

        var input = new SubmitApplicationInput(
            ServicePassportId: "PASSPORT-SQID",
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>());

        var result = await harness.Service.SubmitAsync(input);

        result.IsSuccess.Should().BeTrue();
        (await harness.Db.Applications.CountAsync()).Should().Be(1);
        // The substitute logger received at least one warning. We don't pin the exact
        // message text — only that a warning was emitted, which proves the catch-block ran.
        harness.Logger.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(ILogger.Log)
                      && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning)
            .Should().BeTrue();
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-mc-app-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required ApplicationServiceImpl Service { get; init; }
        public required IMCabinetPublisher MCabinet { get; init; }
        public required ILogger<ApplicationServiceImpl> Logger { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }

        public static Harness Create()
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
            var notify = Substitute.For<INotificationService>();
            notify.EnqueueAsync(
                    Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var caller = Substitute.For<ICallerContext>();
            caller.Roles.Returns(CallerRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");
            caller.UserSqid.Returns("SQID-OP");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var logger = Substitute.For<ILogger<ApplicationServiceImpl>>();

            // R0570 — the MCabinet tests don't exercise the round-robin selection;
            // wire an always-success stub so SubmitAsync proceeds past the
            // examiner-assignment gate.
            var examinerAssignment = Substitute.For<Cnas.Ps.Application.UseCases.IExaminerAssignmentService>();
            examinerAssignment
                .AssignExaminerAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<long>.Success(999L)));

            var service = new ApplicationServiceImpl(
                db, sqids, clock, caller, audit, notify, mcabinet, logger, IdHashHelper.Instance, examinerAssignment);
            return new Harness
            {
                Db = db,
                Service = service,
                MCabinet = mcabinet,
                Logger = logger,
                Caller = caller,
                Sqids = sqids,
            };
        }

        /// <summary>
        /// Seeds only the caller's Solicitant (no passport). Used by the persist-fails-do-not-publish
        /// test where we want authentication to succeed but the passport lookup to return
        /// <see cref="ErrorCodes.NotFound"/> before any application row is persisted.
        /// </summary>
        public async Task SeedSolicitantOnlyAsync()
        {
            var op = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = OperatorIdnp,
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Operator User",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(op);
            await Db.SaveChangesAsync();

            Caller.UserId.Returns(op.Id);
            // Map the test Sqid to a non-existent passport id so the passport lookup returns
            // null and the service short-circuits with NotFound before persistence.
            Sqids.TryDecode("PASSPORT-SQID").Returns(Result<long>.Success(9999L));
        }

        /// <summary>Seeds an active passport and the caller's Solicitant.</summary>
        public async Task SeedAsync()
        {
            var op = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = OperatorIdnp,
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Operator User",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(op);

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = PassportCode,
                NameRo = PassportNameRo,
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-TEST",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsProactive = false,
                DecisionRulesJson = "{\"code\":\"TEST\"}",
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            Caller.UserId.Returns(op.Id);
            Sqids.TryDecode("PASSPORT-SQID").Returns(Result<long>.Success(passport.Id));
        }
    }
}
