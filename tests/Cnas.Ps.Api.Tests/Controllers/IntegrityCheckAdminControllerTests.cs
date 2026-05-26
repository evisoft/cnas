using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2282 / TOR SEC 036 — tests for <see cref="IntegrityCheckAdminController"/>.
/// Verifies the cnas-admin authorize gate, the manual-run happy path, the
/// per-run details route, and the acknowledgement endpoint.
/// </summary>
public sealed class IntegrityCheckAdminControllerTests
{
    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(IntegrityCheckAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task StartRun_HappyPath_Returns200()
    {
        var dto = new IntegrityCheckRunDto(
            Id: "SQID-1",
            RunStartedAt: DateTime.UtcNow,
            RunCompletedAt: DateTime.UtcNow,
            TriggerKind: "Manual",
            Status: "Completed",
            TotalRowsScanned: 5,
            TotalFindings: 0,
            FindingsBySeverity: new Dictionary<string, int>(),
            FailureReason: null);
        var svc = Substitute.For<IIntegrityCheckService>();
        svc.StartManualRunAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IntegrityCheckRunDto>.Success(dto)));

        var controller = new IntegrityCheckAdminController(svc);

        var result = await controller.StartRunAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task GetRunDetails_HappyPath_Returns200()
    {
        var runDto = new IntegrityCheckRunDto(
            Id: "SQID-2",
            RunStartedAt: DateTime.UtcNow,
            RunCompletedAt: DateTime.UtcNow,
            TriggerKind: "Scheduled",
            Status: "Completed",
            TotalRowsScanned: 10,
            TotalFindings: 0,
            FindingsBySeverity: new Dictionary<string, int>(),
            FailureReason: null);
        var details = new IntegrityCheckRunDetailsDto(runDto, Array.Empty<IntegrityCheckFindingDto>());
        var svc = Substitute.For<IIntegrityCheckService>();
        svc.GetRunDetailsAsync("SQID-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IntegrityCheckRunDetailsDto>.Success(details)));

        var controller = new IntegrityCheckAdminController(svc);
        var result = await controller.GetRunDetailsAsync("SQID-2");

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(details);
    }

    [Fact]
    public async Task GetRun_NotFound_Returns404()
    {
        var svc = Substitute.For<IIntegrityCheckService>();
        svc.GetRunByIdAsync("SQID-999", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IntegrityCheckRunDto>.Failure(ErrorCodes.NotFound, "missing")));

        var controller = new IntegrityCheckAdminController(svc);
        var result = await controller.GetRunAsync("SQID-999");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Acknowledge_HappyPath_Returns200()
    {
        var dto = new IntegrityCheckFindingDto(
            Id: "SQID-F1",
            RunSqid: "SQID-1",
            CheckCode: "TEST.CODE",
            Severity: "High",
            AggregateName: "Test",
            AggregateRowId: 42,
            Description: "x",
            ExpectedValue: null,
            ActualValue: null,
            FirstDetectedAt: DateTime.UtcNow,
            Acknowledged: true,
            AcknowledgedAt: DateTime.UtcNow,
            AcknowledgedByUserSqid: "SQID-U1",
            AcknowledgementNote: "Investigated the cause.");
        var svc = Substitute.For<IIntegrityCheckService>();
        svc.AcknowledgeFindingAsync("SQID-F1", Arg.Any<IntegrityFindingAcknowledgeInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IntegrityCheckFindingDto>.Success(dto)));

        var controller = new IntegrityCheckAdminController(svc);
        var result = await controller.AcknowledgeFindingAsync(
            "SQID-F1",
            new IntegrityFindingAcknowledgeInputDto("Investigated the cause."));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }
}
