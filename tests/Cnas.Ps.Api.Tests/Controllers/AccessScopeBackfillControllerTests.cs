using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0671 continuation — unit tests for
/// <see cref="AccessScopeBackfillController"/>. Mirrors the direct-construction
/// pattern used elsewhere in the controller test suite: the service is faked
/// with NSubstitute and the controller is exercised without booting the HTTP
/// pipeline. The 401 / 403 expectations live in the attribute-shape assertions
/// because the controller relies on the <c>[Authorize(Roles="cnas-admin")]</c>
/// declarative gate.
/// </summary>
public sealed class AccessScopeBackfillControllerTests
{
    /// <summary>Empty per-row failure list reused by the happy-path result.</summary>
    private static readonly AccessScopeBackfillFailureDto[] EmptyFailures =
        Array.Empty<AccessScopeBackfillFailureDto>();

    /// <summary>One-element explicit-sqid list reused by happy-path inputs.</summary>
    private static readonly string[] OneSqid = ["SQID-1"];

    /// <summary>Builds a fresh controller around the supplied service substitute.</summary>
    private static AccessScopeBackfillController NewController(IAccessScopeBackfillService svc) =>
        new(svc);

    // ─────────────────────── Happy path ───────────────────────

    /// <summary>
    /// On the Solicitant backfill happy path the controller forwards the result
    /// DTO verbatim as 200 OK.
    /// </summary>
    [Fact]
    public async Task BackfillSolicitantRegion_Success_Returns200WithResult()
    {
        var svc = Substitute.For<IAccessScopeBackfillService>();
        var dto = new AccessScopeBackfillResultDto(
            RowsUpdated: 5, MatchedSqidCount: 5, Failures: EmptyFailures);
        svc.AssignSolicitantRegionByPatternAsync(
                Arg.Any<AccessScopeSolicitantBackfillInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<AccessScopeBackfillResultDto>.Success(dto));
        var controller = NewController(svc);

        var result = await controller.BackfillSolicitantRegionAsync(
            new AccessScopeSolicitantBackfillInputDto(
                RegionCode: "CHIS",
                ExplicitSolicitantSqids: OneSqid),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>
    /// Quota-exceeded failure maps to a 400 ProblemDetails with the back-fill
    /// error code in the body's title (default ProblemDetails behaviour).
    /// </summary>
    [Fact]
    public async Task BackfillSolicitantRegion_QuotaExceeded_Returns400()
    {
        var svc = Substitute.For<IAccessScopeBackfillService>();
        svc.AssignSolicitantRegionByPatternAsync(
                Arg.Any<AccessScopeSolicitantBackfillInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<AccessScopeBackfillResultDto>.Failure(
                ErrorCodes.BackfillQuotaExceeded, "too many rows"));
        var controller = NewController(svc);

        var result = await controller.BackfillSolicitantRegionAsync(
            new AccessScopeSolicitantBackfillInputDto(
                RegionCode: "CHIS",
                ExplicitSolicitantSqids: OneSqid),
            CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// On the Application backfill happy path the controller forwards the result
    /// DTO verbatim as 200 OK.
    /// </summary>
    [Fact]
    public async Task BackfillApplicationSubdivision_Success_Returns200WithResult()
    {
        var svc = Substitute.For<IAccessScopeBackfillService>();
        var dto = new AccessScopeBackfillResultDto(
            RowsUpdated: 3, MatchedSqidCount: 3, Failures: EmptyFailures);
        svc.AssignServiceApplicationSubdivisionByPatternAsync(
                Arg.Any<AccessScopeApplicationBackfillInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<AccessScopeBackfillResultDto>.Success(dto));
        var controller = NewController(svc);

        var result = await controller.BackfillApplicationSubdivisionAsync(
            new AccessScopeApplicationBackfillInputDto(
                SubdivisionCode: "CHISINAU-CENTRU",
                ExplicitApplicationSqids: OneSqid),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    // ─────────────────────── Authorisation surface ───────────────────────

    /// <summary>
    /// Anonymous → 401 + non-admin authenticated → 403 are enforced declaratively
    /// by the controller's <c>[Authorize(Roles = "cnas-admin")]</c> attribute.
    /// Verify the attribute presence + role so a future drive-by edit cannot
    /// silently downgrade the gate.
    /// </summary>
    [Fact]
    public void NonAdmin_RejectedByAuthorizeAttribute()
    {
        var attr = typeof(AccessScopeBackfillController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull(
            "the controller MUST be gated by an explicit Authorize role attribute");
        attr!.Roles.Should().Be("cnas-admin",
            "non-admin callers (including unauthenticated) must be rejected before any action runs");
    }
}
