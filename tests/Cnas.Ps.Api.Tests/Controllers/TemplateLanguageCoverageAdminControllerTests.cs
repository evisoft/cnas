using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2003 / R0133 — tests for
/// <see cref="TemplateLanguageCoverageAdminController"/>. Verifies the
/// cnas-admin authorize gate, the GET coverage happy path, the POST scan
/// happy path, and the acknowledge endpoint.
/// </summary>
public sealed class TemplateLanguageCoverageAdminControllerTests
{
    /// <summary>CA1861 — hoisted to a static field to avoid per-call allocation.</summary>
    private static readonly string[] EnRoRu = ["en", "ro", "ru"];

    private static TemplateLanguageCoverageReportDto NewReportDto()
        => new(
            TotalTemplatesScanned: 5,
            TotalTemplatesFullyCovered: 3,
            TotalTemplatesWithGaps: 2,
            RequiredLanguages: EnRoRu,
            Gaps: Array.Empty<TemplateLanguageCoverageGapDto>(),
            Total: 0,
            Skip: 0,
            Take: 100,
            ComputedAtUtc: new DateTime(2026, 5, 23, 3, 45, 0, DateTimeKind.Utc));

    private static TemplateLanguageCoverageFindingDto NewFindingDto(string id = "SQID-1")
        => new(
            Id: id,
            TemplateSqid: "TPL-100",
            TemplateCode: "decizia-pensie",
            MissingLanguage: "en",
            DetectedAt: new DateTime(2026, 5, 23, 3, 45, 0, DateTimeKind.Utc),
            Acknowledged: true,
            AcknowledgedAt: new DateTime(2026, 5, 23, 4, 0, 0, DateTimeKind.Utc),
            AcknowledgedByUserSqid: "USR-7",
            AcknowledgementNote: "Translation queued in batch 42.");

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(TemplateLanguageCoverageAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task GetCoverage_HappyPath_Returns200()
    {
        var dto = NewReportDto();
        var svc = Substitute.For<ITemplateLanguageCoverageService>();
        svc.ComputeCoverageAsync(
                Arg.Any<TemplateLanguageCoverageFilterDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TemplateLanguageCoverageReportDto>.Success(dto)));
        var controller = new TemplateLanguageCoverageAdminController(svc);

        var result = await controller.GetCoverageAsync(
            requiredLanguages: "ro,en,ru",
            onlyApproved: true,
            includeRetiredTemplates: false,
            skip: 0,
            take: 100,
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task PostScan_HappyPath_Returns200()
    {
        var dto = NewReportDto();
        var svc = Substitute.For<ITemplateLanguageCoverageService>();
        svc.RecordCoverageRunAsync(
                Arg.Any<TemplateLanguageCoverageFilterDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TemplateLanguageCoverageReportDto>.Success(dto)));
        var controller = new TemplateLanguageCoverageAdminController(svc);

        var result = await controller.ScanAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task PostAcknowledge_HappyPath_Returns200()
    {
        var dto = NewFindingDto();
        var svc = Substitute.For<ITemplateLanguageCoverageService>();
        svc.AcknowledgeFindingAsync(
                "SQID-1",
                Arg.Any<TemplateLanguageCoverageAcknowledgeInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TemplateLanguageCoverageFindingDto>.Success(dto)));
        var controller = new TemplateLanguageCoverageAdminController(svc);

        var result = await controller.AcknowledgeAsync(
            sqid: "SQID-1",
            input: new TemplateLanguageCoverageAcknowledgeInputDto(Note: "Done."),
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task ListFindings_HappyPath_Returns200()
    {
        var page = new TemplateLanguageCoverageFindingPageDto(
            Items: new[] { NewFindingDto() },
            Total: 1,
            Skip: 0,
            Take: 50);
        var svc = Substitute.For<ITemplateLanguageCoverageService>();
        svc.ListFindingsAsync(
                Arg.Any<TemplateLanguageCoverageFindingFilterDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TemplateLanguageCoverageFindingPageDto>.Success(page)));
        var controller = new TemplateLanguageCoverageAdminController(svc);

        var result = await controller.ListFindingsAsync(
            acknowledged: false,
            missingLanguage: "en",
            skip: 0,
            take: 50,
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(page);
    }
}
