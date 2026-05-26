using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers.Profile;

/// <summary>
/// R0211 / TOR UI 003 — tests for the language-change route on
/// <see cref="ProfileController"/>. Mirrors the direct-construction pattern
/// used elsewhere in the controller test suite.
/// </summary>
public sealed class ProfileLanguageControllerTests
{
    /// <summary>Returns a fresh service substitute.</summary>
    /// <returns>NSubstitute mock.</returns>
    private static IProfileService NewServiceMock() => Substitute.For<IProfileService>();

    /// <summary>Constructs the SUT around the supplied service.</summary>
    /// <param name="svc">Profile-service substitute.</param>
    /// <returns>A fresh controller.</returns>
    private static ProfileController NewController(IProfileService svc)
        => new(svc, new ProfileLanguageInputValidator(), new ProfileContactInputValidator());

    [Fact]
    public async Task UpdateLanguage_ValidPayload_Returns204()
    {
        var svc = NewServiceMock();
        svc.GetMineAsync(Arg.Any<CancellationToken>()).Returns(
            Result<ProfileOutput>.Success(new ProfileOutput(
                Id: "SQID-1",
                DisplayName: "Test",
                Email: "x@example.md",
                Phone: null,
                PreferredLanguage: "ro",
                IssuedDocuments: Array.Empty<IssuedDocumentSummaryDto>())));
        svc.UpdateMineAsync(Arg.Any<ProfileUpdateInput>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        var result = await controller.UpdateLanguageAsync(
            new ProfileLanguageInputDto("en"),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        await svc.Received().UpdateMineAsync(
            Arg.Is<ProfileUpdateInput>(u => u.PreferredLanguage == "en"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateLanguage_DisallowedLanguage_Returns400()
    {
        var svc = NewServiceMock();
        var controller = NewController(svc);

        var result = await controller.UpdateLanguageAsync(
            new ProfileLanguageInputDto("fr"),
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await svc.DidNotReceive().UpdateMineAsync(Arg.Any<ProfileUpdateInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateLanguage_ProfileNotFound_Returns404()
    {
        var svc = NewServiceMock();
        svc.GetMineAsync(Arg.Any<CancellationToken>()).Returns(
            Result<ProfileOutput>.Failure(ErrorCodes.NotFound, "Profile not found."));
        var controller = NewController(svc);

        var result = await controller.UpdateLanguageAsync(
            new ProfileLanguageInputDto("en"),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }
}
