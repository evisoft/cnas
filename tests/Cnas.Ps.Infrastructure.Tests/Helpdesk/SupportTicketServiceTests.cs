using Cnas.Ps.Application.Helpdesk;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — tests for
/// <see cref="Cnas.Ps.Infrastructure.Services.Helpdesk.SupportTicketService"/>.
/// </summary>
public sealed class SupportTicketServiceTests
{
    [Fact]
    public async Task Submit_Happy_Computes_DueDates_AndAssigns_TicketNumber()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        await HelpdeskTestHelpers.SeedCategoryAsync(db, code: "AUTH", firstResponseMinutes: 60, resolutionMinutes: 480);
        var svc = HelpdeskTestHelpers.NewTicketService(db, audit);

        var result = await svc.SubmitAsync(
            new SupportTicketSubmitInputDto("AUTH", "Cannot login", "Account locked after 3 attempts."),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var ticket = await db.SupportTickets.FirstAsync();
        ticket.TicketNumber.Should().StartWith("TKT-2026-");
        ticket.FirstResponseDueAt.Should().Be(HelpdeskTestHelpers.ClockNow.AddMinutes(60));
        ticket.ResolutionDueAt.Should().Be(HelpdeskTestHelpers.ClockNow.AddMinutes(480));
    }

    [Fact]
    public async Task Acknowledge_Sets_FirstAcknowledgedAt()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        await HelpdeskTestHelpers.SeedCategoryAsync(db);
        var svc = HelpdeskTestHelpers.NewTicketService(db, audit);

        var submitted = await svc.SubmitAsync(
            new SupportTicketSubmitInputDto("AUTH", "Cannot login", "Account locked."),
            CancellationToken.None);
        var sqid = submitted.Value.Id;

