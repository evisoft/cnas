using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Tests.ServiceManagement;

/// <summary>
/// R2503 / TOR PIR 022-023 — tests for
/// <see cref="Cnas.Ps.Infrastructure.Services.ServiceManagement.SystemUpdateScheduleService"/>.
/// </summary>
public sealed class SystemUpdateScheduleServiceTests
{
    private static SystemUpdateScheduleCreateInputDto NewCreate(
        string code = "MONTHLY_PATCH",
        string cadence = "Monthly",
        int leadTimeDays = 30) => new(
        ScheduleCode: code,
        Title: "Monthly patch cadence",
        Cadence: cadence,
        NoticeLeadTimeDays: leadTimeDays,
        Description: null);

    [Fact]
    public async Task Create_HappyPath_PersistsRow_AndAudits()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out var codes);
        var svc = ServiceManagementTestHelpers.NewScheduleService(db, audit);

        var result = await svc.CreateAsync(NewCreate(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.SystemUpdateSchedules.Should().HaveCount(1);
        codes.Should().Contain(ISystemUpdateScheduleService.AuditCreated);
        result.Value.Cadence.Should().Be("Monthly");
        result.Value.NoticeLeadTimeDays.Should().Be(30);
    }

    [Fact]
    public async Task Create_Duplicate_Returns_Conflict()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewScheduleService(db, audit);

        var first = await svc.CreateAsync(NewCreate(), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await svc.CreateAsync(NewCreate(), CancellationToken.None);
        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.UpdateScheduleDuplicateCode);
    }

    [Fact]
    public async Task Activate_Then_Deactivate_Roundtrips()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = ServiceManagementTestHelpers.NewScheduleService(db, audit);

        var created = await svc.CreateAsync(NewCreate(), CancellationToken.None);
        var sqid = created.Value.Id;

        var deactivated = await svc.DeactivateAsync(sqid, CancellationToken.None);
        deactivated.IsSuccess.Should().BeTrue();
        deactivated.Value.IsActive.Should().BeFalse();

        var reactivated = await svc.ActivateAsync(sqid, CancellationToken.None);
        reactivated.IsSuccess.Should().BeTrue();
        reactivated.Value.IsActive.Should().BeTrue();
    }
}
