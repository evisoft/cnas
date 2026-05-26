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
/// R2505 / TOR PIR 030-033 — tests for <see cref="ChangeRequestsController"/>.
/// </summary>
public sealed class ChangeRequestsControllerTests
{
    private static ChangeRequestDto NewDto(string id = "SQID-1") => new(
        Id: id,
        ChangeNumber: "CHG-2026-000001",
        Title: "Patch auth library",
        Description: "Upgrade the in-house auth library to mitigate CVE-2026-12345.",
        Kind: ChangeRequestKind.Normal.ToString(),
        Status: ChangeRequestStatus.Draft.ToString(),
        Risk: ChangeRequestRisk.Medium.ToString(),
        ImpactedSystems: "auth-api, web-portal",
        RollbackPlan: "Re-deploy previous container tag and restore the prior signing key from the vault.",
        TestEnvironmentValidationNote: null,
        TestValidatedBySqid: null,
        TestValidatedAt: null,
        CodeSignatureReference: null,
        CodeSignedBySqid: null,
        CodeSignedAt: null,
        RequestedBySqid: "USR-1",
        ApprovedBySqid: null,
        ApprovedAt: null,
        DeployedAt: null,
        RolledBackAt: null,
        RollbackReason: null,
        CancelReason: null,
        RelatedMaintenanceWindowSqid: null);

    [Fact]
    public async Task Create_HappyPath_Returns200()
    {
        var svc = Substitute.For<IChangeRequestService>();
        svc.CreateAsync(Arg.Any<ChangeRequestCreateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ChangeRequestDto>.Success(NewDto())));
        var controller = new ChangeRequestsController(svc);

        var input = new ChangeRequestCreateInputDto(
            Title: "Patch auth library",
            Description: "Upgrade the in-house auth library to mitigate CVE-2026-12345; deployed in low-traffic window.",
            Kind: "Normal",
            Risk: "Medium",
            ImpactedSystems: "auth-api, web-portal",
            RollbackPlan: "Re-deploy previous container tag and restore the prior signing key from the secrets vault.",
            RelatedMaintenanceWindowSqid: null);

        var result = await controller.CreateAsync(input, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ChangeRequestDto>();
    }

    [Fact]
    public async Task ValidateTestEnv_SameOperator_Returns409()
    {
        var svc = Substitute.For<IChangeRequestService>();
        svc.ValidateTestEnvAsync(
                "SQID-1",
                Arg.Any<ChangeRequestTestValidationInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ChangeRequestDto>.Failure(
                ErrorCodes.ChangeRequestSameOperator, "tester must differ.")));
        var controller = new ChangeRequestsController(svc);

        var result = await controller.ValidateTestEnvAsync(
            "SQID-1",
            new ChangeRequestTestValidationInputDto("Verified."),
            CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }
}
