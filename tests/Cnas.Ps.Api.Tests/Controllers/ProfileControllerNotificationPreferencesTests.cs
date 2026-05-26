using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for the notification-preference endpoints added to
/// <see cref="ProfileController"/> in R0171 (CF 22.02 / CF 04.08). Mirrors the
/// direct-construction pattern of <see cref="ProfileControllerTests"/>; the
/// service is faked with NSubstitute and the controller is exercised without
/// booting the HTTP pipeline. <c>[Authorize]</c> and rate-limiting attributes
/// are out of scope here — they are validated by the integration-style harness
/// tests.
/// </summary>
public sealed class ProfileControllerNotificationPreferencesTests
{
    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static IProfileService NewServiceMock() => Substitute.For<IProfileService>();

    /// <summary>Builds the SUT around the supplied service.</summary>
    /// <param name="svc">Profile-service substitute.</param>
    /// <returns>A fresh controller wired with the live R0211 language validator.</returns>
    private static ProfileController NewController(IProfileService svc)
        => new(
            svc,
            new Cnas.Ps.Application.Validators.ProfileLanguageInputValidator(),
            new Cnas.Ps.Application.Validators.ProfileContactInputValidator());

    [Fact]
    public async Task Get_Unauthorized_Returns401()
    {
        // Arrange — the service signals an anonymous caller (defence-in-depth path).
        var svc = NewServiceMock();
        svc.GetNotificationPreferencesAsync(Arg.Any<CancellationToken>())
           .Returns(Result<NotificationPreferencesDto>.Failure(
               ErrorCodes.Unauthorized, "Not authenticated."));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetNotificationPreferencesAsync(CancellationToken.None);

        // Assert — 401 ProblemDetails.
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Get_Authenticated_ReturnsCurrentPreferences()
    {
        // Arrange — the service returns a populated preferences DTO.
        var svc = NewServiceMock();
        var dto = new NotificationPreferencesDto(
            Email: true,
            Sms: false,
            InApp: true,
            Categories: new Dictionary<string, bool> { ["applicationStatus"] = true });
        svc.GetNotificationPreferencesAsync(Arg.Any<CancellationToken>())
           .Returns(Result<NotificationPreferencesDto>.Success(dto));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetNotificationPreferencesAsync(CancellationToken.None);

        // Assert — 200 with the body forwarded verbatim.
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task Put_ValidBody_Returns204()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.SetNotificationPreferencesAsync(Arg.Any<NotificationPreferencesDto>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);
        var input = new NotificationPreferencesDto(
            Email: false, Sms: true, InApp: true,
            Categories: new Dictionary<string, bool>());

        // Act
        var result = await controller.SetNotificationPreferencesAsync(input, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Put_OversizedCategoryKey_Returns400()
    {
        // Arrange — the service rejects the oversized key (>64 chars). The controller
        // simply forwards the ValidationFailed code to a 400 ProblemDetails.
        var svc = NewServiceMock();
        svc.SetNotificationPreferencesAsync(Arg.Any<NotificationPreferencesDto>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.ValidationFailed, "Category key exceeds 64 chars."));
        var controller = NewController(svc);
        var input = new NotificationPreferencesDto(
            Email: true, Sms: true, InApp: true,
            Categories: new Dictionary<string, bool> { [new string('x', 100)] = true });

        // Act
        var result = await controller.SetNotificationPreferencesAsync(input, CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Category key exceeds 64 chars.");
    }

    [Fact]
    public async Task Put_NullBody_ThrowsArgumentNullException()
    {
        // ASP.NET's filter pipeline turns the throw into a 400 in production — the
        // controller's contract is purely "guard against null inputs".
        var controller = NewController(NewServiceMock());

        await FluentActions.Awaiting(() =>
                controller.SetNotificationPreferencesAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }
}
