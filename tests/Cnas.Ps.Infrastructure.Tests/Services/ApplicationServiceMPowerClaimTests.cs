using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
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
/// Claim-based delegation tests for <see cref="ApplicationServiceImpl.SubmitAsync"/>.
/// MPower is consumed indirectly via MPass claims surfaced through
/// <see cref="ICallerContext.OnBehalfOfPrincipalIdnp"/> and
/// <see cref="ICallerContext.DelegationPowerId"/> — not as a separate HTTP service. These
/// tests verify the inline guard that replaces the obsolete <c>IMPowerClient</c> wiring.
/// </summary>
public class ApplicationServiceMPowerClaimTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);
    private const string OperatorIdnp = "2000000000007";
    private const string PrincipalIdnp = "2000000000015";
    private const string PassportCode = "SP-001-BIRTH";
    private const string DelegationId = "DEL-9af3";

    private static readonly string[] CallerRoles = ["cnas-operator"];

    // ─────────────────────── Tests ───────────────────────

    [Fact]
    public async Task SubmitAsync_NoOnBehalfOf_NoDelegationClaim_Succeeds()
    {
        var harness = Harness.Create();
        var seed = await harness.SeedAsync(seedPrincipal: false);

        var input = new SubmitApplicationInput(
            ServicePassportId: "PASSPORT-SQID",
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>());

        var result = await harness.Service.SubmitAsync(input);

        result.IsSuccess.Should().BeTrue();

        var apps = await harness.Db.Applications.ToListAsync();
        apps.Should().ContainSingle();
        apps[0].SolicitantId.Should().Be(seed.OperatorId);
    }

    [Fact]
    public async Task SubmitAsync_OnBehalfOfSet_MatchingClaim_Succeeds_LogsDelegationId()
    {
        var harness = Harness.Create();
        var seed = await harness.SeedAsync(seedPrincipal: true);
        harness.Caller.OnBehalfOfPrincipalIdnp.Returns(PrincipalIdnp);
        harness.Caller.DelegationPowerId.Returns(DelegationId);

        var input = new SubmitApplicationInput(
            ServicePassportId: "PASSPORT-SQID",
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>(),
            OnBehalfOfPrincipalIdnp: PrincipalIdnp);

        var result = await harness.Service.SubmitAsync(input);

        result.IsSuccess.Should().BeTrue();

        var apps = await harness.Db.Applications.ToListAsync();
        apps.Should().ContainSingle();
        // Application is owned by the PRINCIPAL, not the operator (UC06 CF 06.02 R0551).
        apps[0].SolicitantId.Should().Be(seed.PrincipalId);
        apps[0].SolicitantId.Should().NotBe(seed.OperatorId);
        apps[0].Status.Should().Be(ApplicationStatus.Submitted);

        // Audit entry must capture the MPower delegation id so investigators can correlate
        // the local dossier with the citizen's power-of-attorney record on MPass / MPower.
        await harness.Audit.Received().RecordAsync(
            "APPLICATION.SUBMITTED",
            Arg.Any<AuditSeverity>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Is<string>(d => d.Contains(DelegationId, StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitAsync_OnBehalfOfSet_NoClaim_ReturnsMPowerNotAuthorized()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(seedPrincipal: true);
        // Caller did not authenticate via a delegation — claim is null.
        harness.Caller.OnBehalfOfPrincipalIdnp.Returns((string?)null);
        harness.Caller.DelegationPowerId.Returns((string?)null);

        var input = new SubmitApplicationInput(
            ServicePassportId: "PASSPORT-SQID",
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>(),
            OnBehalfOfPrincipalIdnp: PrincipalIdnp);

        var result = await harness.Service.SubmitAsync(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MPowerNotAuthorized);
        (await harness.Db.Applications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SubmitAsync_OnBehalfOfSet_DifferentClaim_ReturnsMPowerNotAuthorized()
    {
        var harness = Harness.Create();
        await harness.SeedAsync(seedPrincipal: true);
        // Caller's claim authorises representation of someone OTHER than the requested
        // principal — must not be allowed to submit for the requested principal.
        harness.Caller.OnBehalfOfPrincipalIdnp.Returns("9999999999999");
        harness.Caller.DelegationPowerId.Returns("DEL-other");

        var input = new SubmitApplicationInput(
            ServicePassportId: "PASSPORT-SQID",
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>(),
            OnBehalfOfPrincipalIdnp: PrincipalIdnp);

        var result = await harness.Service.SubmitAsync(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MPowerNotAuthorized);
        (await harness.Db.Applications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SubmitAsync_OnBehalfOfEmpty_NoDelegationClaim_Succeeds()
    {
        // An empty-string OnBehalfOfPrincipalIdnp must be treated the same as null — no
        // delegation in play, normal self-submit.
        var harness = Harness.Create();
        var seed = await harness.SeedAsync(seedPrincipal: false);
        harness.Caller.OnBehalfOfPrincipalIdnp.Returns((string?)null);

        var input = new SubmitApplicationInput(
            ServicePassportId: "PASSPORT-SQID",
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>(),
            OnBehalfOfPrincipalIdnp: string.Empty);

        var result = await harness.Service.SubmitAsync(input);

        result.IsSuccess.Should().BeTrue();
        var apps = await harness.Db.Applications.ToListAsync();
        apps.Should().ContainSingle();
        apps[0].SolicitantId.Should().Be(seed.OperatorId);
    }

    [Fact]
    public async Task SubmitAsync_OnBehalfOfSet_ClaimDiffersOnlyByCase_Succeeds()
    {
        // IDNP comparison must be case-insensitive — a claim that differs from the
        // requested principal IDNP only in letter casing should authorise the submission.
        // (Moldovan IDNPs are numeric, but the guard rule is defensive across the entire
        // string class and exercised here against an uppercased synthetic value.)
        var harness = Harness.Create();
        var seed = await harness.SeedAsync(seedPrincipal: true);
        harness.Caller.OnBehalfOfPrincipalIdnp.Returns(PrincipalIdnp.ToUpperInvariant());
        harness.Caller.DelegationPowerId.Returns(DelegationId);

        var input = new SubmitApplicationInput(
            ServicePassportId: "PASSPORT-SQID",
            FormPayloadJson: "{}",
            AttachmentDocumentIds: Array.Empty<string>(),
            OnBehalfOfPrincipalIdnp: PrincipalIdnp.ToLowerInvariant());

        var result = await harness.Service.SubmitAsync(input);

        result.IsSuccess.Should().BeTrue();
        var apps = await harness.Db.Applications.ToListAsync();
        apps.Should().ContainSingle();
        apps[0].SolicitantId.Should().Be(seed.PrincipalId);
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-mp-claim-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record SeedResult(long OperatorId, long PrincipalId, long PassportId);

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required ApplicationServiceImpl Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required INotificationService Notify { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }
        public required ICnasTimeProvider Clock { get; init; }

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
            // UserId is populated by the harness after seeding the operator Solicitant.
            caller.Roles.Returns(CallerRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");
            caller.UserSqid.Returns("SQID-OP");

            // MCabinet publisher substitute — best-effort, returns success.
            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            // R0570 — wire an always-success examiner-assignment stub so the
            // MPower-claim tests focus on the delegation logic rather than
            // the orthogonal round-robin selection.
            var examinerAssignment = Substitute.For<Cnas.Ps.Application.UseCases.IExaminerAssignmentService>();
            examinerAssignment
                .AssignExaminerAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<long>.Success(999L)));
            var service = new ApplicationServiceImpl(
                db, sqids, clock, caller, audit, notify, mcabinet,
                NullLogger<ApplicationServiceImpl>.Instance, IdHashHelper.Instance, examinerAssignment);
            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
                Notify = notify,
                Caller = caller,
                Sqids = sqids,
                Clock = clock,
            };
        }

        /// <summary>Seeds an active passport, an operator Solicitant (the caller), and optionally the principal Solicitant.</summary>
        public async Task<SeedResult> SeedAsync(bool seedPrincipal)
        {
            var op = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = OperatorIdnp,
                NationalIdHash = IdHashHelper.Hash(OperatorIdnp),
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Operator User",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(op);

            long principalId = 0;
            if (seedPrincipal)
            {
                var principal = new Solicitant
                {
                    CreatedAtUtc = ClockNow,
                    NationalId = PrincipalIdnp,
                    NationalIdHash = IdHashHelper.Hash(PrincipalIdnp),
                    Kind = ApplicantKind.NaturalPerson,
                    DisplayName = "Principal Citizen",
                    PreferredLanguage = "ro",
                    IsActive = true,
                };
                Db.Solicitants.Add(principal);
                await Db.SaveChangesAsync();
                principalId = principal.Id;
            }

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = PassportCode,
                NameRo = "Test passport",
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

            // Wire caller -> operator solicitant.
            Caller.UserId.Returns(op.Id);

            // Bind sqid mappings the tests use.
            Sqids.TryDecode("PASSPORT-SQID").Returns(Result<long>.Success(passport.Id));

            return new SeedResult(op.Id, principalId, passport.Id);
        }
    }
}
