using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R1504 / TOR §3.7-E — controller-level unit tests for
/// <see cref="PaymentSuspensionsController"/>.
/// </summary>
public sealed class PaymentSuspensionsControllerTests
{
    private static (PaymentSuspensionsController Controller, IPaymentSuspensionService Service) NewSut()
    {
        IValidator<PaymentSuspensionInputDto> validator = new PaymentSuspensionInputValidator();
        var svc = Substitute.For<IPaymentSuspensionService>();
        return (new PaymentSuspensionsController(svc, validator), svc);
    }

    /// <summary>SuspendAsync — happy path returns 200 with the DTO.</summary>
    [Fact]
    public async Task SuspendAsync_HappyPath_Returns200()
    {
        var (controller, svc) = NewSut();
        var dto = new PaymentSuspensionDto(
            Sqid: "SQID-5",
            DecisionSqid: "SQID-DEC",
            SuspensionReason: "Reason valid",
            SuspendedAtUtc: DateTime.UtcNow,
            ResumedAtUtc: null,
            ResumeReason: null,
            SuspensionDocumentSqid: "SQID-DOC",
            ResumeDocumentSqid: null);
        svc.SuspendAsync("SQID-DEC", "Reason valid", Arg.Any<CancellationToken>())
            .Returns(Result<PaymentSuspensionDto>.Success(dto));

        var result = await controller.SuspendAsync(
            "SQID-DEC",
            new PaymentSuspensionInputDto("Reason valid"),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>SuspendAsync — Conflict failure maps to 409.</summary>
    [Fact]
    public async Task SuspendAsync_Conflict_Returns409()
    {
        var (controller, svc) = NewSut();
        svc.SuspendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<PaymentSuspensionDto>.Failure(ErrorCodes.Conflict, "already suspended"));

        var result = await controller.SuspendAsync(
            "SQID-DEC",
            new PaymentSuspensionInputDto("Reason valid"),
            CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }
}
