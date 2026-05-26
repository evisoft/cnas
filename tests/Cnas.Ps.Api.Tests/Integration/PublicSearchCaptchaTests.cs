using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Captcha;
using Cnas.Ps.Application.PublicCatalog;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Integration;

/// <summary>
/// R0507 / TOR CF 01.10 — verifies that
/// <see cref="PublicCatalogController.ListAsync"/> enforces the CAPTCHA gate
/// for broad searches and bypasses the gate when a recently-verified token is
/// presented or when the query is narrow.
/// </summary>
public sealed class PublicSearchCaptchaTests
{
    private static PagedResult<PublicCatalogListItemDto> NewPage() =>
        new(Items: new List<PublicCatalogListItemDto>(), Page: 1, PageSize: 50, TotalCount: 0);

    private static PublicCatalogController NewController(
        IPublicCatalogService svc,
        ICaptchaPolicyEvaluator pol,
        ICaptchaChallengeService cap,
        Microsoft.AspNetCore.Http.HttpContext? httpContext = null)
    {
        return new PublicCatalogController(svc, pol, cap)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext ?? new DefaultHttpContext(),
            },
        };
    }

    [Fact]
    public async Task BroadSearch_NoCaptchaToken_Returns403()
    {
        var svc = Substitute.For<IPublicCatalogService>();
        svc.ListAsync(Arg.Any<PublicCatalogListQueryDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<PublicCatalogListItemDto>>.Success(NewPage()));
        var pol = Substitute.For<ICaptchaPolicyEvaluator>();
        pol.RequireCaptcha(Arg.Any<PublicCatalogListQueryDto?>()).Returns(true);
        var cap = Substitute.For<ICaptchaChallengeService>();
        cap.IsRecentlyVerifiedAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(false);

        var controller = NewController(svc, pol, cap);
        var result = await controller.ListAsync(
            q: null, category: null, sort: "Relevance",
            skip: 0, take: 50, language: "ro",
            cancellationToken: CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be("https://cnas/captcha/required");
    }

    [Fact]
    public async Task BroadSearch_WithVerifiedCaptchaToken_Returns200()
    {
        var svc = Substitute.For<IPublicCatalogService>();
        svc.ListAsync(Arg.Any<PublicCatalogListQueryDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<PublicCatalogListItemDto>>.Success(NewPage()));
        var pol = Substitute.For<ICaptchaPolicyEvaluator>();
        pol.RequireCaptcha(Arg.Any<PublicCatalogListQueryDto?>()).Returns(true);
        var cap = Substitute.For<ICaptchaChallengeService>();
        cap.IsRecentlyVerifiedAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);
        // R0507 — the gate now also calls ConsumeAsync after IsRecentlyVerified
        // succeeds, to flip the one-shot token to consumed so it cannot be
        // replayed inside the post-verify window. Stub success here so the
        // gate proceeds to the underlying service call.
        cap.ConsumeAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[PublicCatalogController.CaptchaTokenHeader] = "any-verified-token";

        var controller = NewController(svc, pol, cap, httpContext);
        var result = await controller.ListAsync(
            q: null, category: null, sort: "Relevance",
            skip: 0, take: 50, language: "ro",
            cancellationToken: CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    /// <summary>
    /// R0507 — pin the replay-block: once ConsumeAsync flags the token as
    /// already-consumed, the gate must reject the second request with the
    /// same 403 captcha-required problem so the UI re-mints a fresh
    /// challenge. The one-shot promise is what stops a single solved
    /// challenge from being used to spray multiple broad searches.
    /// </summary>
    [Fact]
    public async Task BroadSearch_VerifiedToken_RejectedOnReplayAfterConsume()
    {
        var svc = Substitute.For<IPublicCatalogService>();
        svc.ListAsync(Arg.Any<PublicCatalogListQueryDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<PublicCatalogListItemDto>>.Success(NewPage()));
        var pol = Substitute.For<ICaptchaPolicyEvaluator>();
        pol.RequireCaptcha(Arg.Any<PublicCatalogListQueryDto?>()).Returns(true);
        var cap = Substitute.For<ICaptchaChallengeService>();
        cap.IsRecentlyVerifiedAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);
        // First Consume succeeds, second returns ALREADY_CONSUMED.
        cap.ConsumeAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(Result.Success()),
                Task.FromResult(Result.Failure(
                    ErrorCodes.CaptchaAlreadyConsumed,
                    "Verified CAPTCHA token has already been consumed.")));

        var httpContext1 = new DefaultHttpContext();
        httpContext1.Request.Headers[PublicCatalogController.CaptchaTokenHeader] = "verified-token";
        var first = await NewController(svc, pol, cap, httpContext1).ListAsync(
            q: null, category: null, sort: "Relevance",
            skip: 0, take: 50, language: "ro",
            cancellationToken: CancellationToken.None);
        first.Result.Should().BeOfType<OkObjectResult>();

        var httpContext2 = new DefaultHttpContext();
        httpContext2.Request.Headers[PublicCatalogController.CaptchaTokenHeader] = "verified-token";
        var second = await NewController(svc, pol, cap, httpContext2).ListAsync(
            q: null, category: null, sort: "Relevance",
            skip: 0, take: 50, language: "ro",
            cancellationToken: CancellationToken.None);

        var obj = second.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be("https://cnas/captcha/required");
    }

    [Fact]
    public async Task NarrowSearch_DoesNotRequireCaptcha()
    {
        var svc = Substitute.For<IPublicCatalogService>();
        svc.ListAsync(Arg.Any<PublicCatalogListQueryDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<PublicCatalogListItemDto>>.Success(NewPage()));
        var pol = Substitute.For<ICaptchaPolicyEvaluator>();
        pol.RequireCaptcha(Arg.Any<PublicCatalogListQueryDto?>()).Returns(false);
        var cap = Substitute.For<ICaptchaChallengeService>();

        var controller = NewController(svc, pol, cap);
        var result = await controller.ListAsync(
            q: "pension", category: null, sort: "Relevance",
            skip: 0, take: 50, language: "ro",
            cancellationToken: CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        // Captcha service must NOT have been consulted when policy says
        // "not required" — proves the short-circuit.
        await cap.DidNotReceiveWithAnyArgs().IsRecentlyVerifiedAsync(default, default);
    }
}
