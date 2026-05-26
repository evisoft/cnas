using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.AthletePensions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.AthletePensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.AthletePensions;

/// <summary>
/// R1403 / TOR §3.6-D — service-level tests for the athlete-pension award
/// registry. Exercises the create / submit / approve / activate / terminate
/// state machine, audit + metric emission, and the auto-generated award
/// number format.
/// </summary>
public sealed class AthletePensionAwardServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-athlete-pen-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Sqid stub that round-trips "APE-{id}" strings.</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"APE-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("APE-", StringComparison.Ordinal)
                && long.TryParse(s["APE-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>Captures audit invocations for assertion.</summary>
    private static (IAuditService Audit, Func<List<(string Code, AuditSeverity Severity, long? TargetId)>> Calls)
        NewAuditCapture()
    {
        var calls = new List<(string Code, AuditSeverity Severity, long? TargetId)>();
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add((
                    call.ArgAt<string>(0),
                    call.ArgAt<AuditSeverity>(1),
                    call.ArgAt<long?>(4)));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => calls);
    }

    /// <summary>Authenticated caller stub.</summary>
    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(7L);
        caller.UserSqid.Returns("USR-7");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-athlete");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-admin"]);
        return caller;
    }

    /// <summary>Builds the SUT.</summary>
    private static AthletePensionAwardService NewService(CnasDbContext db, IAuditService audit)
    {
        var clock = new StubClock(ClockNow);
        var evaluator = new AthletePensionEligibilityEvaluator();
        var calculator = new AthletePensionAmountCalculator();
        return new(
            db,
            clock,
            NewSqidMock(),
            NewCaller(),
            audit,
            IdHashHelper.Instance,
            evaluator,
            calculator,
            new AthletePensionAwardCreateInputValidator(clock),
            new AthleteCareerRecordInputValidator(clock),
            new AthleteCareerRecordVerificationInputValidator(),
            new AthletePensionApprovalInputValidator(),
            new AthletePensionActivationInputValidator(clock),
            new AthletePensionReasonInputValidator(),
            new AthletePensionAwardFilterValidator());
    }

    /// <summary>Builds a canonical create-input DTO.</summary>
    private static AthletePensionAwardCreateInputDto BuildCreateInput(
        string idnp = "2002000000007",
        string role = "Athlete") => new(
            BeneficiaryIdnp: idnp,
            BeneficiaryDisplayName: "Ion Popescu",
            BeneficiaryBirthDate: new DateOnly(1980, 4, 1),
            BeneficiarySex: nameof(BeneficiarySex.Male),
            Role: role,
            SportDiscipline: "ATHLETICS");

    [Fact]
    public async Task CreateAsync_HappyPath_PersistsDraftAndAuditsAndGeneratesNumber()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreateAsync(BuildCreateInput());

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(AthletePensionAwardStatus.Draft));
        result.Value.AwardNumber.Should().Be("APE-2026-000001");
        result.Value.BeneficiaryIdnpHash.Should().NotBeNullOrWhiteSpace();
        (await db.AthletePensionAwards.CountAsync()).Should().Be(1);
        calls().Should().ContainSingle(c =>
            c.Code == AthletePensionAwardService.AuditCreated
            && c.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public async Task SubmitAsync_FromNonDraft_ReturnsConflict()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);

        var second = await sut.SubmitAsync(created.Value.Id);

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task ApproveAsync_WithoutEligibility_ReturnsConflictNotEligible()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        // Athlete role, but no verified medal records → ineligible.
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);

        var approve = await sut.ApproveAsync(
            created.Value.Id,
            new AthletePensionApprovalInputDto(
                Note: "Approval attempt",
                RegulatoryBaseMdl: 3_000m,
                AdditionalMultipliers: null));

        approve.IsFailure.Should().BeTrue();
        approve.ErrorCode.Should().Be(ErrorCodes.Conflict);
        approve.ErrorMessage.Should().Contain("NOT_ELIGIBLE");
    }

    [Fact]
    public async Task ActivateThenTerminate_Succeeds_WithReason()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput());
        var awardSqid = created.Value.Id;

        // Add a verified Olympic gold record so the athlete is eligible.
        var add = await sut.AddCareerRecordAsync(
            awardSqid,
            new AthleteCareerRecordInputDto(
                AchievementKind: nameof(AthleteAchievementKind.OlympicGold),
                AchievementYear: 2008,
                Event: "Beijing 2008 — 100m",
                Years: null,
                EvidenceDocumentReference: null));
        add.IsSuccess.Should().BeTrue();

        var recordSqid = add.Value.CareerRecords.Single().Id;
        var verify = await sut.VerifyCareerRecordAsync(
            awardSqid,
            recordSqid,
            new AthleteCareerRecordVerificationInputDto(VerificationNote: "Confirmed by archive"));
        verify.IsSuccess.Should().BeTrue();

        await sut.SubmitAsync(awardSqid);
        var approve = await sut.ApproveAsync(
            awardSqid,
            new AthletePensionApprovalInputDto(
                Note: "Approved per criteria",
                RegulatoryBaseMdl: 3_000m,
                AdditionalMultipliers: null));
        approve.IsSuccess.Should().BeTrue();
        approve.Value.MonthlyAmountMdl.Should().BeGreaterThan(0m);

        var activate = await sut.ActivateAsync(
            awardSqid,
            new AthletePensionActivationInputDto(
                EffectiveFrom: new DateOnly(2026, 5, 23),
                Note: "Activation confirmed"));
        activate.IsSuccess.Should().BeTrue();
        activate.Value.Status.Should().Be(nameof(AthletePensionAwardStatus.Active));

        var terminate = await sut.TerminateAsync(
            awardSqid,
            new AthletePensionReasonInputDto(Reason: "Beneficiary deceased"));
        terminate.IsSuccess.Should().BeTrue();
        terminate.Value.Status.Should().Be(nameof(AthletePensionAwardStatus.Terminated));
        terminate.Value.TerminationReason.Should().Be("Beneficiary deceased");
        calls().Should().Contain(c => c.Code == AthletePensionAwardService.AuditTerminated);
    }
}
