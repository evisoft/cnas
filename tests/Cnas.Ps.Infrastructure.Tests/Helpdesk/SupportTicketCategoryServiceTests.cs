using Cnas.Ps.Application.Helpdesk;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Tests.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — tests for
/// <see cref="Cnas.Ps.Infrastructure.Services.Helpdesk.SupportTicketCategoryService"/>.
/// </summary>
public sealed class SupportTicketCategoryServiceTests
{
    private static SupportTicketCategoryCreateInputDto NewCreate(string code = "AUTH") => new(
        Code: code,
        DisplayName: "Auth issues",
        Description: null,
        DefaultSeverity: "Normal",
        FirstResponseSlaMinutes: 60,
        ResolutionSlaMinutes: 480,
        EscalationQueueCode: "L2_AUTH");

    [Fact]
    public async Task Create_HappyPath_PersistsRow_AndAudits()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out var codes);
        var svc = HelpdeskTestHelpers.NewCategoryService(db, audit);

        var result = await svc.CreateAsync(NewCreate(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.SupportTicketCategories.Should().HaveCount(1);
        codes.Should().Contain(ISupportTicketCategoryService.AuditCategoryCreated);
    }

    [Fact]
    public async Task Modify_AfterDeactivate_StillSucceeds()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        var svc = HelpdeskTestHelpers.NewCategoryService(db, audit);

        var created = await svc.CreateAsync(NewCreate(), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();
        var sqid = created.Value.Id;

        var deactivate = await svc.DeactivateAsync(sqid, CancellationToken.None);
        deactivate.IsSuccess.Should().BeTrue();
        deactivate.Value.IsActive.Should().BeFalse();

        var modify = await svc.ModifyAsync(
            sqid,
            new SupportTicketCategoryModifyInputDto(
                DisplayName: "Renamed",
                Description: null,
                DefaultSeverity: null,
                FirstResponseSlaMinutes: null,
                ResolutionSlaMinutes: null,
                EscalationQueueCode: null,
                ChangeReason: "rename"),
            CancellationToken.None);

        modify.IsSuccess.Should().BeTrue();
        modify.Value.DisplayName.Should().Be("Renamed");
    }

    [Fact]
    public async Task List_Filters_By_IsActive()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        var svc = HelpdeskTestHelpers.NewCategoryService(db, audit);

        await HelpdeskTestHelpers.SeedCategoryAsync(db, code: "ACTIVE_ONE", isActive: true);
        await HelpdeskTestHelpers.SeedCategoryAsync(db, code: "INACTIVE_ONE", isActive: false);

        var activeOnly = await svc.ListAsync(
            new SupportTicketCategoryFilterDto(IsActive: true),
            CancellationToken.None);
        activeOnly.IsSuccess.Should().BeTrue();
        activeOnly.Value.Items.Should().HaveCount(1);
        activeOnly.Value.Items[0].Code.Should().Be("ACTIVE_ONE");
    }
}
