using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0580 / TOR CF 09.02 — unit tests for
/// <see cref="AdHocReportsController"/>.
/// </summary>
public sealed class AdHocReportsControllerTests
{
    /// <summary>Reused column projection (CA1861 — no inline new[] arrays).</summary>
    private static readonly string[] IdOnly = ["Id"];

    /// <summary>Empty filter list reused across facts.</summary>
    private static readonly AdHocReportFilterDto[] NoFilters = Array.Empty<AdHocReportFilterDto>();

    /// <summary>Happy path returns 200 with the result DTO.</summary>
    [Fact]
    public async Task BuildAsync_Success_Returns200()
    {
        var svc = Substitute.For<IAdHocReportBuilder>();
        var dto = new AdHocReportResultDto(
            IdOnly,
            new List<IReadOnlyDictionary<string, object?>>(),
            0);
        svc.BuildAsync(Arg.Any<AdHocReportSpecDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<AdHocReportResultDto>.Success(dto));
        var validator = Substitute.For<IValidator<AdHocReportSpecDto>>();
        validator.ValidateAsync(Arg.Any<AdHocReportSpecDto>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult());
        var controller = new AdHocReportsController(svc, validator);

        var result = await controller.BuildAsync(
            new AdHocReportSpecDto(
                AdHocReportEntitySets.Applications,
                IdOnly,
                NoFilters,
                null, false),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>AdHocReportTooLarge maps to 422.</summary>
    [Fact]
    public async Task BuildAsync_TooLarge_Returns422()
    {
        var svc = Substitute.For<IAdHocReportBuilder>();
        svc.BuildAsync(Arg.Any<AdHocReportSpecDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<AdHocReportResultDto>.Failure(
                ErrorCodes.AdHocReportTooLarge, "result set too large"));
        var validator = Substitute.For<IValidator<AdHocReportSpecDto>>();
        validator.ValidateAsync(Arg.Any<AdHocReportSpecDto>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult());
        var controller = new AdHocReportsController(svc, validator);

        var result = await controller.BuildAsync(
            new AdHocReportSpecDto(
                AdHocReportEntitySets.Applications,
                IdOnly,
                NoFilters,
                null, false),
            CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
    }
}
