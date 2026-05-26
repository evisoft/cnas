using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2164 / TOR §15.4 INT 005 — direct-construction unit tests for
/// <see cref="WsdlPortalController"/>. Exercises the listing surface and the per-
/// controller WSDL endpoint's content-type + 404 branches.
/// </summary>
public sealed class WsdlPortalControllerTests
{
    [Fact]
    public void Controller_RequiresTechAdminPolicy()
    {
        var authorize = typeof(WsdlPortalController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        authorize.Should().NotBeNull();
        authorize!.Policy.Should().Be(Cnas.Ps.Api.Composition.AuthorizationComposition.CnasTechAdmin);
        typeof(WsdlPortalController)
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
            .Should()
            .BeEmpty();
    }

    /// <summary>Builds a fresh portal substitute.</summary>
    private static IWsdlPortalService NewPortalMock() => Substitute.For<IWsdlPortalService>();

    /// <summary>Builds the SUT around the supplied portal.</summary>
    private static WsdlPortalController NewController(IWsdlPortalService portal) => new(portal);

    [Fact]
    public async Task ListAsync_Success_Returns200WithRows()
    {
        var portal = NewPortalMock();
        IReadOnlyList<WsdlListingDto> rows =
        [
            new("Health", "/api/wsdl-portal/Health.wsdl", 3),
        ];
        portal.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<WsdlListingDto>>.Success(rows));
        var controller = NewController(portal);

        var result = await controller.ListAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(rows);
    }

    [Fact]
    public async Task GetWsdlAsync_Success_Returns200WithWsdlContentType()
    {
        var portal = NewPortalMock();
        var descriptor = new WsdlDescriptorDto(
            "Health",
            "<?xml version=\"1.0\"?><definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\" />",
            ["Ping"]);
        portal.GetForControllerAsync("Health", Arg.Any<CancellationToken>())
            .Returns(Result<WsdlDescriptorDto>.Success(descriptor));
        var controller = NewController(portal);

        var result = await controller.GetWsdlAsync("Health", CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Contain("application/wsdl+xml");
        content.Content.Should().Contain("definitions");
    }

    [Fact]
    public async Task GetWsdlAsync_NotFound_Returns404()
    {
        var portal = NewPortalMock();
        portal.GetForControllerAsync("UnknownController", Arg.Any<CancellationToken>())
            .Returns(Result<WsdlDescriptorDto>.Failure(ErrorCodes.NotFound, "missing"));
        var controller = NewController(portal);

        var result = await controller.GetWsdlAsync("UnknownController", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }
}
