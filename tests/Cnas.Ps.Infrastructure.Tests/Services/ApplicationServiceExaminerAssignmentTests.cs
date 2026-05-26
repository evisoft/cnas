using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Services.ApplicationProcessing;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0570 / TOR CF 08.02 — regression tests pinning the wiring of
/// <see cref="RoundRobinExaminerAssignmentService"/> into
/// <see cref="ApplicationServiceImpl.SubmitAsync"/>. Specifically:
/// the registrar (the authenticated caller) MUST NEVER be assigned as
/// the examiner of their own submission, and an empty eligible pool
/// MUST surface as <see cref="ErrorCodes.ApplicationNoAvailableExaminer"/>
/// without persisting the cerere.
/// </summary>
public sealed class ApplicationServiceExaminerAssignmentTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 24, 13, 0, 0, DateTimeKind.Utc);
    private const string OperatorIdnp = "2000000000007";
    private const string PassportSqid = "PSP-1";
    private const string ExaminerRole = "cnas-examiner";

    /// <summary>Stable role array used for the registrar's <c>caller.Roles</c> stub.</summary>
    private static readonly string[] RegistrarRoles = ["cnas-user"];

    /// <summary>
    /// R0570 — even when the registrar carries the examiner role themselves
    /// the assignment service must select a DIFFERENT examiner so the cerere
    /// is never self-examined.
    /// </summary>
    [Fact]
    public async Task SubmitAsync_RegistrarHasExaminerRole_AssignsDifferentExaminer()
    {
        var harness = await Harness.CreateAsync(registrarIsExaminer: true);

        var input = new SubmitApplicationInput(
            ServicePassportId: PassportSqid,
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>());

        var result = await harness.Service.SubmitAsync(input);

        result.IsSuccess.Should().BeTrue();
        var app = await harness.Db.Applications.SingleAsync();
        app.AssignedExaminerUserId.Should().NotBeNull();
        app.AssignedExaminerUserId.Should().NotBe(harness.RegistrarId,
            "CF 08.02 forbids the registrar from being their own examiner");
        app.AssignedExaminerUserId.Should().Be(harness.OtherExaminerId);
    }

    /// <summary>
    /// R0570 — when no eligible examiner remains after the registrar
    /// exclusion, the submission MUST fail with
    /// <see cref="ErrorCodes.ApplicationNoAvailableExaminer"/> and no
    /// application row is persisted.
    /// </summary>
    [Fact]
    public async Task SubmitAsync_NoExaminerPool_FailsNoAvailableExaminer_AndDoesNotPersist()
    {
        var harness = await Harness.CreateAsync(registrarIsExaminer: true, seedOtherExaminer: false);

        var input = new SubmitApplicationInput(
            ServicePassportId: PassportSqid,
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>());

        var result = await harness.Service.SubmitAsync(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApplicationNoAvailableExaminer);
        (await harness.Db.Applications.CountAsync()).Should().Be(0,
            "an empty examiner pool must abort BEFORE persisting any cerere row");
    }

    // ────────────────────────── Harness ──────────────────────────

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required ApplicationServiceImpl Service { get; init; }
        public long RegistrarId { get; set; }
        public long? OtherExaminerId { get; set; }

        public static async Task<Harness> CreateAsync(bool registrarIsExaminer, bool seedOtherExaminer = true)
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            // Seed the registrar UserProfile FIRST and use its auto-assigned id
            // as the "anchor" id for the matching Solicitant row. The two
            // tables have independent EF identity counters, so we have to
            // align them explicitly to mirror the test pattern used elsewhere
            // (the SubmitAsync caller resolution looks up the Solicitant via
            // userId == Solicitant.Id, so the two rows must share an id).
            var registrarUser = new UserProfile
            {
                DisplayName = "Registrar",
                PreferredLanguage = "ro",
                IsActive = true,
                State = UserAccountState.Active,
                Roles = registrarIsExaminer ? [ExaminerRole] : [],
                Groups = [],
                CreatedAtUtc = ClockNow,
            };
            db.UserProfiles.Add(registrarUser);
            await db.SaveChangesAsync();

            var registrar = new Solicitant
            {
                Id = registrarUser.Id,
                NationalId = OperatorIdnp,
                NationalIdHash = "h-reg",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Registrar",
                PreferredLanguage = "ro",
                IsActive = true,
                CreatedAtUtc = ClockNow,
            };
            db.Solicitants.Add(registrar);

            var passport = new ServicePassport
            {
                Code = "SP-X",
                NameRo = "Test",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-X",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsProactive = false,
                DecisionRulesJson = "{\"code\":\"TEST\"}",
                IsActive = true,
                CreatedAtUtc = ClockNow,
            };
            db.ServicePassports.Add(passport);
            await db.SaveChangesAsync();

            long? otherExaminerId = null;
            if (seedOtherExaminer)
            {
                var other = new UserProfile
                {
                    DisplayName = "Other Examiner",
                    PreferredLanguage = "ro",
                    IsActive = true,
                    State = UserAccountState.Active,
                    Roles = [ExaminerRole],
                    Groups = [],
                    CreatedAtUtc = ClockNow,
                };
                db.UserProfiles.Add(other);
                await db.SaveChangesAsync();
                otherExaminerId = other.Id;
            }

            sqids.TryDecode(PassportSqid).Returns(Result<long>.Success(passport.Id));

            var clock = new StubClock(ClockNow);
            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(registrar.Id);
            caller.UserSqid.Returns($"SQID-{registrar.Id}");
            caller.Roles.Returns(RegistrarRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("c1");

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

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var assignment = new RoundRobinExaminerAssignmentService(db, clock);
            var logger = Substitute.For<ILogger<ApplicationServiceImpl>>();

            var service = new ApplicationServiceImpl(
                db, sqids, clock, caller, audit, notify, mcabinet, logger,
                IdHashHelper.Instance, assignment);

            return new Harness
            {
                Db = db,
                Service = service,
                RegistrarId = registrar.Id,
                OtherExaminerId = otherExaminerId,
            };
        }
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-app-rr-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }
}
