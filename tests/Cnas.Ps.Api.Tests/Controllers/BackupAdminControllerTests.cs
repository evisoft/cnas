using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2307 / TOR SEC 060 — tests for <see cref="BackupAdminController"/>.
/// </summary>
public sealed class BackupAdminControllerTests
{
    private static BackupRunDto NewRunDto() => new(
        Id: "SQID-RUN-1",
        PolicySqid: "SQID-1",
        RunNumber: "BKR-2026-000001",
        Status: BackupRunStatus.Succeeded.ToString(),
        TriggerKind: BackupTriggerKind.Manual.ToString(),
        StartedAt: new DateTime(2026, 5, 23, 4, 0, 0, DateTimeKind.Utc),
        CompletedAt: new DateTime(2026, 5, 23, 4, 0, 1, DateTimeKind.Utc),
        DurationMs: 1000L,
        PayloadSizeBytes: 100L,
        PayloadHashSha256: new string('a', 64),
        FailureReason: null,
        RetentionPurgedAt: null);

    [Fact]
    public async Task TriggerManualRun_HappyPath_Returns200()
    {
        var dto = NewRunDto();
        var orchestrator = Substitute.For<IBackupOrchestrator>();
        orchestrator.RunPolicyAsync("SQID-1", BackupTriggerKind.Manual, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BackupRunDto>.Success(dto)));
        var controller = new BackupAdminController(orchestrator);

        var result = await controller.TriggerManualRunAsync("SQID-1", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task ListRuns_WithFilter_Returns200()
    {
        var page = new BackupRunPageDto(new[] { NewRunDto() }, Total: 1, Skip: 0, Take: 50);
        var orchestrator = Substitute.For<IBackupOrchestrator>();
        orchestrator.ListRunsAsync(Arg.Any<BackupRunFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BackupRunPageDto>.Success(page)));
        var controller = new BackupAdminController(orchestrator);

        var result = await controller.ListRunsAsync(
            policySqid: "SQID-1",
            status: BackupRunStatus.Succeeded.ToString(),
            triggerKind: null,
            skip: 0,
            take: 50,
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task SweepExpired_HappyPath_Returns200_WithCount()
    {
        var orchestrator = Substitute.For<IBackupOrchestrator>();
        orchestrator.SweepExpiredRunsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<int>.Success(5)));
        var controller = new BackupAdminController(orchestrator);

        var result = await controller.SweepExpiredAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<BackupSweepResponse>().Subject;
        body.PurgedCount.Should().Be(5);
    }
}
