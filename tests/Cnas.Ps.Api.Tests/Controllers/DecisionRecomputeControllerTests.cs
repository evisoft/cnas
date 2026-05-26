using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R1502 / TOR §3.7-C — unit tests for <see cref="DecisionRecomputeController"/>.
/// Pins the success / NotFound branches and the DTO contract.
/// </summary>
public sealed class DecisionRecomputeControllerTests
{
    private static (DecisionRecomputeController Controller, IDecisionRecomputeService Service) NewSut()
    {
        var svc = Substitute.For<IDecisionRecomputeService>();
        return (new DecisionRecomputeController(svc), svc);
    }

    /// <summary>Successful recompute returns 200 with the outcome DTO.</summary>
    [Fact]
    public async Task RecomputeAsync_HappyPath_Returns200WithOutcome()
    {
        var (controller, svc) = NewSut();
        var dto = new DecisionRecomputeOutcomeDto(
            PriorAmount: 1000m,
            NewAmount: 1200m,
            Delta: 200m,
            NewDocumentSqid: "SQID-7",
            DocumentKindCode: "DECIZIE_AJUSTARE_SUME");
        svc.RecomputeAsync("good-sqid",
                DecisionRecomputeReason.BaseAmountChanged,
                1200m,
                Arg.Any<CancellationToken>())
           .Returns(Result<DecisionRecomputeOutcomeDto>.Success(dto));

        var result = await controller.RecomputeAsync(
            "good-sqid",
            new DecisionRecomputeInputDto(DecisionRecomputeReason.BaseAmountChanged, 1200m),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>Unknown prior decision returns 404.</summary>
    [Fact]
    public async Task RecomputeAsync_NotFound_Returns404()
    {
        var (controller, svc) = NewSut();
        svc.RecomputeAsync(Arg.Any<string>(),
                Arg.Any<DecisionRecomputeReason>(),
                Arg.Any<decimal>(),
                Arg.Any<CancellationToken>())
           .Returns(Result<DecisionRecomputeOutcomeDto>.Failure(
                ErrorCodes.NotFound, "Prior decision id=999 not found."));

        var result = await controller.RecomputeAsync(
            "missing",
            new DecisionRecomputeInputDto(DecisionRecomputeReason.Other, 500m),
            CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
