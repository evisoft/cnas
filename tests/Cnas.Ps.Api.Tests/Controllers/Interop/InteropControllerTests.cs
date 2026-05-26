using Cnas.Ps.Api.Controllers.Interop;
using Cnas.Ps.Application.Interop;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts.Interop;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers.Interop;

/// <summary>
/// R0634 / TOR CF 14.12 / Annex 4 — controller-level unit tests for
/// <see cref="InteropController"/>. Direct-construction style — the
/// underlying <see cref="IInteropApi"/> is faked with NSubstitute; the
/// validators are real instances (simple FluentValidation classes).
/// </summary>
public sealed class InteropControllerTests
{
    /// <summary>Canonical valid IDNP (mod-10 checksum valid).</summary>
    private const string ValidIdnp = "2000123456782";

    /// <summary>Builds a fresh service substitute.</summary>
    private static IInteropApi NewApiMock() => Substitute.For<IInteropApi>();

    /// <summary>Builds the SUT with real validators around the supplied service.</summary>
    private static InteropController NewController(IInteropApi api)
        => new(
            api,
            new InteropIdnpRequestDtoValidator(),
            new InteropContributionHistoryRequestValidator(),
            new ActiveDecisionsRequestDtoValidator(),
            new PaymentStatusRequestDtoValidator(),
            new PayerDataRequestDtoValidator(),
            new IsBenefitBeneficiaryRequestDtoValidator(),
            new ContributionPaymentInfoRequestDtoValidator(),
            new LegalApplicableFormRequestDtoValidator());

