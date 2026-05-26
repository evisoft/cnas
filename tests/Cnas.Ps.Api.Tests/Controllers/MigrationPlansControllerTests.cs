using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2430 / TOR M4 — tests for <see cref="MigrationPlansController"/>.
/// </summary>
public sealed class MigrationPlansControllerTests
{
    private static MigrationPlanDto NewDto(string id = "SQID-1")
        => new(
            Id: id,
            PlanCode: "LEGACY_PENSIONS_2026",
            Title: "Plan",
            Description: null,
            SourceKind: "InMemoryTest",
            TargetEntityName: "Pension",
            MappingDescriptorJson: null,
            BatchSize: 1000,
            Status: "Draft",
            RegisteredByUserSqid: "USR-1",
            ApprovedByUserSqid: null,
            ApprovedAt: null,
            CreatedAtUtc: new DateTime(2026, 5, 23, 4, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Create_HappyPath_Returns200()
    {
        var svc = Substitute.For<IMigrationPlanService>();
        svc.CreateAsync(Arg.Any<MigrationPlanCreateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MigrationPlanDto>.Success(NewDto())));
        var controller = new MigrationPlansController(svc);

        var result = await controller.CreateAsync(
            new MigrationPlanCreateInputDto(
                PlanCode: "LEGACY_PENSIONS_2026",
                Title: "Plan",
                Description: null,
                SourceKind: "InMemoryTest",
                TargetEntityName: "Pension",
                MappingDescriptorJson: null,
                BatchSize: 1000),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<MigrationPlanDto>();
    }

    [Fact]
    public async Task Get_HappyPath_Returns200()
    {
        var dto = NewDto();
        var svc = Substitute.For<IMigrationPlanService>();
        svc.GetByIdAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MigrationPlanDto>.Success(dto)));
        var controller = new MigrationPlansController(svc);

        var result = await controller.GetByIdAsync("SQID-1", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }
}
