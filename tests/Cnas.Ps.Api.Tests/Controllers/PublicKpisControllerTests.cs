using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0500 — Controller-level tests for the
/// <see cref="PublicController.GetKpisAsync"/> action. The controller
/// delegates to <see cref="IPublicKpiService"/>; these tests assert the
/// 200 happy path, the snapshot payload shape, and that the action is
/// marked <c>[AllowAnonymous]</c> so anonymous callers reach it.
/// </summary>
public sealed class PublicKpisControllerTests
{
    private static IPublicContentService NewContentMock() => Substitute.For<IPublicContentService>();
    private static IInformationServices NewInfoMock() => Substitute.For<IInformationServices>();
    private static IPublicKpiService NewKpiMock() => Substitute.For<IPublicKpiService>();

    private static PublicController NewController(IPublicKpiService kpi) =>
        new(NewContentMock(), NewInfoMock(), kpi);

    /// <summary>
    /// R0500 — happy path: the service returns a snapshot and the controller
    /// surfaces it as <see cref="OkObjectResult"/> with the snapshot DTO.
    /// </summary>
    [Fact]
    public async Task GetKpis_ServiceReturnsSnapshot_Returns200WithDto()
    {
        var kpi = NewKpiMock();
        var snapshot = new PublicKpiSnapshotDto(
            ComputedAtUtc: new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc),
            TotalActiveContributors: 12345L,
            TotalActiveInsuredPersons: 67890L,
            TotalPendingApplications: 100L,
            DecisionsIssuedLast30Days: 250L,
            LastSuccessfulTreasuryImportAtUtc: new DateTime(2026, 5, 24, 8, 0, 0, DateTimeKind.Utc));
        kpi.GetCurrentAsync(Arg.Any<CancellationToken>())
           .Returns(Result<PublicKpiSnapshotDto>.Success(snapshot));
        var controller = NewController(kpi);

        var result = await controller.GetKpisAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<PublicKpiSnapshotDto>()
            .Which.TotalActiveContributors.Should().Be(12345L);
    }

    /// <summary>
    /// R0500 — Anonymous access: the GetKpisAsync action MUST carry
    /// <see cref="AllowAnonymousAttribute"/> so the future addition of a
    /// class-level <c>[Authorize]</c> cannot accidentally gate the public
    /// KPI surface.
    /// </summary>
    [Fact]
    public void GetKpis_Action_IsMarkedAllowAnonymous()
    {
        var method = typeof(PublicController).GetMethod(nameof(PublicController.GetKpisAsync))!;

        method.GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().NotBeNull("the public KPI endpoint must be anonymous-accessible (R0500).");
    }
}