    /// <summary>Test 14 — controller POST insured-person-status returns 200 with the DTO on success.</summary>
    [Fact]
    public async Task R0634_GetInsuredPersonStatus_ServiceSuccess_Returns200_WithDto()
    {
        var api = NewApiMock();
        var dto = new InsuredPersonStatusDto(
            IdnpHashPrefix: "0123abcd",
            IsRegistered: true,
            AccountCode: "PA-1001",
            ActiveBenefitsCount: 1,
            AsOfUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc));
        api.GetInsuredPersonStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<InsuredPersonStatusDto>.Success(dto));
        var controller = NewController(api);

        var body = new InteropIdnpRequestDto(ValidIdnp);
        var result = await controller.GetInsuredPersonStatusAsync(body, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>
    /// Test 15 — controller is gated by <c>[Authorize(Roles = "InteropClient")]</c>.
    /// Asserted via reflection of the controller-level attribute (the
    /// runtime 403 mapping is enforced by ASP.NET Authorization
    /// middleware; the unit-test surface only verifies the contract).
    /// </summary>
    [Fact]
    public void R0634_Controller_HasAuthorizeInteropClientRoleAttribute()
    {
        var attrs = typeof(InteropController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty("the controller MUST be gated by an explicit Authorize policy");
        attrs.Should().Contain(a => a.Roles != null && a.Roles.Contains("InteropClient", StringComparison.Ordinal));
    }

    /// <summary>Invalid IDNP shape surfaces as 400 ProblemDetails.</summary>
    [Fact]
    public async Task R0634_GetInsuredPersonStatus_InvalidIdnpShape_Returns400()
    {
        var api = NewApiMock();
        var controller = NewController(api);

        var body = new InteropIdnpRequestDto("123"); // 3 chars, fails length+digit checks
        var result = await controller.GetInsuredPersonStatusAsync(body, CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["errorCode"].Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>Service-level NOT_FOUND on contribution-history surfaces as 404.</summary>
    [Fact]
    public async Task R0634_GetContributionHistory_ServiceNotFound_Returns404()
    {
        var api = NewApiMock();
        api.GetContributionHistoryAsync(
                Arg.Any<string>(),
                Arg.Any<DateOnly>(),
                Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
           .Returns(Result<ContributionHistoryDto>.Failure(ErrorCodes.NotFound, "unknown"));
        var controller = NewController(api);

        var body = new InteropContributionHistoryRequestDto(
            ValidIdnp,
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 12, 1));
        var result = await controller.GetContributionHistoryAsync(body, CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>Happy-path GetBenefitsList returns 200 with the DTO.</summary>
    [Fact]
    public async Task R0634_GetBenefitsList_ServiceSuccess_Returns200_WithDto()
    {
        var api = NewApiMock();
        var dto = new BenefitsListDto(
            IdnpHashPrefix: "0123abcd",
            Benefits: new[]
            {
                new BenefitEntryDto("OldAgePension", new DateOnly(2024, 1, 1), new DateOnly(2026, 5, 1), 30),
            });
        api.GetBenefitsListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<BenefitsListDto>.Success(dto));
        var controller = NewController(api);

        var result = await controller.GetBenefitsListAsync(
            new InteropIdnpRequestDto(ValidIdnp),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>Happy-path GetPersonalAccountSnapshot returns 200 with the DTO.</summary>
    [Fact]
    public async Task R0634_GetPersonalAccountSnapshot_ServiceSuccess_Returns200_WithDto()
    {
        var api = NewApiMock();
        var dto = new PersonalAccountSnapshotDto(
            IdnpHashPrefix: "0123abcd",
            AccountCode: "PA-5001",
            LifetimeContributions: 9999.99m,
            LifetimeMonths: 240,
            AsOfUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc));
        api.GetPersonalAccountSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<PersonalAccountSnapshotDto>.Success(dto));
        var controller = NewController(api);

        var result = await controller.GetPersonalAccountSnapshotAsync(
            new InteropIdnpRequestDto(ValidIdnp),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>Valid IDNO used by R1704/R1706 happy paths (13-digit numeric, allowed by validator).</summary>
    private const string ValidIdno = "1003600012345";

    /// <summary>R1702 happy path — controller returns 200 with the DTO on service success.</summary>
    [Fact]
    public async Task R1702_GetActiveDecisions_ServiceSuccess_Returns200_WithDto()
    {
        var api = NewApiMock();
        var dto = new ActiveDecisionsDto(
            IdnpHashPrefix: "0123abcd",
            Decisions: Array.Empty<ActiveDecisionEntryDto>(),
            AsOfUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc));
        api.GetActiveDecisionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<ActiveDecisionsDto>.Success(dto));
        var controller = NewController(api);

        var result = await controller.GetActiveDecisionsAsync(
            new ActiveDecisionsRequestDto(ValidIdnp),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>R1702 — service NOT_FOUND surfaces as 404.</summary>
    [Fact]
    public async Task R1702_GetActiveDecisions_ServiceNotFound_Returns404()
    {
        var api = NewApiMock();
        api.GetActiveDecisionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<ActiveDecisionsDto>.Failure(ErrorCodes.NotFound, "unknown"));
        var controller = NewController(api);

        var result = await controller.GetActiveDecisionsAsync(
            new ActiveDecisionsRequestDto(ValidIdnp),
            CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>R1703 — service NOT_FOUND surfaces as 404 on payment-status.</summary>
    [Fact]
    public async Task R1703_GetPaymentStatus_ServiceNotFound_Returns404()
    {
        var api = NewApiMock();
        api.GetPaymentStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
           .Returns(Result<PaymentStatusDto>.Failure(ErrorCodes.NotFound, "unknown"));
        var controller = NewController(api);

        var result = await controller.GetPaymentStatusAsync(
            new PaymentStatusRequestDto("SQID-123", new DateOnly(2026, 1, 1)),
            CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>R1703 — empty DecisionSqid is rejected as 400 validation failure.</summary>
    [Fact]
    public async Task R1703_GetPaymentStatus_EmptySqid_Returns400()
    {
        var api = NewApiMock();
        var controller = NewController(api);

        var result = await controller.GetPaymentStatusAsync(
            new PaymentStatusRequestDto(string.Empty, new DateOnly(2026, 1, 1)),
            CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    /// <summary>R1704 happy path — payer-data success returns 200.</summary>
    [Fact]
    public async Task R1704_GetPayerData_ServiceSuccess_Returns200_WithDto()
    {
        var api = NewApiMock();
        var dto = new PayerDataDto(
            TaxpayerHashPrefix: "0123abcd",
            PayerKind: "NaturalPerson",
            DisplayName: "Popescu Ion",
            RegistrationDate: new DateOnly(2024, 1, 1),
            Status: "Active",
            CountOfInsuredEmployees: 0,
            LastDeclarationMonth: null);
        api.GetPayerDataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<PayerDataDto>.Success(dto));
        var controller = NewController(api);

        var result = await controller.GetPayerDataAsync(
            new PayerDataRequestDto(ValidIdnp),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>R1705 happy path — is-beneficiary success returns 200 with the DTO.</summary>
    [Fact]
    public async Task R1705_IsBenefitBeneficiary_ServiceSuccess_Returns200_WithDto()
    {
        var api = NewApiMock();
        var dto = new IsBenefitBeneficiaryDto(
            IdnpHashPrefix: "0123abcd",
            BenefitType: "OldAgePension",
            IsBeneficiary: true,
            Reason: string.Empty,
            EvaluationDate: new DateOnly(2026, 5, 22),
            DecisionSqid: null);
        api.IsBenefitBeneficiaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<IsBenefitBeneficiaryDto>.Success(dto));
        var controller = NewController(api);

        var result = await controller.IsBenefitBeneficiaryAsync(
            new IsBenefitBeneficiaryRequestDto(ValidIdnp, "OldAgePension"),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>R1706 — NOT_FOUND on contribution-payment-info surfaces as 404.</summary>
    [Fact]
    public async Task R1706_GetContributionPaymentInfo_ServiceNotFound_Returns404()
    {
        var api = NewApiMock();
        api.GetContributionPaymentInfoAsync(
                Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
           .Returns(Result<ContributionPaymentInfoDto>.Failure(ErrorCodes.NotFound, "unknown"));
        var controller = NewController(api);

        var result = await controller.GetContributionPaymentInfoAsync(
            new ContributionPaymentInfoRequestDto(ValidIdno, new DateOnly(2026, 1, 1)),
            CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>R1707 — legal-applicable-form happy path returns 200 (even on NotApplicable).</summary>
    [Fact]
    public async Task R1707_GetLegalApplicableForm_ServiceSuccess_Returns200_WithDto()
    {
        var api = NewApiMock();
        var dto = new LegalApplicableFormDto(
            IdnpHashPrefix: "0123abcd",
            AgreementCode: "RO_MD_2006",
            ApplicableForm: "NotApplicable",
            FormSerialNumber: null,
            IssueDate: null,
            ValidUntil: null,
            HostCountryCode: "RO");
        api.GetLegalApplicableFormAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<LegalApplicableFormDto>.Success(dto));
        var controller = NewController(api);

        var result = await controller.GetLegalApplicableFormAsync(
            new LegalApplicableFormRequestDto(ValidIdnp, "RO_MD_2006"),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>R1708 happy path — work-insurance-period returns 200 with the DTO.</summary>
    [Fact]
    public async Task R1708_GetWorkInsurancePeriod_ServiceSuccess_Returns200_WithDto()
    {
        var api = NewApiMock();
        var dto = new WorkInsurancePeriodDto(
            IdnpHashPrefix: "0123abcd",
            TotalMonths: 240,
            FirstInsuredMonth: new DateOnly(2005, 1, 1),
            LastInsuredMonth: new DateOnly(2024, 12, 1),
            CurrentlyInsured: false,
            PeriodCount: 1);
        api.GetWorkInsurancePeriodAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<WorkInsurancePeriodDto>.Success(dto));
        var controller = NewController(api);

        var result = await controller.GetWorkInsurancePeriodAsync(
            new InteropIdnpRequestDto(ValidIdnp),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>R1708 — NOT_FOUND on work-insurance-period surfaces as 404.</summary>
    [Fact]
    public async Task R1708_GetWorkInsurancePeriod_ServiceNotFound_Returns404()
    {
        var api = NewApiMock();
        api.GetWorkInsurancePeriodAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<WorkInsurancePeriodDto>.Failure(ErrorCodes.NotFound, "unknown"));
        var controller = NewController(api);

        var result = await controller.GetWorkInsurancePeriodAsync(
            new InteropIdnpRequestDto(ValidIdnp),
            CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }
}
