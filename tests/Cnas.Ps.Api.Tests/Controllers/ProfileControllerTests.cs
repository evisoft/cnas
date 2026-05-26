using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="ProfileController"/>. Mirrors the direct-construction pattern
/// used elsewhere in the controller test suite: the service is faked with NSubstitute and
/// the controller is exercised without booting the HTTP pipeline. Authorization
/// (<c>[Authorize]</c>) and rate-limiting attributes are out of scope here — they are
/// validated by the integration-style harness tests.
/// </summary>
public sealed class ProfileControllerTests
{
    /// <summary>
    /// Cached <see cref="System.Text.Json.JsonSerializerOptions"/> instance reused
    /// by the wire-shape verification tests (CA1869 — never allocate per call).
    /// Mirrors the ASP.NET Core Web defaults so the serialized output matches
    /// what the framework would produce on the real pipeline.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions WireJsonOptions
        = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static IProfileService NewServiceMock() => Substitute.For<IProfileService>();

    /// <summary>
    /// Builds the SUT around the supplied service. A live
    /// <see cref="Cnas.Ps.Application.Validators.ProfileLanguageInputValidator"/>
    /// is passed for the R0211 language-PUT route (these tests do not exercise it
    /// but the constructor signature requires it).
    /// </summary>
    /// <param name="svc">Profile-service substitute.</param>
    /// <returns>A fresh controller.</returns>
    private static ProfileController NewController(IProfileService svc)
        => new(
            svc,
            new Cnas.Ps.Application.Validators.ProfileLanguageInputValidator(),
            new Cnas.Ps.Application.Validators.ProfileContactInputValidator());

    [Fact]
    public async Task GetMine_Success_Returns200WithProfile()
    {
        // Arrange — service returns a populated profile.
        var svc = NewServiceMock();
        var profile = new ProfileOutput(
            Id: "k3Gq9",
            DisplayName: "Ion Popescu",
            Email: "ion@example.md",
            Phone: "+37369123456",
            PreferredLanguage: "ro",
            IssuedDocuments: Array.Empty<IssuedDocumentSummaryDto>());
        svc.GetMineAsync(Arg.Any<CancellationToken>())
           .Returns(Result<ProfileOutput>.Success(profile));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetMineAsync(CancellationToken.None);

        // Assert — 200 with the body forwarded verbatim.
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(profile);
    }

    /// <summary>
    /// R0621 / TOR CF 13.02 — when the service yields a profile that carries
    /// at least one issued-document summary the controller serialises the
    /// <c>issuedDocuments</c> slice into the wire JSON with the expected
    /// camelCase property name and per-row fields.
    /// </summary>
    [Fact]
    public async Task GetMine_WithIssuedDocuments_SerializesIssuedDocumentsArray()
    {
        // Arrange — service returns a profile with two issued-document rows.
        var svc = NewServiceMock();
        var doc1 = new IssuedDocumentSummaryDto(
            Sqid: "doc-A",
            DocumentTypeCode: "Decision",
            Title: "Decision 2026-1",
            IssuedAtUtc: new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc),
            Channel: IssuedDocumentChannel.Electronic,
            Status: "Active",
            DownloadUrl: "/api/documents/doc-A/download");
        var doc2 = new IssuedDocumentSummaryDto(
            Sqid: "doc-B",
            DocumentTypeCode: "Certificate",
            Title: "Certificate 2026-2",
            IssuedAtUtc: new DateTime(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc),
            Channel: IssuedDocumentChannel.Paper,
            Status: "Active",
            DownloadUrl: null);
        var profile = new ProfileOutput(
            Id: "k3Gq9",
            DisplayName: "Ion Popescu",
            Email: null,
            Phone: null,
            PreferredLanguage: "ro",
            IssuedDocuments: new[] { doc1, doc2 });
        svc.GetMineAsync(Arg.Any<CancellationToken>())
           .Returns(Result<ProfileOutput>.Success(profile));
        var controller = NewController(svc);

        // Act — Ok wraps the DTO; serialise with the default ASP.NET Core JSON
        //       options (camelCase property naming) to verify the wire shape
        //       the WASM client + downstream consumers actually receive.
        var result = await controller.GetMineAsync(CancellationToken.None);
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(profile);

        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value, WireJsonOptions);

        json.Should().Contain("\"issuedDocuments\":",
            "the wire shape MUST expose the issued-documents slice on the profile aggregate.");
        json.Should().Contain("\"sqid\":\"doc-A\"");
        json.Should().Contain("\"documentTypeCode\":\"Decision\"");
        json.Should().Contain("\"sqid\":\"doc-B\"");
        json.Should().Contain("\"documentTypeCode\":\"Certificate\"");
    }

    [Fact]
    public async Task GetMine_NotFound_Returns404()
    {
        // Arrange — service signals the user-row no longer exists.
        var svc = NewServiceMock();
        svc.GetMineAsync(Arg.Any<CancellationToken>())
           .Returns(Result<ProfileOutput>.Failure(ErrorCodes.NotFound, "Profile not found."));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetMineAsync(CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetMine_Unauthorized_Returns401()
    {
        // Arrange — service signals the caller is anonymous (UserId is null upstream).
        // ASP.NET's [Authorize] should normally prevent this, but the service still
        // returns Unauthorized as defense in depth.
        var svc = NewServiceMock();
        svc.GetMineAsync(Arg.Any<CancellationToken>())
           .Returns(Result<ProfileOutput>.Failure(ErrorCodes.Unauthorized, "Not authenticated."));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetMineAsync(CancellationToken.None);

        // Assert — Unauthorized maps to 401 ProblemDetails.
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task UpdateMine_Success_Returns204()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.UpdateMineAsync(Arg.Any<ProfileUpdateInput>(), Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = NewController(svc);

        // Act
        var input = new ProfileUpdateInput(
            Email: "new@example.md",
            Phone: "+37369000111",
            PreferredLanguage: "en");
        var result = await controller.UpdateMineAsync(input, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task UpdateMine_ValidationFailure_Returns400()
    {
        // Arrange — service rejects a bad value at the boundary.
        var svc = NewServiceMock();
        svc.UpdateMineAsync(Arg.Any<ProfileUpdateInput>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.ValidationFailed, "Invalid preferred language."));
        var controller = NewController(svc);

        // Act
        var input = new ProfileUpdateInput(null, null, "xx");
        var result = await controller.UpdateMineAsync(input, CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Invalid preferred language.");
    }

    [Fact]
    public async Task UpdateMine_NotFound_Returns404()
    {
        // Arrange
        var svc = NewServiceMock();
        svc.UpdateMineAsync(Arg.Any<ProfileUpdateInput>(), Arg.Any<CancellationToken>())
           .Returns(Result.Failure(ErrorCodes.NotFound, "Profile not found."));
        var controller = NewController(svc);

        // Act
        var input = new ProfileUpdateInput(null, null, "ro");
        var result = await controller.UpdateMineAsync(input, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task UpdateMine_NullBody_ThrowsArgumentNullException()
    {
        // The controller must guard against a null body even though [FromBody] normally
        // populates it. ASP.NET's filter pipeline turns the throw into a 400.
        var controller = NewController(NewServiceMock());

        await FluentActions.Awaiting(() =>
                controller.UpdateMineAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }
}
