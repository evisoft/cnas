using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Benefits;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0517 — controller-level unit tests for
/// <see cref="BenefitPaymentsController"/>. Asserts the happy-path 200 shape
/// on the self-service route, the 403 ProblemDetails on the admin route
/// without permission, and the Sqid-decode failure path.
/// </summary>
public sealed class BenefitPaymentsControllerTests
{
    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static IBenefitPaymentStatusService NewServiceMock() =>
        Substitute.For<IBenefitPaymentStatusService>();

    /// <summary>Builds the SUT around the supplied service.</summary>
    private static BenefitPaymentsController NewController(
        IBenefitPaymentStatusService svc,
        ISqidService? sqids = null)
    {
        if (sqids is null)
        {
            sqids = Substitute.For<ISqidService>();
            sqids.TryDecode(Arg.Any<string?>())
                 .Returns(call =>
                 {
                     var s = call.Arg<string?>();
                     if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                         && long.TryParse(s.AsSpan(5), out var id))
                     {
                         return Result<long>.Success(id);
                     }
                     return Result<long>.Failure(ErrorCodes.InvalidSqid, "Invalid sqid.");
                 });
        }
        return new BenefitPaymentsController(svc, sqids);
    }

    /// <summary>Builds a populated status DTO for happy-path tests.</summary>
    private static BenefitPaymentStatusDto SampleStatus(string solicitantSqid)
        => new(
            SolicitantSqid: solicitantSqid,
            Payments: Array.Empty<BenefitPaymentDto>(),
            TotalPaidLast12Months: 0m,
            TotalScheduledNext3Months: 0m,
            GeneratedAtUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc));

    /// <summary>
    /// Controller test — self-service GET returns 200 with the populated DTO.
    /// </summary>
    [Fact]
    public async Task R0517_GetMine_Success_Returns200_WithStatusDto()
    {
        var svc = NewServiceMock();
        var dto = SampleStatus("SQID-1");
        svc.GetForCurrentUserAsync(Arg.Any<BenefitPaymentStatusQueryDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<BenefitPaymentStatusDto>.Success(dto));
        var controller = NewController(svc);

        var result = await controller.GetMineAsync(
            fromMonth: null,
            toMonth: null,
            type: null,
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>
    /// Controller test — admin GET surfaces Forbidden as 403 ProblemDetails.
    /// </summary>
    [Fact]
    public async Task R0517_GetForSolicitant_Forbidden_Returns403()
    {
        var svc = NewServiceMock();
        svc.GetForSolicitantAsync(
                Arg.Any<long>(),
                Arg.Any<BenefitPaymentStatusQueryDto>(),
                Arg.Any<CancellationToken>())
           .Returns(Result<BenefitPaymentStatusDto>.Failure(
               ErrorCodes.Forbidden, "Permission required."));
        var controller = NewController(svc);

        var result = await controller.GetForSolicitantAsync(
            "SQID-42",
            fromMonth: null,
            toMonth: null,
            type: null,
            cancellationToken: CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(403);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["errorCode"].Should().Be(ErrorCodes.Forbidden);
    }

    /// <summary>
    /// Controller test — admin GET returns 400 ProblemDetails when the Sqid
    /// fails to decode.
    /// </summary>
    [Fact]
    public async Task R0517_GetForSolicitant_BadSqid_Returns400()
    {
        var svc = NewServiceMock();
        var controller = NewController(svc);

        var result = await controller.GetForSolicitantAsync(
            "not-a-sqid",
            fromMonth: null,
            toMonth: null,
            type: null,
            cancellationToken: CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(400);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["errorCode"].Should().Be(ErrorCodes.InvalidSqid);
    }

    /// <summary>
    /// Controller test — admin GET success returns 200 with the populated DTO.
    /// </summary>
    [Fact]
    public async Task R0517_GetForSolicitant_Success_Returns200()
    {
        var svc = NewServiceMock();
        var dto = SampleStatus("SQID-42");
        svc.GetForSolicitantAsync(42L, Arg.Any<BenefitPaymentStatusQueryDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<BenefitPaymentStatusDto>.Success(dto));
        var controller = NewController(svc);

        var result = await controller.GetForSolicitantAsync(
            "SQID-42",
            fromMonth: null,
            toMonth: null,
            type: null,
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }
}
