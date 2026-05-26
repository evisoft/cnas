using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.AthletePensions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R1403 / TOR §3.6-D — controller-level tests for the athlete-pension REST
/// surface. Verifies the cnas-admin authorize gate plus the create / get /
/// conflict mapping happy paths.
/// </summary>
public sealed class AthletePensionAwardsControllerTests
{
    private const string Sqid = "APE-1";

    private static AthletePensionAwardDto MakeDto(string status = "Draft") => new(
        Id: Sqid,
        AwardNumber: "APE-2026-000001",
        BeneficiaryIdnpHash: "HASH==",
        BeneficiaryDisplayName: "Ion Popescu",
        BeneficiaryBirthDate: new DateOnly(1980, 4, 1),
        BeneficiarySex: "Male",
        Role: "Athlete",
        SportDiscipline: "ATHLETICS",
        Status: status,
        RequestedAt: new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc),
        ApprovedAt: null,
        RejectedAt: null,
        RejectionReason: null,
        EffectiveFrom: null,
        SuspendedAt: null,
        SuspensionReason: null,
        TerminatedAt: null,
        TerminationReason: null,
        MonthlyAmountMdl: 0m,
        RegulatoryBaseMdl: 0m,
        MultiplierPercent: 0m,
        EligibilityNotesJson: null,
        RegisteredAt: new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc),
        LastRecomputedAt: null,
        CareerRecords: Array.Empty<AthleteCareerRecordDto>());

    private static AthletePensionAwardCreateInputDto MakeCreateInput() => new(
        BeneficiaryIdnp: "2002000000007",
        BeneficiaryDisplayName: "Ion Popescu",
        BeneficiaryBirthDate: new DateOnly(1980, 4, 1),
        BeneficiarySex: "Male",
        Role: "Athlete",
        SportDiscipline: "ATHLETICS");

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(AthletePensionAwardsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();
        attrs.Should().NotBeEmpty();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task CreateAsync_HappyPath_Returns201()
    {
        var dto = MakeDto();
        var svc = Substitute.For<IAthletePensionAwardService>();
        svc.CreateAsync(
                Arg.Any<AthletePensionAwardCreateInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AthletePensionAwardDto>.Success(dto)));

        var controller = new AthletePensionAwardsController(svc);
        var result = await controller.CreateAsync(MakeCreateInput(), CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedResult>().Subject;
        created.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task GetAsync_NotFound_Returns404()
    {
        var svc = Substitute.For<IAthletePensionAwardService>();
        svc.GetByIdAsync(Sqid, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.NotFound, "missing")));

        var controller = new AthletePensionAwardsController(svc);
        var result = await controller.GetAsync(Sqid, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task SubmitAsync_Conflict_Returns409()
    {
        var svc = Substitute.For<IAthletePensionAwardService>();
        svc.SubmitAsync(Sqid, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AthletePensionAwardDto>.Failure(
                ErrorCodes.Conflict, "ATHLETE_PENSION.INVALID_TRANSITION")));

        var controller = new AthletePensionAwardsController(svc);
        var result = await controller.SubmitAsync(Sqid, CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }
}
