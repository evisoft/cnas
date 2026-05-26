using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R1900-R1905 / iter-145 — tests for <see cref="ReportCatalogAdminController"/>.
/// Verifies the cnas-admin authorize gate, the list endpoint, and the
/// refresh endpoint happy path.
/// </summary>
public sealed class ReportCatalogAdminControllerTests
{
    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(ReportCatalogAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task List_HappyPath_Returns200WithCatalogPage()
    {
        var page = new ReportCatalogPageDto(
            Items: new List<ReportCatalogRowDto>
            {
                new(
                    Id: "SQID-1",
                    Code: "RPT-PEN-ACTIVE",
                    NameRo: "Beneficiari pensii active",
                    Purpose: "Lista beneficiarilor.",
                    Audience: "cnas-decider",
                    Frequency: "OnDemand",
                    ParametersJson: "{}",
                    ColumnsJson: "[]",
                    RbacRole: "cnas-decider",
                    Schedule: "OnDemand",
                    OutputFormatsJson: "[\"csv\"]",
                    Category: "PaymentsProcessed",
                    DefaultFormat: "xlsx",
                    IsPublic: false),
            },
            Total: 1);
        var svc = Substitute.For<IReportCatalogSeedService>();
        svc.ListAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ReportCatalogPageDto>.Success(page)));

        var controller = new ReportCatalogAdminController(svc);
        var result = await controller.ListAsync(category: null, frequency: null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task List_WithCategoryFilter_PassesFilterThrough()
    {
        var page = new ReportCatalogPageDto(
            Items: Array.Empty<ReportCatalogRowDto>(),
            Total: 0);
        var svc = Substitute.For<IReportCatalogSeedService>();
        svc.ListAsync("AuditSecurity", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ReportCatalogPageDto>.Success(page)));

        var controller = new ReportCatalogAdminController(svc);
        var result = await controller.ListAsync(category: "AuditSecurity");

        result.Result.Should().BeOfType<OkObjectResult>();
        await svc.Received(1).ListAsync(
            "AuditSecurity",
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_HappyPath_Returns200WithCounters()
    {
        var outcome = new ReportCatalogRefreshResultDto(
            Inserted: 55,
            Updated: 0,
            Unchanged: 0,
            Total: 55);
        var svc = Substitute.For<IReportCatalogSeedService>();
        svc.RefreshAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ReportCatalogRefreshResultDto>.Success(outcome)));

        var controller = new ReportCatalogAdminController(svc);
        var result = await controller.RefreshAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(outcome);
    }

    [Fact]
    public async Task Refresh_OnDbFailure_Returns500()
    {
        var svc = Substitute.For<IReportCatalogSeedService>();
        svc.RefreshAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                Result<ReportCatalogRefreshResultDto>.Failure("INTERNAL_ERROR", "db down")));

        var controller = new ReportCatalogAdminController(svc);
        var result = await controller.RefreshAsync(CancellationToken.None);

        var status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(500);
    }
}
