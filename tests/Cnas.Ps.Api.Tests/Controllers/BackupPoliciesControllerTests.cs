using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2307 / TOR SEC 060 — tests for <see cref="BackupPoliciesController"/>.
/// </summary>
public sealed class BackupPoliciesControllerTests
{
    private static BackupPolicyDto NewDto(string id = "SQID-1") => new(
        Id: id,
        PolicyCode: "DB_FULL",
        DisplayName: "Daily full DB",
        Description: null,
        Scope: "PrimaryDatabase",
        Strategy: "Full",
        CronSchedule: "0 0 2 * * ?",
        RetentionDays: 30,
        TargetKind: "InMemoryTest",
        TargetReference: "bucket/db",
        IsActive: true,
        LastSuccessfulRunAt: null,
        LastFailedRunAt: null);

    [Fact]
    public async Task Create_HappyPath_Returns200()
    {
        var svc = Substitute.For<IBackupPolicyService>();
        svc.CreateAsync(Arg.Any<BackupPolicyCreateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BackupPolicyDto>.Success(NewDto())));
        var controller = new BackupPoliciesController(svc);

        var input = new BackupPolicyCreateInputDto(
            PolicyCode: "DB_FULL",
            DisplayName: "Daily full DB",
            Description: null,
            Scope: "PrimaryDatabase",
            Strategy: "Full",
            CronSchedule: "0 0 2 * * ?",
            RetentionDays: 30,
            TargetKind: "InMemoryTest",
            TargetReference: "bucket/db");

        var result = await controller.CreateAsync(input, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<BackupPolicyDto>();
    }

    [Fact]
    public async Task GetById_HappyPath_Returns200()
    {
        var dto = NewDto();
        var svc = Substitute.For<IBackupPolicyService>();
        svc.GetByIdAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BackupPolicyDto>.Success(dto)));
        var controller = new BackupPoliciesController(svc);

        var result = await controller.GetByIdAsync("SQID-1", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }
}