        var ack = await svc.AcknowledgeAsync(sqid, CancellationToken.None);
        ack.IsSuccess.Should().BeTrue();
        ack.Value.FirstAcknowledgedAt.Should().NotBeNull();
        ack.Value.Status.Should().Be(nameof(SupportTicketStatus.Acknowledged));
    }

    [Fact]
    public async Task StartProgress_Requires_Acknowledged_State()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        await HelpdeskTestHelpers.SeedCategoryAsync(db);
        var svc = HelpdeskTestHelpers.NewTicketService(db, audit);

        var submitted = await svc.SubmitAsync(
            new SupportTicketSubmitInputDto("AUTH", "Cannot login", "Account locked."),
            CancellationToken.None);

        var result = await svc.StartProgressAsync(submitted.Value.Id, CancellationToken.None);
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ISupportTicketService.InvalidTransitionCode);
    }

    [Fact]
    public async Task Resolve_Requires_Summary_And_Valid_State()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        await HelpdeskTestHelpers.SeedCategoryAsync(db);
        var svc = HelpdeskTestHelpers.NewTicketService(db, audit);

        var submitted = await svc.SubmitAsync(
            new SupportTicketSubmitInputDto("AUTH", "Cannot login", "Account locked."),
            CancellationToken.None);
        var sqid = submitted.Value.Id;
        await svc.AcknowledgeAsync(sqid, CancellationToken.None);
        await svc.StartProgressAsync(sqid, CancellationToken.None);

        // Empty summary -> validation failure.
        var bad = await svc.ResolveAsync(sqid, new SupportTicketResolutionInputDto(""), CancellationToken.None);
        bad.IsSuccess.Should().BeFalse();
        bad.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);

        // Valid summary -> success.
        var good = await svc.ResolveAsync(sqid, new SupportTicketResolutionInputDto("Unlocked account."), CancellationToken.None);
        good.IsSuccess.Should().BeTrue();
        good.Value.Status.Should().Be(nameof(SupportTicketStatus.Resolved));
    }

    [Fact]
    public async Task Reopen_Within_7Days_Succeeds_After_7Days_Conflict()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        await HelpdeskTestHelpers.SeedCategoryAsync(db);
        var clock = new HelpdeskTestHelpers.StubClock(HelpdeskTestHelpers.ClockNow);
        var svc = HelpdeskTestHelpers.NewTicketService(db, audit, clock);

        var submitted = await svc.SubmitAsync(
            new SupportTicketSubmitInputDto("AUTH", "Cannot login", "Account locked."),
            CancellationToken.None);
        var sqid = submitted.Value.Id;
        await svc.AcknowledgeAsync(sqid, CancellationToken.None);
        await svc.StartProgressAsync(sqid, CancellationToken.None);
        await svc.ResolveAsync(sqid, new SupportTicketResolutionInputDto("Done."), CancellationToken.None);

        // Within window — succeeds.
        var inWindow = await svc.ReopenAsync(sqid, CancellationToken.None);
        inWindow.IsSuccess.Should().BeTrue();
        inWindow.Value.Status.Should().Be(nameof(SupportTicketStatus.InProgress));

        // Resolve then push the clock 8 days into the future — reopen now fails.
        await svc.ResolveAsync(sqid, new SupportTicketResolutionInputDto("Done again."), CancellationToken.None);
        var laterClock = new HelpdeskTestHelpers.StubClock(HelpdeskTestHelpers.ClockNow.AddDays(8));
        var laterSvc = HelpdeskTestHelpers.NewTicketService(db, audit, laterClock);
        var beyond = await laterSvc.ReopenAsync(sqid, CancellationToken.None);
        beyond.IsSuccess.Should().BeFalse();
        beyond.ErrorCode.Should().Be(ISupportTicketService.ReopenWindowExpiredCode);
    }

    [Fact]
    public async Task Cancel_From_Resolved_Returns_Conflict()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        await HelpdeskTestHelpers.SeedCategoryAsync(db);
        var svc = HelpdeskTestHelpers.NewTicketService(db, audit);

        var submitted = await svc.SubmitAsync(
            new SupportTicketSubmitInputDto("AUTH", "Cannot login", "Account locked."),
            CancellationToken.None);
        var sqid = submitted.Value.Id;
        await svc.AcknowledgeAsync(sqid, CancellationToken.None);
        await svc.StartProgressAsync(sqid, CancellationToken.None);
        await svc.ResolveAsync(sqid, new SupportTicketResolutionInputDto("Done."), CancellationToken.None);
        await svc.CloseAsync(sqid, CancellationToken.None);

        var cancel = await svc.CancelAsync(sqid, new SupportTicketReasonInputDto("nope"), CancellationToken.None);
        cancel.IsSuccess.Should().BeFalse();
        cancel.ErrorCode.Should().Be(ISupportTicketService.InvalidTransitionCode);
    }

    [Fact]
    public async Task Comments_Listed_In_Chronological_Order()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        await HelpdeskTestHelpers.SeedCategoryAsync(db);
        var svc = HelpdeskTestHelpers.NewTicketService(db, audit);

        var submitted = await svc.SubmitAsync(
            new SupportTicketSubmitInputDto("AUTH", "Cannot login", "Account locked."),
            CancellationToken.None);
        var sqid = submitted.Value.Id;

        await svc.AddCommentAsync(sqid, new SupportTicketCommentInputDto("first", false), CancellationToken.None);
        await svc.AddCommentAsync(sqid, new SupportTicketCommentInputDto("second", false), CancellationToken.None);
        await svc.AddCommentAsync(sqid, new SupportTicketCommentInputDto("third", true), CancellationToken.None);

        var view = await svc.GetByIdAsync(sqid, CancellationToken.None);
        view.IsSuccess.Should().BeTrue();
        view.Value.Comments.Should().HaveCount(3);
        view.Value.Comments[0].Body.Should().Be("first");
        view.Value.Comments[1].Body.Should().Be("second");
        view.Value.Comments[2].Body.Should().Be("third");
    }
}
