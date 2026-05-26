using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0933 / TOR §10.1 — unit tests for <see cref="DecisionSupersessionController"/>.
/// Mirrors the direct-construction pattern of the rest of the controller suite;
/// authorization is asserted indirectly through the
/// <c>[Authorize(Policy=...)]</c> attribute on the controller type — the policy
/// registration itself is covered by <c>RolePoliciesTests</c>. These tests pin
/// the controller's branch logic + the <see cref="ErrorCodes"/> → HTTP status
/// mapping (200, 204, 404, 400).
/// </summary>
public sealed class DecisionSupersessionControllerTests
{
    private static (DecisionSupersessionController Controller,
        IPriorDecisionTerminator Service,
        ISqidService Sqids) NewSut()
    {
        var svc = Substitute.For<IPriorDecisionTerminator>();
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode("good-sqid").Returns(Result<long>.Success(42L));
        sqids.TryDecode("bad-sqid").Returns(
            Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));
        return (new DecisionSupersessionController(svc, sqids), svc, sqids);
    }

    [Fact]
    public async Task CompareWithPrior_HappyPath_Returns200WithComparisonBody()
    {
        var (controller, svc, _) = NewSut();
        var dto = new DecisionComparisonDto(
            HasPrior: true,
            PreviousDecisionSqid: "SQID-7",
            PriorAmount: 1000m,
            NewAmount: 1500m,
            Difference: 500m,
            LowerSumWarning: false);
        svc.CompareAsync(42L, Arg.Any<CancellationToken>())
           .Returns(Result<DecisionComparisonDto>.Success(dto));

        var result = await controller.CompareWithPriorAsync("good-sqid", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task CompareWithPrior_InvalidSqid_Returns400()
    {
        var (controller, _, _) = NewSut();

        var result = await controller.CompareWithPriorAsync("bad-sqid", CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SupersedePrior_HappyPath_Returns200WithSupersessionDto()
    {
        var (controller, svc, _) = NewSut();
        var dto = new DecisionSupersessionDto(
            Id: "SQID-100",
            PreviousDecisionSqid: "SQID-7",
            NewDecisionSqid: "SQID-42",
            SupersededAtUtc: new DateTime(2026, 5, 24, 13, 0, 0, DateTimeKind.Utc),
            SupersededByUserSqid: "SQID-DECIDER",
            Reason: "Prior decision terminated on new acceptance (new=1500, prior=1000).",
            PriorAmount: 1000m,
            NewAmount: 1500m);
        svc.TerminateOnAcceptanceAsync(42L, Arg.Any<CancellationToken>())
           .Returns(Result<DecisionSupersessionDto?>.Success(dto));

        var result = await controller.SupersedePriorAsync("good-sqid", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task SupersedePrior_NoPriorExists_Returns204NoContent()
    {
        var (controller, svc, _) = NewSut();
        svc.TerminateOnAcceptanceAsync(42L, Arg.Any<CancellationToken>())
           .Returns(Result<DecisionSupersessionDto?>.Success(null));

        var result = await controller.SupersedePriorAsync("good-sqid", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SupersedePrior_NewDecisionNotFound_Returns404()
    {
        var (controller, svc, _) = NewSut();
        svc.TerminateOnAcceptanceAsync(42L, Arg.Any<CancellationToken>())
           .Returns(Result<DecisionSupersessionDto?>.Failure(
                ErrorCodes.NotFound, "missing"));

        var result = await controller.SupersedePriorAsync("good-sqid", CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public void Controller_RequiresCnasDeciderPolicy()
    {
        // Pin the [Authorize(Policy = ...)] attribute the controller declares so a
        // future refactor cannot silently widen the policy gate. The terminator
        // service itself does not enforce role checks; this policy IS the gate.
        var attr = typeof(DecisionSupersessionController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        attr.Policy.Should().Be(AuthorizationComposition.CnasDecider,
            "the terminate-prior-on-acceptance surface must be gated by the cnas-decider policy");
    }
}
