using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0321 / R0224 / UI 008 — unit tests for <see cref="ApplicationVersionsController"/>.
/// Direct construction with stubbed dependencies; the underlying
/// <see cref="IApplicationVersionService"/> is faked with NSubstitute. The controller
/// no longer touches the DB context directly — every ownership check lives in the
/// service layer — so the tests are purely shape assertions over the HTTP surface.
/// </summary>
public sealed class ApplicationVersionsControllerTests
{
    private const string ApplicationSqid = "SQID-8001";

    [Fact]
    public async Task SaveAsync_ValidBody_Returns201_WithLocationHeader()
    {
        var versions = Substitute.For<IApplicationVersionService>();
        versions.SaveAsync(
            ApplicationSqid,
            "{\"x\":1}",
            ApplicationVersionSource.ManualSave,
            null,
            Arg.Any<CancellationToken>())
            .Returns(Result<ApplicationVersionOutputDto>.Success(new ApplicationVersionOutputDto(
                Id: "SQID-VER1",
                ApplicationSqid: ApplicationSqid,
                VersionNumber: 1,
                FormDataJson: "{\"x\":1}",
                CreatedByUserSqid: "SQID-7777",
                Source: nameof(ApplicationVersionSource.ManualSave),
                CreatedAtUtc: DateTime.UtcNow,
                Note: null,
                IsCurrent: true)));

        var controller = NewController(versions);

        var result = await controller.SaveAsync(
            ApplicationSqid,
            new ApplicationVersionSaveDto(
                FormDataJson: "{\"x\":1}",
                Source: nameof(ApplicationVersionSource.ManualSave),
                Note: null),
            CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be(nameof(ApplicationVersionsController.GetAsync));
        created.RouteValues.Should().NotBeNull();
        created.RouteValues!["applicationSqid"].Should().Be(ApplicationSqid);
        created.RouteValues["versionNumber"].Should().Be(1);

        var payload = created.Value.Should().BeOfType<ApplicationVersionOutputDto>().Subject;
        payload.Id.Should().Be("SQID-VER1");
        payload.VersionNumber.Should().Be(1);
    }

    [Fact]
    public async Task RevertAsync_ReturnsOkWithNewVersion_NotTheTarget()
    {
        var versions = Substitute.For<IApplicationVersionService>();
        // The service returns the NEW row (version 3, source=Revert) — the controller
        // must surface that exact value, not the target version.
        var newRow = new ApplicationVersionOutputDto(
            Id: "SQID-VER3",
            ApplicationSqid: ApplicationSqid,
            VersionNumber: 3,
            FormDataJson: "{\"x\":1}",
            CreatedByUserSqid: "SQID-7777",
            Source: nameof(ApplicationVersionSource.Revert),
            CreatedAtUtc: DateTime.UtcNow,
            Note: "Reverted to version 1",
            IsCurrent: true);
        versions.RevertAsync(ApplicationSqid, 1, Arg.Any<CancellationToken>())
            .Returns(Result<ApplicationVersionOutputDto>.Success(newRow));

        var controller = NewController(versions);

        var result = await controller.RevertAsync(ApplicationSqid, 1, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        var body = ok.Value.Should().BeOfType<ApplicationVersionOutputDto>().Subject;
        body.VersionNumber.Should().Be(3);
        body.Source.Should().Be(nameof(ApplicationVersionSource.Revert));
    }

    /// <summary>Builds a controller with a real validator and the supplied service stub.</summary>
    /// <param name="versions">Service substitute.</param>
    /// <returns>Wired controller.</returns>
    private static ApplicationVersionsController NewController(IApplicationVersionService versions)
        => new(versions, new ApplicationVersionSaveDtoValidator());
}
