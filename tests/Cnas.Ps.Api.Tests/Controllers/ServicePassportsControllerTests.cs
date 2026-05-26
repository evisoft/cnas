using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="ServicePassportsController"/>. Direct-construction pattern
/// matching the rest of the suite — exercises controller branch logic with a NSubstitute
/// mock of <see cref="IServicePassportService"/>. Authorization (CnasAdmin policy) and
/// rate-limiting are validated by composition tests and the E2E journeys.
/// </summary>
public sealed class ServicePassportsControllerTests
{
    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static IServicePassportService NewServiceMock() =>
        Substitute.For<IServicePassportService>();

    /// <summary>Helper that returns a fresh matrix-service substitute.</summary>
    private static IServicePassportConfigMatrixService NewMatrixMock() =>
        Substitute.For<IServicePassportConfigMatrixService>();

    /// <summary>Helper that returns a fresh business-rules editor substitute.</summary>
    private static IServicePassportRulesEditorService NewRulesEditorMock() =>
        Substitute.For<IServicePassportRulesEditorService>();

    /// <summary>Builds the SUT around the supplied service (using a default matrix + rules stub).</summary>
    private static ServicePassportsController NewController(IServicePassportService svc) =>
        new(svc, NewMatrixMock(), NewRulesEditorMock());

    /// <summary>Builds the SUT around both service collaborators.</summary>
    private static ServicePassportsController NewController(
        IServicePassportService svc,
        IServicePassportConfigMatrixService matrix) =>
        new(svc, matrix, NewRulesEditorMock());

    /// <summary>Builds the SUT around all three service collaborators.</summary>
    private static ServicePassportsController NewController(
        IServicePassportService svc,
        IServicePassportConfigMatrixService matrix,
        IServicePassportRulesEditorService rulesEditor) =>
        new(svc, matrix, rulesEditor);

    /// <summary>Builds a sample passport input with sensible defaults.</summary>
    private static ServicePassportInput SampleInput(string? id = null) => new(
        Id: id,
        Code: "SP-TEST",
        NameRo: "Serviciu de test",
        NameEn: "Test service",
        NameRu: "Тестовый сервис",
        DescriptionRo: "Descriere.",
        FormSchemaJson: "{}",
        WorkflowCode: "wf-test",
        MaxProcessingDays: 30,
        IsEnabled: true,
        IsProactive: false,
        DecisionRulesJson: "{}");

