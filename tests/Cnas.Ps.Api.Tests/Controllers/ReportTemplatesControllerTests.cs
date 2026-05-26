using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — controller-shape tests for
/// <see cref="ReportTemplatesController"/>. Direct-construction style mirroring
/// the rest of the controller suite.
/// </summary>
public sealed class ReportTemplatesControllerTests
{
    /// <summary>Reused stubs for the constructor dependencies.</summary>
    private static (IReportTemplateService Tpl, IReportEngine Eng, ISqidService Sqids) NewMocks()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode(Arg.Any<string?>()).Returns(Result<long>.Success(42L));
        return (Substitute.For<IReportTemplateService>(), Substitute.For<IReportEngine>(), sqids);
    }

    private static ReportTemplatesController NewController(
        IReportTemplateService tpl,
        IReportEngine eng,
        ISqidService sqids) => new(tpl, eng, sqids);

    /// <summary>Reused empty dictionary literal — avoids inline new[].</summary>
    private static readonly IReadOnlyDictionary<string, object?> EmptyCells =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Reused empty column list.</summary>
    private static readonly string[] NoColumns = Array.Empty<string>();

    [Fact]
    public void Controller_IsGatedBy_CnasUser_Policy()
    {
        var attr = typeof(ReportTemplatesController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(AuthorizationComposition.CnasUser);
    }

    [Fact]
    public async Task Run_ServiceSuccess_Returns200_WithExecutionDto()
    {
        var (tpl, eng, sqids) = NewMocks();
        var execution = new ReportExecutionResultDto(
            Columns: NoColumns,
            Rows: Array.Empty<ReportRowDto>(),
            TotalRowCount: 0,
            ElapsedMs: 5);
        eng.RunAsync(42L, 0, 50, Arg.Any<CancellationToken>())
            .Returns(Result<ReportExecutionResultDto>.Success(execution));

        var controller = NewController(tpl, eng, sqids);
        var result = await controller.RunAsync("k3Gq9", 0, 50, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(execution);
    }

    [Fact]
    public async Task Run_BudgetTooBroad_Returns422()
    {
        var (tpl, eng, sqids) = NewMocks();
        eng.RunAsync(42L, 0, 50, Arg.Any<CancellationToken>())
            .Returns(Result<ReportExecutionResultDto>.Failure(ErrorCodes.QueryTooBroad, "too broad"));

        var controller = NewController(tpl, eng, sqids);
        var result = await controller.RunAsync("k3Gq9", 0, 50, CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
    }
}
