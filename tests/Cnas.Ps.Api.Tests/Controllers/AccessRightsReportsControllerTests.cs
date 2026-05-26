using System.Text;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2274 / TOR SEC 028 — controller-level tests for
/// <see cref="AccessRightsReportsController"/>.
/// </summary>
public sealed class AccessRightsReportsControllerTests
{
    /// <summary>Stub clock returning a fixed instant.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>Sample by-user DTO returned by the service mock.</summary>
    private static AccessRightsByUserReportDto SampleByUser() => new(
        UserSqid: "USR-42",
        DisplayName: "Sample User",
        Email: "su@example.com",
        AccountStatus: nameof(UserAccountState.Active),
        DirectRoles: ["ROLE_A"],
        EffectiveRoles: [new AccessRightsEffectiveRoleDto("ROLE_A", nameof(AccessRightsGrantKind.Direct), [])],
        GroupMemberships: []);

    /// <summary>Sample by-role DTO returned by the service mock.</summary>
    private static AccessRightsByRoleReportDto SampleByRole() => new(
        RoleCode: "ROLE_A",
        Items: [new UserAccessRowDto(
            UserSqid: "USR-42",
            DisplayName: "Sample User",
            Email: "su@example.com",
            AccountStatus: nameof(UserAccountState.Active),
            GrantKind: nameof(AccessRightsGrantKind.Direct),
            GrantingGroups: [])],
        Total: 1,
        Skip: 0,
        Take: 100);

    /// <summary>R2274 — GET /api/access-rights-reports/by-user/{sqid} returns 200.</summary>
    [Fact]
    public async Task ByUser_ReturnsOkWithDto()
    {
        var svc = Substitute.For<IAccessRightsReportService>();
        svc.ReportByUserAsync("USR-42", Arg.Any<CancellationToken>())
            .Returns(Result<AccessRightsByUserReportDto>.Success(SampleByUser()));
        var controller = new AccessRightsReportsController(svc, new StubClock());

        var result = await controller.GetByUserAsync("USR-42", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        var dto = ok.Value.Should().BeOfType<AccessRightsByUserReportDto>().Subject;
        dto.UserSqid.Should().Be("USR-42");
    }

    /// <summary>R2274 — GET /api/access-rights-reports/by-role/{code} returns 200 with the paged result.</summary>
    [Fact]
    public async Task ByRole_ReturnsPagedResult()
    {
        var svc = Substitute.For<IAccessRightsReportService>();
        svc.ReportByRoleAsync(
                "ROLE_A",
                Arg.Any<AccessRightsReportPagingDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<AccessRightsByRoleReportDto>.Success(SampleByRole()));
        var controller = new AccessRightsReportsController(svc, new StubClock());

        var result = await controller.GetByRoleAsync(
            "ROLE_A",
            skip: 0,
            take: 100,
            includeDisabledAccounts: false,
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        var dto = ok.Value.Should().BeOfType<AccessRightsByRoleReportDto>().Subject;
        dto.Total.Should().Be(1);
    }

    /// <summary>R2274 — by-role CSV export returns text/csv with the expected header bytes.</summary>
    [Fact]
    public async Task ByRoleCsvExport_ReturnsCsvWithContentType_TextCsv()
    {
        var svc = Substitute.For<IAccessRightsReportService>();
        var bytes = Encoding.UTF8.GetBytes("UserSqid,DisplayName,Email,AccountStatus,DirectGrant,GrantingGroups\r\n");
        svc.ExportByRoleCsvAsync("ROLE_A", Arg.Any<CancellationToken>())
            .Returns(Result<byte[]>.Success(bytes));
        var controller = new AccessRightsReportsController(svc, new StubClock());

        var actionResult = await controller.ExportByRoleCsvAsync("ROLE_A", CancellationToken.None);

        var file = actionResult.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("text/csv");
        file.FileContents.Should().BeEquivalentTo(bytes);
        file.FileDownloadName.Should().Contain("ROLE_A");
        file.FileDownloadName.Should().Contain("20260523");
    }

    /// <summary>R2274 — full-matrix CSV export respects the Take limit before invoking the service.</summary>
    [Fact]
    public async Task FullMatrixCsvExport_RespectsTakeLimit()
    {
        var svc = Substitute.For<IAccessRightsReportService>();
        var bytes = Encoding.UTF8.GetBytes("UserSqid,DisplayName,Email,AccountStatus,RoleCode,GrantKind,GrantingChain\r\n");
        AccessRightsReportPagingDto? observed = null;
        svc.ExportFullAccessMatrixCsvAsync(Arg.Any<AccessRightsReportPagingDto>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observed = call.ArgAt<AccessRightsReportPagingDto>(0);
                return Task.FromResult(Result<byte[]>.Success(bytes));
            });
        var controller = new AccessRightsReportsController(svc, new StubClock());

        var actionResult = await controller.ExportFullMatrixCsvAsync(skip: 0, take: 250, CancellationToken.None);

        var file = actionResult.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("text/csv");
        observed.Should().NotBeNull();
        observed!.Take.Should().Be(250);
        observed.Skip.Should().Be(0);
    }
}
