using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// R2461 / R2462 — controller tests for
/// <see cref="ReportingAdminController"/>. Verifies the cnas-admin
/// authorize gate, the support-monthly happy path, the error-fix-monthly
/// happy path, and the 400-on-bad-month branch.
/// </summary>
public sealed class ReportingAdminControllerTests
{
    private static MonthlySupportReportDto NewSupportDto()
        => new(
            Month: new DateOnly(2026, 4, 1),
            GeneratedAtUtc: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            TotalSubmitted: 10,
            TotalResolved: 8,
            TotalClosed: 7,
            TotalEscalated: 1,
            TotalCancelled: 0,
            AvgFirstResponseMinutes: 12.5m,
            AvgResolutionMinutes: 240.75m,
            FirstResponseBreachRate: 0.1m,
            ResolutionBreachRate: 0.05m,
            SeverityBreakdown: Array.Empty<MonthlySupportSeverityBreakdownRow>(),
            CategoryBreakdown: Array.Empty<MonthlySupportCategoryBreakdownRow>());

    private static MonthlyErrorFixReportDto NewErrorFixDto()
        => new(
            Month: new DateOnly(2026, 4, 1),
            GeneratedAtUtc: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            TotalIntegrityFindings: 5,
            IntegrityFindingsByCriticalSeverity: 1,
            IntegrityFindingsByHighSeverity: 2,
            IntegrityFindingsByMediumSeverity: 1,
            IntegrityFindingsByLowSeverity: 1,
            TotalChangeRequestsRolledBack: 2,
            TotalChangeRequestsDeployed: 6,
            TotalDocumentationTemplatesUpdated: 3,
            CategoryBreakdown: Array.Empty<MonthlyErrorFixCategoryBreakdownRow>());

    /// <summary>R2461 / R2462 — the controller carries the cnas-admin policy.</summary>
    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(ReportingAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    /// <summary>R2461 — GET /support-monthly happy path returns 200 with the report.</summary>
    [Fact]
    public async Task GetSupportMonthly_HappyPath_Returns200()
    {
        var dto = NewSupportDto();
        var support = Substitute.For<IMonthlySupportReportService>();
        support.ComputeAsync(
                Arg.Any<MonthlySupportReportInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MonthlySupportReportDto>.Success(dto)));
        var errorFix = Substitute.For<IMonthlyErrorFixReportService>();
        var controller = new ReportingAdminController(support, errorFix);

        var result = await controller.GetSupportMonthlyAsync(
            month: "2026-04-01",
            categoryCodes: "AUTH,PAYMENT",
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>R2462 — GET /error-fix-monthly happy path returns 200 with the report.</summary>
    [Fact]
    public async Task GetErrorFixMonthly_HappyPath_Returns200()
    {
        var dto = NewErrorFixDto();
        var support = Substitute.For<IMonthlySupportReportService>();
        var errorFix = Substitute.For<IMonthlyErrorFixReportService>();
        errorFix.ComputeAsync(
                Arg.Any<MonthlyErrorFixReportInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MonthlyErrorFixReportDto>.Success(dto)));
        var controller = new ReportingAdminController(support, errorFix);

        var result = await controller.GetErrorFixMonthlyAsync(
            month: "2026-04-01",
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>R2461 — malformed month returns 400 ProblemDetails.</summary>
    [Fact]
    public async Task GetSupportMonthly_BadMonth_Returns400()
    {
        var support = Substitute.For<IMonthlySupportReportService>();
        var errorFix = Substitute.For<IMonthlyErrorFixReportService>();
        var controller = new ReportingAdminController(support, errorFix);

        var result = await controller.GetSupportMonthlyAsync(
            month: "not-a-date",
            categoryCodes: null,
            cancellationToken: CancellationToken.None);

        var obj = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(400);
    }
}
