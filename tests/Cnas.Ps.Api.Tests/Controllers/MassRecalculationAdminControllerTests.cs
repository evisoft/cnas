using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Recalculation;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R1503 / TOR §3.7-D — tests for <see cref="MassRecalculationAdminController"/>.
/// </summary>
public sealed class MassRecalculationAdminControllerTests
{
    private static RecalculationRunDto SampleRun() => new(
        Id: "SQID-RUN-1",
        LegalChangeSqid: "SQID-EVT-1",
        TriggerKind: "Manual",
        Mode: "DryRun",
        Status: "Completed",
        StartedAt: DateTime.UtcNow,
        CompletedAt: DateTime.UtcNow,
        TotalDecisionsScanned: 10,
        TotalDecisionsRecalculated: 8,
        TotalSkipped: 2,
        TotalFailed: 0,
        TotalDeltaMdl: 1600m,
        FailureReason: null);

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(MassRecalculationAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task StartDryRun_HappyPath_Returns200()
    {
        var svc = Substitute.For<IMassRecalculationService>();
        svc.StartDryRunAsync("SQID-EVT-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<RecalculationRunDto>.Success(SampleRun())));
        var controller = new MassRecalculationAdminController(svc);

        var r = await controller.StartDryRunAsync("SQID-EVT-1");

        r.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetRun_HappyPath_Returns200()
    {
        var svc = Substitute.For<IMassRecalculationService>();
        svc.GetRunByIdAsync("SQID-RUN-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<RecalculationRunDto>.Success(SampleRun())));
        var controller = new MassRecalculationAdminController(svc);

        var r = await controller.GetRunAsync("SQID-RUN-1");

        r.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RejectResult_HappyPath_Returns200()
    {
        var dto = new RecalculationDecisionResultDto(
            Id: "SQID-RES-1",
            RunSqid: "SQID-RUN-1",
            BenefitDecisionId: 42L,
            BenefitType: "OldAgePension",
            BeneficiaryIdnpHash: "HASH-1",
            OldAmountMdl: 3000m,
            NewAmountMdl: 3200m,
            DeltaMdl: 200m,
            Status: "Rejected",
            Reason: "Operator excludes.",
            AppliedAt: null);
        var svc = Substitute.For<IMassRecalculationService>();
        svc.RejectResultAsync(
                "SQID-RES-1",
                Arg.Any<RecalculationResultRejectInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<RecalculationDecisionResultDto>.Success(dto)));
        var controller = new MassRecalculationAdminController(svc);

        var r = await controller.RejectResultAsync(
            "SQID-RES-1",
            new RecalculationResultRejectInputDto("Operator excludes."));

        r.Result.Should().BeOfType<OkObjectResult>();
    }
}
