using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0196 / TOR CF 23.02 — tests for <see cref="AuditCategoriesController"/>.
/// Verifies the controller delegates to <see cref="IAuditCategoryService"/>
/// and maps Result.Failure codes to the appropriate HTTP status.
/// </summary>
public sealed class AuditCategoriesControllerTests
{
    private static AuditCategoryDto NewDto(string id = "SQID-1", string code = "AUTH", bool isActive = true) =>
        new(Id: id, Code: code, DisplayName: "Authentication", Description: null,
            DefaultSeverity: "Notice", IsActive: isActive);

    [Fact]
    public async Task Create_HappyPath_Returns_200()
    {
        var svc = Substitute.For<IAuditCategoryService>();
        svc.CreateAsync(Arg.Any<AuditCategoryCreateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AuditCategoryDto>.Success(NewDto())));
        var controller = new AuditCategoriesController(svc);

        var input = new AuditCategoryCreateInputDto("AUTH", "Authentication", null, "Notice");
        var result = await controller.CreateAsync(input, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<AuditCategoryDto>();
    }

    [Fact]
    public async Task Create_Duplicate_Returns_409()
    {
        var svc = Substitute.For<IAuditCategoryService>();
        svc.CreateAsync(Arg.Any<AuditCategoryCreateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AuditCategoryDto>.Failure(
                IAuditCategoryService.DuplicateCategoryCodeCode, "exists")));
        var controller = new AuditCategoriesController(svc);

        var input = new AuditCategoryCreateInputDto("AUTH", "Authentication", null, "Notice");
        var result = await controller.CreateAsync(input, CancellationToken.None);

        var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task GetByCode_NotFound_Returns_404()
    {
        var svc = Substitute.For<IAuditCategoryService>();
        svc.GetByCodeAsync("UNKNOWN", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AuditCategoryDto>.Failure(ErrorCodes.NotFound, "not found")));
        var controller = new AuditCategoriesController(svc);

        var result = await controller.GetByCodeAsync("UNKNOWN", CancellationToken.None);

        var nf = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        nf.StatusCode.Should().Be(404);
    }
}
