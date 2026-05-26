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
/// R0939 / iter 136 — regression pins on the wiring between
/// <see cref="ApplicationServiceImpl.WithdrawAsync"/> and the centralised
/// <see cref="IApplicationStatusGuard"/>. The wire contract for clients of the
/// withdraw endpoint did NOT change in iter 136: a withdrawal from a locked
/// status STILL surfaces as <see cref="ErrorCodes.ApplicationLocked"/>, regardless
/// of whether the production guard is wired or the legacy ladder is in play. These
/// tests fix that contract so a future refactor cannot accidentally widen the
/// error surface.
/// </summary>
/// <remarks>
/// The existing <c>ApplicationServiceWithdrawTests</c> fixture exercises the
/// legacy ladder (guard is left at its <c>null</c> default). This fixture adds
/// the dual pin: same wire shape, but with the guard explicitly wired through
/// the constructor. Together they prove the iter-136 wiring is backward
/// compatible.
/// </remarks>
public class ApplicationServiceWithdrawGuardTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 11, 0, 0, DateTimeKind.Utc);
    private const string OwnerIdnp = "2000000000007";
    private const string PassportCode = "SP-001-WITHDRAW-GUARD";
    private const string PassportNameRo = "Withdraw guard passport";
    private const string ApplicationSqid = "APP-SQID-G1";

    private static readonly string[] CallerRoles = ["cnas-applicant"];

    /// <summary>
    /// When the guard is wired AND the application is in
    /// <see cref="ApplicationStatus.Approved"/>, the guard rejects the
    /// Approved → Withdrawn edge (not on the matrix). The service MUST
    /// downgrade the verdict's <c>APPLICATION.ILLEGAL_TRANSITION</c> code to
    /// the legacy <see cref="ErrorCodes.ApplicationLocked"/> shape so existing
    /// clients see no change in behavior.
    /// </summary>
    [Fact]
    public async Task WithdrawAsync_GuardWired_LockedStatus_PreservesLegacyErrorCode()
    {
        var harness = Harness.Create(wireGuard: true);
        await harness.SeedAsync(ApplicationStatus.Approved);

        var result = await harness.Service.WithdrawAsync(ApplicationSqid);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApplicationLocked,
            because: "the iter-136 guard wiring must NOT change the wire contract — "
                + "Approved still surfaces as ApplicationLocked, not APPLICATION.ILLEGAL_TRANSITION.");
        await harness.MCabinet.DidNotReceive().PublishCardAsync(
            Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When the guard is wired AND the application is in
    /// <see cref="ApplicationStatus.Submitted"/> (an allowed origin for the
    /// Withdrawn edge), the guard returns success and the service commits the
    /// withdrawal — proving that the guard wiring did not regress the happy
    /// path.
    /// </summary>
    [Fact]
    public async Task WithdrawAsync_GuardWired_AllowedTransition_Succeeds()
    {
        var harness = Harness.Create(wireGuard: true);
        var seeded = await harness.SeedAsync(ApplicationStatus.Submitted);

        var result = await harness.Service.WithdrawAsync(ApplicationSqid);

        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Withdrawn);
        app.ClosedAtUtc.Should().Be(ClockNow);
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Builds a unique in-memory EF Core context per test (no cross-test leakage).</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-mc-withdraw-guard-{Guid.NewGuid():N}")
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
    /// Test harness wiring <see cref="ApplicationServiceImpl"/> with optional
    /// <see cref="IApplicationStatusGuard"/> participation.
    /// </summary>
    private sealed class Harness
    {
        /// <summary>EF Core in-memory context used by the service under test.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>Service under test, fully wired.</summary>
        public required ApplicationServiceImpl Service { get; init; }

        /// <summary>MCabinet publisher substitute — receive-assertions live here.</summary>
        public required IMCabinetPublisher MCabinet { get; init; }

        /// <summary>Caller context substitute — controls authenticated user id / roles.</summary>
        public required ICallerContext Caller { get; init; }

        /// <summary>Sqid service substitute — controls encode / decode mappings per test.</summary>
        public required ISqidService Sqids { get; init; }

        /// <summary>
        /// Creates a fresh harness with default success-path stubs on every collaborator.
        /// When <paramref name="wireGuard"/> is true, the production
        /// <see cref="ApplicationStatusGuard"/> is wired against the read-only
        /// projection of the same in-memory context so the guard sees committed rows.
        /// </summary>
        /// <param name="wireGuard">Whether to wire the production guard.</param>
        public static Harness Create(bool wireGuard)
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
            caller.CorrelationId.Returns("corr-withdraw-guard");
            caller.UserSqid.Returns("SQID-OWNER");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var logger = Substitute.For<ILogger<ApplicationServiceImpl>>();

            var examinerAssignment = Substitute.For<IExaminerAssignmentService>();
            examinerAssignment
                .AssignExaminerAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<long>.Success(999L)));

            // The production guard reads through IReadOnlyCnasDbContext; CnasDbContext
            // implements both the write-side and read-only interfaces so the guard sees
            // the same in-memory backing store as the service under test.
            IApplicationStatusGuard? guard = wireGuard
                ? new ApplicationStatusGuard(db)
                : null;

            var service = new ApplicationServiceImpl(
                db, sqids, clock, caller, audit, notify, mcabinet, logger,
                Cnas.Ps.Infrastructure.Tests.TestHelpers.IdHashHelper.Instance,
                examinerAssignment,
                autoCreator: null,
                statusGuard: guard);

            return new Harness
            {
                Db = db,
                Service = service,
                MCabinet = mcabinet,
                Caller = caller,
                Sqids = sqids,
            };
        }

        /// <summary>
        /// Seeds an owner Solicitant + ServicePassport + ServiceApplication in the
        /// requested status, wires the Caller and Sqid substitutes so the service
        /// resolves the application through <see cref="ApplicationSqid"/>.
        /// </summary>
        /// <param name="status">Initial application status to seed.</param>
        public async Task<SeedResult> SeedAsync(ApplicationStatus status)
        {
            var owner = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = OwnerIdnp,
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
                ReferenceNumber = "PS-TEST-WGUARD",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Sqids.TryDecode(ApplicationSqid).Returns(Result<long>.Success(app.Id));
            Sqids.Encode(app.Id).Returns(ApplicationSqid);
            Caller.UserId.Returns(owner.Id);

            return new SeedResult(app.Id, owner.Id);
        }
    }
}
