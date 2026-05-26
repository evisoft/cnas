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
/// R1906 / TOR Annex 6 — tests for
/// <see cref="ReportDistributionRulesController"/>. Verifies the
/// cnas-admin authorize gate, the create-then-201 flow, the get-by-id
/// success path, and the list-with-filter happy path.
/// </summary>
public sealed class ReportDistributionRulesControllerTests
{
    private static ReportDistributionRuleDto MakeDto(string sqid = "SQID-1") => new(
        Id: sqid,
        ReportCode: "ACCESS_RIGHTS.FULL_MATRIX",
        Channel: "Email",
        RecipientKind: "EmailAddress",
        RecipientCode: "ops@example.org",
        Format: "Pdf",
        Priority: "Normal",
        IsActive: true,
        EffectiveFrom: new DateOnly(2026, 1, 1),
        EffectiveUntil: null,
        CreatedAt: DateTime.UtcNow,
        Notes: null);

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(ReportDistributionRulesController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task CreateAsync_HappyPath_Returns201()
    {
        var dto = MakeDto();
        var svc = Substitute.For<IReportDistributionService>();
        svc.CreateRuleAsync(Arg.Any<ReportDistributionRuleCreateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ReportDistributionRuleDto>.Success(dto)));

        var controller = new ReportDistributionRulesController(svc);
        var input = new ReportDistributionRuleCreateInputDto(
            ReportCode: "ACCESS_RIGHTS.FULL_MATRIX",
            Channel: "Email",
            RecipientKind: "EmailAddress",
            RecipientCode: "ops@example.org",
            Format: "Pdf",
            Priority: "Normal",
            EffectiveFrom: new DateOnly(2026, 1, 1),
            EffectiveUntil: null,
            Notes: null);

        var result = await controller.CreateAsync(input, CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.Value.Should().BeSameAs(dto);
        created.RouteValues!["sqid"].Should().Be(dto.Id);
    }

    [Fact]
    public async Task GetAsync_HappyPath_Returns200()
    {
        var dto = MakeDto("SQID-42");
        var svc = Substitute.For<IReportDistributionService>();
        svc.GetRuleByIdAsync("SQID-42", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ReportDistributionRuleDto>.Success(dto)));

        var controller = new ReportDistributionRulesController(svc);
        var result = await controller.GetAsync("SQID-42");

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task ListAsync_WithFilter_Returns200()
    {
        var page = new ReportDistributionRulePageDto(
            Items: new List<ReportDistributionRuleDto> { MakeDto() },
            Total: 1,
            Skip: 0,
            Take: 50);
        var svc = Substitute.For<IReportDistributionService>();
        svc.ListRulesAsync(Arg.Any<ReportDistributionRuleFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ReportDistributionRulePageDto>.Success(page)));

        var controller = new ReportDistributionRulesController(svc);
        var result = await controller.ListAsync(
            reportCode: "ACCESS_RIGHTS.FULL_MATRIX",
            channel: null,
            recipientKind: null,
            isActive: true,
            skip: 0,
            take: 50);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task GetAsync_NotFound_Returns404()
    {
        var svc = Substitute.For<IReportDistributionService>();
        svc.GetRuleByIdAsync("SQID-999", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ReportDistributionRuleDto>.Failure(ErrorCodes.NotFound, "missing")));

        var controller = new ReportDistributionRulesController(svc);
        var result = await controller.GetAsync("SQID-999");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }
}
