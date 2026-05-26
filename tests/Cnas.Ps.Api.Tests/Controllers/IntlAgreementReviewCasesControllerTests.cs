using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.IntlAgreements;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — controller-level tests for the
/// international-agreements REST surface. Verifies the cnas-admin
/// authorize gate plus the happy paths for create / review / list-by-kind.
/// </summary>
public sealed class IntlAgreementReviewCasesControllerTests
{
    private const string Sqid = "IAR-1";

    private static IntlAgreementReviewCaseDto MakeDto(
        string status = "Draft",
        string benefitKind = "IncapacityMaternity") => new(
        Id: Sqid,
        CaseNumber: "IAR-2026-000001",
        BenefitKind: benefitKind,
        BeneficiaryIdnpHash: "HASH==",
        BeneficiaryDisplayName: "Ion Popescu",
        AgreementCode: "RO_MD_2006",
        HostCountryCode: "RO",
        Status: status,
        CurrentLevel: nameof(IntlAgreementReviewLevel.Local),
        ReferenceBenefitPassportSqid: null,
        SubmittedAt: null,
        ApprovedAt: null,
        RejectedAt: null,
        RejectionReason: null,
        RevisionRequestedAt: null,
        RevisionRequestNote: null,
        CancelledAt: null,
        CancelReason: null,
        EvidenceJson: null,
        RegisteredAt: new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc),
        Steps: Array.Empty<IntlAgreementReviewStepDto>());

    private static IntlAgreementReviewCaseCreateInputDto MakeCreateInput() => new(
        BenefitKind: "IncapacityMaternity",
        BeneficiaryIdnp: "2002000000007",
        BeneficiaryDisplayName: "Ion Popescu",
        AgreementCode: "RO_MD_2006",
        HostCountryCode: "RO");

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(IntlAgreementReviewCasesController)
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
        var svc = Substitute.For<IIntlAgreementRoutingService>();
        svc.CreateAsync(
                Arg.Any<IntlAgreementReviewCaseCreateInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IntlAgreementReviewCaseDto>.Success(dto)));

        var controller = new IntlAgreementReviewCasesController(svc);
        var result = await controller.CreateAsync(MakeCreateInput(), CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedResult>().Subject;
        created.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task ReviewAsync_HappyPath_Returns200()
    {
        var dto = MakeDto(status: "AtRegionalReview");
        var svc = Substitute.For<IIntlAgreementRoutingService>();
        svc.RecordReviewAsync(
                Sqid,
                Arg.Any<IntlAgreementReviewInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IntlAgreementReviewCaseDto>.Success(dto)));

        var controller = new IntlAgreementReviewCasesController(svc);
        var result = await controller.RecordReviewAsync(
            Sqid,
            new IntlAgreementReviewInputDto(
                Outcome: nameof(IntlAgreementReviewStepOutcome.Approved),
                Note: "Approved at local level."),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task ListAsync_RespectsBenefitKindFilter()
    {
        var page = new IntlAgreementReviewCasePageDto(
            Items: new[] { MakeDto(benefitKind: "Unemployment") },
            Total: 1,
            Skip: 0,
            Take: 25);
        var svc = Substitute.For<IIntlAgreementRoutingService>();
        svc.ListAsync(
                Arg.Is<IntlAgreementReviewCaseFilterDto>(f => f.BenefitKind == "Unemployment"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IntlAgreementReviewCasePageDto>.Success(page)));

        var controller = new IntlAgreementReviewCasesController(svc);
        var result = await controller.ListAsync(
            status: null,
            benefitKind: "Unemployment",
            agreementCode: null,
            hostCountryCode: null,
            currentLevel: null,
            skip: 0,
            take: 25,
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task ReviewAsync_Forbidden_Returns403()
    {
        var svc = Substitute.For<IIntlAgreementRoutingService>();
        svc.RecordReviewAsync(
                Sqid,
                Arg.Any<IntlAgreementReviewInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IntlAgreementReviewCaseDto>.Failure(
                ErrorCodes.Forbidden, "INTL_AGREEMENT.WRONG_REVIEWER_ROLE")));

        var controller = new IntlAgreementReviewCasesController(svc);
        var result = await controller.RecordReviewAsync(
            Sqid,
            new IntlAgreementReviewInputDto(
                Outcome: nameof(IntlAgreementReviewStepOutcome.Approved),
                Note: "Attempt without role."),
            CancellationToken.None);

        var status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(403);
    }
}
