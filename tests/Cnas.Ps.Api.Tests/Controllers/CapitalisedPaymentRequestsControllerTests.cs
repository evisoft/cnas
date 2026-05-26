using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.CapitalisedPayments;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R1202 / TOR §3.4-C — controller-level tests for the capitalised-payment
/// REST surface. Verifies the cnas-admin authorize gate plus the
/// create / get / compute happy paths.
/// </summary>
public sealed class CapitalisedPaymentRequestsControllerTests
{
    private const string Sqid = "CPR-1";

    private static CapitalisedPaymentRequestDto MakeDto(string status = "Draft") => new(
        Id: Sqid,
        RequestNumber: "CPR-2026-000001",
        BeneficiaryIdnpHash: "HASH==",
        BeneficiaryBirthDate: new DateOnly(1960, 1, 1),
        BeneficiarySex: "Male",
        LiquidatedDebtorIdnoHash: "DHASH==",
        LiquidatedDebtorName: "SRL Demo",
        Status: status,
        ObligationKind: "IncapacityForWork",
        MonthlyAmountMdl: 1_000m,
        ObligationStartDate: new DateOnly(2018, 1, 1),
        ObligationEndDate: null,
        ValuationDate: new DateOnly(2026, 6, 1),
        LegalDiscountRatePercent: 9.5m,
        RegisteredAt: new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc),
        CancellationReason: null);

    private static CapitalisedPaymentDecisionDto MakeDecisionDto() => new(
        Id: "CPR-DEC-1",
        RequestSqid: Sqid,
        DecisionStatus: "Approved",
        ComputedAtUtc: DateTime.UtcNow,
        EffectiveAgeYears: 65m,
        LifeExpectancyMonths: 222,
        EffectiveDiscountMonthly: 0.00761594m,
        CapitalisedAmountMdl: 123_456m,
        ComputationBreakdownJson: "{}",
        ApprovedByUserSqid: "USR-7",
        RejectionReason: null);

    private static CapitalisedPaymentRequestCreateInputDto MakeCreateInput() => new(
        BeneficiaryIdnp: "2002000000007",
        BeneficiaryBirthDate: new DateOnly(1960, 1, 1),
        BeneficiarySex: "Male",
        LiquidatedDebtorIdno: "1003600000123",
        LiquidatedDebtorName: "SRL Demo",
        ObligationKind: "IncapacityForWork",
        MonthlyAmountMdl: 1_000m,
        ObligationStartDate: new DateOnly(2018, 1, 1),
        ObligationEndDate: null,
        ValuationDate: new DateOnly(2026, 6, 1),
        LegalDiscountRatePercent: 9.5m);

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(CapitalisedPaymentRequestsController)
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
        var svc = Substitute.For<ICapitalisedPaymentService>();
        svc.CreateAsync(
                Arg.Any<CapitalisedPaymentRequestCreateInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<CapitalisedPaymentRequestDto>.Success(dto)));

        var controller = new CapitalisedPaymentRequestsController(svc);
        var result = await controller.CreateAsync(MakeCreateInput(), CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedResult>().Subject;
        created.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task ComputeAsync_HappyPath_Returns200WithDecision()
    {
        var dto = MakeDecisionDto();
        var svc = Substitute.For<ICapitalisedPaymentService>();
        svc.ComputeAsync(Sqid, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<CapitalisedPaymentDecisionDto>.Success(dto)));

        var controller = new CapitalisedPaymentRequestsController(svc);
        var result = await controller.ComputeAsync(Sqid, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task GetAsync_NotFound_Returns404()
    {
        var svc = Substitute.For<ICapitalisedPaymentService>();
        svc.GetByIdAsync(Sqid, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<CapitalisedPaymentRequestDto>.Failure(
                ErrorCodes.NotFound, "missing")));

        var controller = new CapitalisedPaymentRequestsController(svc);
        var result = await controller.GetAsync(Sqid, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task SubmitAsync_Conflict_Returns409()
    {
        var svc = Substitute.For<ICapitalisedPaymentService>();
        svc.SubmitAsync(Sqid, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<CapitalisedPaymentRequestDto>.Failure(
                ErrorCodes.Conflict, CapitalisedPaymentRequestsControllerConflictHelper.Message)));

        var controller = new CapitalisedPaymentRequestsController(svc);
        var result = await controller.SubmitAsync(Sqid, CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }
}

/// <summary>Test helper holding the canonical invalid-transition message.</summary>
internal static class CapitalisedPaymentRequestsControllerConflictHelper
{
    /// <summary>Stable invalid-transition message used by the controller tests.</summary>
    public const string Message = "CAP_PAY.INVALID_TRANSITION";
}
