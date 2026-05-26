using Cnas.Ps.Application.Backups;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Infrastructure.Tests.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — tests for <see cref="Cnas.Ps.Infrastructure.Services.Backups.BackupPolicyService"/>.
/// </summary>
public sealed class BackupPolicyServiceTests
{
    private static BackupPolicyCreateInputDto NewCreate(string policyCode = "DB_FULL") => new(
        PolicyCode: policyCode,
        DisplayName: "Daily full DB",
        Description: null,
        Scope: "PrimaryDatabase",
        Strategy: "Full",
        CronSchedule: "0 0 2 * * ?",
        RetentionDays: 30,
        TargetKind: "InMemoryTest",
        TargetReference: "bucket/db");

    [Fact]
    public async Task Create_HappyPath_PersistsRow_AndAudits()
    {
        using var db = BackupTestHelpers.CreateContext();
        var audit = BackupTestHelpers.NewAuditCapturing(out var codes);
        var svc = BackupTestHelpers.NewPolicyService(db, audit);

        var result = await svc.CreateAsync(NewCreate(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.BackupPolicies.Should().HaveCount(1);
        codes.Should().Contain(IBackupPolicyService.AuditPolicyCreated);
    }

    [Fact]
    public async Task Modify_AfterArchive_Returns_Conflict()
    {
        using var db = BackupTestHelpers.CreateContext();
        var audit = BackupTestHelpers.NewAuditCapturing(out _);
        var svc = BackupTestHelpers.NewPolicyService(db, audit);

        var created = await svc.CreateAsync(NewCreate(), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();
        var sqid = created.Value.Id;

        var archive = await svc.ArchiveAsync(sqid, new BackupPolicyReasonInputDto("decommissioning"), CancellationToken.None);
        archive.IsSuccess.Should().BeTrue();

        var modify = await svc.ModifyAsync(
            sqid,
            new BackupPolicyModifyInputDto(DisplayName: "New", Description: null, CronSchedule: null, RetentionDays: null, TargetReference: null, ChangeReason: "test"),
            CancellationToken.None);

        modify.IsSuccess.Should().BeFalse();
        modify.ErrorCode.Should().Be(IBackupPolicyService.InvalidTransitionCode);
    }

    [Fact]
    public async Task Activate_Then_Deactivate_RoundTrips()
    {
        using var db = BackupTestHelpers.CreateContext();
        var audit = BackupTestHelpers.NewAuditCapturing(out _);
        var svc = BackupTestHelpers.NewPolicyService(db, audit);

        var created = await svc.CreateAsync(NewCreate(), CancellationToken.None);
        var sqid = created.Value.Id;
        created.Value.IsActive.Should().BeTrue();

        var deactivate = await svc.DeactivateAsync(sqid, CancellationToken.None);
        deactivate.IsSuccess.Should().BeTrue();
        deactivate.Value.IsActive.Should().BeFalse();

        var activate = await svc.ActivateAsync(sqid, CancellationToken.None);
        activate.IsSuccess.Should().BeTrue();
        activate.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task List_Filters_By_IsActive()
    {
        using var db = BackupTestHelpers.CreateContext();
        var audit = BackupTestHelpers.NewAuditCapturing(out _);
        var svc = BackupTestHelpers.NewPolicyService(db, audit);

        await BackupTestHelpers.SeedPolicyAsync(db, policyCode: "ACTIVE_ONE", isActive: true);
        await BackupTestHelpers.SeedPolicyAsync(db, policyCode: "INACTIVE_ONE", isActive: false);

        var activeOnly = await svc.ListAsync(new BackupPolicyFilterDto(IsActive: true), CancellationToken.None);
        activeOnly.IsSuccess.Should().BeTrue();
        activeOnly.Value.Items.Should().HaveCount(1);
        activeOnly.Value.Items[0].PolicyCode.Should().Be("ACTIVE_ONE");
    }
}
