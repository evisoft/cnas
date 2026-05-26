using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers.ServiceManagement;

/// <summary>
/// R2506 / TOR PIR 037-040 — tests for <see cref="QualityRisksController"/>.
/// </summary>
public sealed class QualityRisksControllerTests
{
    private static QualityRiskDto NewDto(string id = "SQID-1") => new(
        Id: id,
        RiskCode: "DATA_LOSS",
        Title: "Risk of payroll data loss during migration",
        Description: "Possible corruption of payroll data during the legacy-to-PostgreSQL migration window.",
        Category: QualityRiskCategory.Technical.ToString(),
        Likelihood: QualityRiskLikelihood.Possible.ToString(),
        Impact: QualityRiskImpact.Major.ToString(),
        Status: QualityRiskStatus.Open.ToString(),
        OwnerSqid: "USR-1",
        IdentifiedAt: new DateTime(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc),
        LastReviewedAt: null,
        ClosedAt: null,
        ClosureReason: null);

    [Fact]
    public async Task Create_HappyPath_Returns200()
    {
        var svc = Substitute.For<IQualityRiskService>();
        svc.CreateRiskAsync(Arg.Any<QualityRiskCreateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<QualityRiskDto>.Success(NewDto())));
        var controller = new QualityRisksController(svc);

        var input = new QualityRiskCreateInputDto(
            RiskCode: "DATA_LOSS",
            Title: "Risk of payroll data loss during migration",
            Description: "Possible corruption of payroll data during the legacy-to-PostgreSQL migration window.",
            Category: "Technical",
            Likelihood: "Possible",
            Impact: "Major",
            OwnerSqid: "SQID-1");

        var result = await controller.CreateAsync(input, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<QualityRiskDto>();
    }

    [Fact]
    public async Task RecordReview_NotOwner_Returns403()
    {
        var svc = Substitute.For<IQualityRiskService>();
        svc.RecordReviewAsync("SQID-1", Arg.Any<QualityRiskReviewInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<QualityRiskDto>.Failure(
                ErrorCodes.QualityRiskNotOwner, "Only the owner can review.")));
        var controller = new QualityRisksController(svc);

        var result = await controller.RecordReviewAsync(
            "SQID-1",
            new QualityRiskReviewInputDto("Reviewed."),
            CancellationToken.None);

        var status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(403);
    }
}
