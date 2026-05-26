using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests for the MCabinet outbound publish wiring on
/// <see cref="ApplicationServiceImpl.WithdrawAsync"/>. Withdrawal is the only solicitant-
/// initiated terminal transition that does NOT pass through Approved / Rejected; the
/// citizen-portal card must still receive a <see cref="MCabinetStatus.Closed"/> revision
/// so the dashboard shows the dossier as finalised. The publish is best-effort: a
/// publisher failure must not break the withdraw flow (CLAUDE.md cross-cutting
/// "Idempotent Callbacks").
/// </summary>
public class ApplicationServiceWithdrawTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 11, 0, 0, DateTimeKind.Utc);
    private const string OwnerIdnp = "2000000000007";
    private const string OtherIdnp = "2000000000099";
    private const string PassportCode = "SP-001-WITHDRAW";
    private const string PassportNameRo = "Withdraw test passport";
    private const string ApplicationSqid = "APP-SQID-1";
    private const string UnknownSqid = "UNKNOWN-SQID";

    private static readonly string[] CallerRoles = ["cnas-applicant"];

    // ─────────────────────── Tests ───────────────────────

    /// <summary>
    /// The happy path: a Submitted application owned by the caller is withdrawn, the
    /// row is flipped to <see cref="ApplicationStatus.Withdrawn"/> with <c>ClosedAtUtc</c>
    /// stamped, and the MCabinet publisher receives a single <see cref="MCabinetStatus.Closed"/>
    /// card whose <see cref="MCabinetCard.ExternalId"/> matches the application Sqid used
    /// at submission time (so MCabinet treats it as an update of the existing card rather
    /// than a new one — see CLAUDE.md "Idempotent Callbacks").
    /// </summary>
    [Fact]
    public async Task WithdrawAsync_OwnedApplication_SetsClosedAndPublishesCard()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(ApplicationStatus.Submitted, ownerSolicitantId: null);

        var result = await harness.Service.WithdrawAsync(ApplicationSqid);

        result.IsSuccess.Should().BeTrue();

        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Withdrawn);
        app.ClosedAtUtc.Should().Be(ClockNow);

        await harness.MCabinet.Received(1).PublishCardAsync(
            Arg.Is<MCabinetCard>(c =>
                c.ExternalId == ApplicationSqid
                && c.CitizenIdnp == OwnerIdnp
                && c.ServiceCode == PassportCode
                && c.Status == MCabinetStatus.Closed
                && c.TitleRo == PassportNameRo
                && c.EventUtc == ClockNow),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An already-Approved application cannot be withdrawn — the service must short-
    /// circuit with <see cref="ErrorCodes.ApplicationLocked"/> and must NOT call the
    /// MCabinet publisher (no spurious card revisions for invalid transitions).
    /// </summary>
    [Fact]
    public async Task WithdrawAsync_AlreadyApproved_ReturnsApplicationLockedAndDoesNotPublish()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(ApplicationStatus.Approved, ownerSolicitantId: null);

        var result = await harness.Service.WithdrawAsync(ApplicationSqid);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApplicationLocked);
        await harness.MCabinet.DidNotReceive().PublishCardAsync(
            Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Best-effort projection — when the MCabinet publisher returns a failed
    /// <see cref="Result"/>, the withdraw flow must still succeed. The application row
    /// remains terminally Withdrawn and the caller sees Success; only a structured
    /// warning is logged.
    /// </summary>
    [Fact]
    public async Task WithdrawAsync_PublisherFails_StillReturnsSuccess()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync(ApplicationStatus.Submitted, ownerSolicitantId: null);
        harness.MCabinet
            .PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.MCabinetPublishFailed, "Upstream MCabinet down."));

        var result = await harness.Service.WithdrawAsync(ApplicationSqid);

        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Withdrawn);
        app.ClosedAtUtc.Should().Be(ClockNow);
    }

    /// <summary>
    /// Defense in depth — even if the publisher throws an unhandled exception, the
    /// withdraw transition must commit and the caller must see Success. The wiring
    /// wraps the call in try/catch and logs a warning.
    /// </summary>
    [Fact]
    public async Task WithdrawAsync_PublisherThrows_DoesNotCrash()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(ApplicationStatus.Submitted, ownerSolicitantId: null);
        harness.MCabinet
            .PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new InvalidOperationException("boom"));

        var result = await harness.Service.WithdrawAsync(ApplicationSqid);

        result.IsSuccess.Should().BeTrue();
        harness.Logger.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(ILogger.Log)
                      && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning)
            .Should().BeTrue();
    }

    /// <summary>
    /// A non-owner caller must NOT be able to withdraw someone else's application —
    /// the service returns <see cref="ErrorCodes.Forbidden"/> and must NOT publish
    /// any MCabinet card revision for the unrelated dossier.
    /// </summary>
    [Fact]
    public async Task WithdrawAsync_NotOwner_ReturnsForbiddenAndDoesNotPublish()
    {
        var harness = Harness.Create();
        // Seed the application with a different owning Solicitant than the caller.
        await harness.SeedAsync(ApplicationStatus.Submitted, ownerSolicitantId: null, ownerIdnp: OtherIdnp, makeCallerOwn: false);

        var result = await harness.Service.WithdrawAsync(ApplicationSqid);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        await harness.MCabinet.DidNotReceive().PublishCardAsync(
            Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Looking up a non-existent Sqid must short-circuit with
    /// <see cref="ErrorCodes.NotFound"/> before the MCabinet publisher is ever consulted.
    /// </summary>
    [Fact]
    public async Task WithdrawAsync_UnknownSqid_ReturnsNotFoundAndDoesNotPublish()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(ApplicationStatus.Submitted, ownerSolicitantId: null);
        // Override the Sqid mapping: UnknownSqid decodes to an id that has no row.
        harness.Sqids.TryDecode(UnknownSqid).Returns(Result<long>.Success(9999L));

        var result = await harness.Service.WithdrawAsync(UnknownSqid);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        await harness.MCabinet.DidNotReceive().PublishCardAsync(
            Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>());
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Builds a unique in-memory EF Core context per test (no cross-test leakage).</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-mc-withdraw-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Fixed-time clock substitute so tests can pin <c>EventUtc</c> assertions.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <summary>The frozen "now" value injected at construction.</summary>
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Seeded ids returned by <see cref="Harness.SeedAsync"/>.</summary>
    /// <param name="AppId">Database PK of the seeded ServiceApplication row.</param>
    /// <param name="SolicitantId">Database PK of the seeded Solicitant row.</param>
    private sealed record SeedResult(long AppId, long SolicitantId);

    /// <summary>
    /// Per-test substitute graph. Each property exposes the substitute / context for
    /// fine-grained Arrange/Assert. Constructed via <see cref="Create"/>.
    /// </summary>
    private sealed class Harness
    {
        /// <summary>EF Core in-memory context used by the service under test.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>Service under test, fully wired.</summary>
        public required ApplicationServiceImpl Service { get; init; }

        /// <summary>MCabinet publisher substitute — receive-assertions live here.</summary>
        public required IMCabinetPublisher MCabinet { get; init; }

        /// <summary>Logger substitute — used to assert that warnings were emitted on the catch path.</summary>
        public required ILogger<ApplicationServiceImpl> Logger { get; init; }

        /// <summary>Caller context substitute — controls authenticated user id / roles.</summary>
        public required ICallerContext Caller { get; init; }

        /// <summary>Sqid service substitute — controls encode / decode mappings per test.</summary>
        public required ISqidService Sqids { get; init; }

        /// <summary>Creates a fresh harness with default success-path stubs on every collaborator.</summary>
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
            caller.CorrelationId.Returns("corr-withdraw");
            caller.UserSqid.Returns("SQID-OWNER");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var logger = Substitute.For<ILogger<ApplicationServiceImpl>>();

            // R0570 — the withdraw tests don't exercise the round-robin selection;
            // wire an always-success stub so SubmitAsync proceeds past the
            // examiner-assignment gate when seed paths submit applications.
            var examinerAssignment = Substitute.For<Cnas.Ps.Application.UseCases.IExaminerAssignmentService>();
            examinerAssignment
                .AssignExaminerAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<long>.Success(999L)));

            var service = new ApplicationServiceImpl(
                db, sqids, clock, caller, audit, notify, mcabinet, logger,
                Cnas.Ps.Infrastructure.Tests.TestHelpers.IdHashHelper.Instance,
                examinerAssignment);

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
        /// Seeds an owner Solicitant, an active ServicePassport, and a ServiceApplication
        /// in the requested status. Wires the Caller and Sqid substitutes so the service
        /// resolves the application through <see cref="ApplicationSqid"/>.
        /// </summary>
        /// <param name="status">Initial application status to seed.</param>
        /// <param name="ownerSolicitantId">Unused (placeholder for future explicit-id seeding).</param>
        /// <param name="ownerIdnp">IDNP of the owning Solicitant; defaults to the test-default owner.</param>
        /// <param name="makeCallerOwn">
        /// When true (default), the caller is set to the owning Solicitant — happy path.
        /// When false, the caller is a different user — used by the not-owner test.
        /// </param>
        public async Task<SeedResult> SeedAsync(
            ApplicationStatus status,
            long? ownerSolicitantId,
            string ownerIdnp = OwnerIdnp,
            bool makeCallerOwn = true)
        {
            _ = ownerSolicitantId; // reserved for future use

            var owner = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = ownerIdnp,
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Owner User",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(owner);

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

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow,
                CreatedBy = "SQID-OWNER",
                SolicitantId = owner.Id,
                ServicePassportId = passport.Id,
                Status = status,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                SubmittedAtUtc = ClockNow,
                ReferenceNumber = "PS-TEST-WITHDRAW",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            // Wire the Sqid mapping so that ApplicationSqid resolves to the seeded row.
            Sqids.TryDecode(ApplicationSqid).Returns(Result<long>.Success(app.Id));
            Sqids.Encode(app.Id).Returns(ApplicationSqid);

            if (makeCallerOwn)
            {
                Caller.UserId.Returns(owner.Id);
            }
            else
            {
                // A different solicitant id — the caller is NOT the owner.
                Caller.UserId.Returns(owner.Id + 1);
            }

            return new SeedResult(app.Id, owner.Id);
        }
    }
}