    [Fact]
    public async Task ListAsync_Success_Returns200WithCatalog()
    {
        // Arrange — the service returns a single-row catalogue.
        var svc = NewServiceMock();
        IReadOnlyList<ServicePassportListItem> items =
        [
            new("k3Gq9", "SP-TEST", "Serviciu de test", true, 1),
        ];
        svc.ListAsync(Arg.Any<CancellationToken>())
           .Returns(Result<IReadOnlyList<ServicePassportListItem>>.Success(items));
        var controller = NewController(svc);

        // Act
        var result = await controller.ListAsync(CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(items);
    }

    [Fact]
    public async Task GetAsync_Success_Returns200WithDetail()
    {
        // Arrange — service returns a full detail row for a known Sqid.
        var svc = NewServiceMock();
        var detail = new ServicePassportDetailOutput(
            "k3Gq9", "SP-TEST", "Serviciu de test", null, null,
            "desc", "{}", "wf-test", 30, true, false, "{}", 1, true);
        svc.GetAsync("k3Gq9", Arg.Any<CancellationToken>())
           .Returns(Result<ServicePassportDetailOutput>.Success(detail));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetAsync("k3Gq9", CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(detail);
    }

    [Fact]
    public async Task GetAsync_NotFound_Returns404()
    {
        // Arrange — service reports the Sqid resolved to no row.
        var svc = NewServiceMock();
        svc.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<ServicePassportDetailOutput>.Failure(ErrorCodes.NotFound, "Not found."));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetAsync("missing", CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetAsync_InvalidSqid_Returns400()
    {
        // Arrange — service rejects the Sqid as malformed.
        var svc = NewServiceMock();
        svc.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<ServicePassportDetailOutput>.Failure(ErrorCodes.InvalidSqid, "Bad sqid."));
        var controller = NewController(svc);

        // Act
        var result = await controller.GetAsync("!!!", CancellationToken.None);

        // Assert
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Bad sqid.");
    }

    [Fact]
    public async Task CreateAsync_Success_Returns201_WithLocationAndBody()
    {
        // Arrange — service accepts the create and returns a fresh Sqid.
        var svc = NewServiceMock();
        svc.UpsertAsync(Arg.Any<ServicePassportInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Success("newSqid1"));
        var controller = NewController(svc);

        // Act — caller supplies an Id; controller must override it to null on create.
        var input = SampleInput(id: "ignored-by-controller");
        var result = await controller.CreateAsync(input, CancellationToken.None);

        // Assert — 201 Created with the new Sqid in the body + Location route value.
        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be(nameof(ServicePassportsController.GetAsync));
        created.RouteValues.Should().NotBeNull();
        created.RouteValues!["sqid"].Should().Be("newSqid1");
        created.Value.Should().Be("newSqid1");

        // Verify the create branch was hit (the service saw a null-Id input).
        await svc.Received(1).UpsertAsync(
            Arg.Is<ServicePassportInput>(i => i.Id == null && i.Code == "SP-TEST"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_NullBody_Throws()
    {
        // Defensive guard — ArgumentNullException flows to the framework filter as 400.
        var controller = NewController(NewServiceMock());
        await FluentActions.Awaiting(() =>
                controller.CreateAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UpdateAsync_RouteIdOverridesBodyId_AndForwardsToService()
    {
        // Arrange — service accepts the update and echoes the (route) Sqid.
        var svc = NewServiceMock();
        svc.UpsertAsync(Arg.Any<ServicePassportInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Success("routeSqid"));
        var controller = NewController(svc);

        // Act — body's Id is a decoy; controller must bind the route Sqid.
        var input = SampleInput(id: "body-id-should-be-ignored");
        var result = await controller.UpdateAsync("routeSqid", input, CancellationToken.None);

        // Assert — 200 with the Sqid as the body.
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be("routeSqid");

        // Verify the service saw the route id (NOT the body id).
        await svc.Received(1).UpsertAsync(
            Arg.Is<ServicePassportInput>(i => i.Id == "routeSqid"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_NotFound_Returns404()
    {
        // Arrange — service reports the passport id was not found.
        var svc = NewServiceMock();
        svc.UpsertAsync(Arg.Any<ServicePassportInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Failure(ErrorCodes.NotFound, "Service passport not found."));
        var controller = NewController(svc);

        // Act
        var result = await controller.UpdateAsync("ghost", SampleInput(), CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task UpdateAsync_ValidationFailed_Returns400()
    {
        // Arrange — service rejects on validation grounds (e.g. invalid form schema).
        var svc = NewServiceMock();
        svc.UpsertAsync(Arg.Any<ServicePassportInput>(), Arg.Any<CancellationToken>())
           .Returns(Result<string>.Failure(ErrorCodes.ValidationFailed, "Bad form schema."));
        var controller = NewController(svc);

        // Act
        var result = await controller.UpdateAsync("any", SampleInput(), CancellationToken.None);

        // Assert
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetConfigMatrixAsync_Success_Returns200WithMatrix()
    {
        // Arrange — the matrix service returns a complete 8-column projection.
        var matrix = NewMatrixMock();
        var dto = new ServicePassportConfigMatrixDto(
            Id: "k3Gq9",
            Code: "SP-TEST",
            Version: 1,
            FormSchemaJson: "{}",
            ValidationRulesJson: null,
            MandatoryAttachments: [],
            ReceiptTemplateCode: "recipisa",
            DecisionTemplateCode: "decizia-pensie",
            FisaCalculTemplateCode: "fisa-de-calcul",
            CalcFormulas: [],
            ProcessingRulesJson: "{}",
            PrintFormTemplateCode: "cerere");
        matrix.GetMatrixAsync("SP-TEST", Arg.Any<CancellationToken>())
            .Returns(Result<ServicePassportConfigMatrixDto>.Success(dto));
        var controller = NewController(NewServiceMock(), matrix);

        // Act
        var result = await controller.GetConfigMatrixAsync("SP-TEST", CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task GetConfigMatrixAsync_NotFound_Returns404()
    {
        // Arrange — the matrix service can't resolve the code.
        var matrix = NewMatrixMock();
        matrix.GetMatrixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<ServicePassportConfigMatrixDto>.Failure(
                ErrorCodes.NotFound, "Unknown passport."));
        var controller = NewController(NewServiceMock(), matrix);

        // Act
        var result = await controller.GetConfigMatrixAsync("MISSING", CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
