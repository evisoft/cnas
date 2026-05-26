using System;
using System.Linq;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — tests for
/// <see cref="Cnas.Ps.Infrastructure.Services.Helpdesk.SupportTicketSlaEvaluator"/>.
/// </summary>
public sealed class SupportTicketSlaEvaluatorTests
{
    private static async Task<SupportTicket> SeedTicketAsync(
        Cnas.Ps.Infrastructure.Persistence.CnasDbContext db,
        DateTime submittedAt,
        SupportTicketStatus status = SupportTicketStatus.Submitted,
        int firstResponseMinutes = 60,
        int resolutionMinutes = 480,
        DateTime? firstAcknowledgedAt = null,
        DateTime? resolvedAt = null)
    {
        var category = await db.SupportTicketCategories.FirstOrDefaultAsync()
            ?? await HelpdeskTestHelpers.SeedCategoryAsync(db, code: "AUTH", firstResponseMinutes: firstResponseMinutes, resolutionMinutes: resolutionMinutes);
        var ticket = new SupportTicket
        {
            TicketNumber = $"TKT-2026-{Guid.NewGuid().ToString("N")[..6]}",
            CategoryId = category.Id,
            Title = "test",
            Description = "test body",
            Severity = SupportTicketSeverity.Normal,
            Status = status,
            SubmittedByUserId = 1,
            SubmittedAt = submittedAt,
            FirstAcknowledgedAt = firstAcknowledgedAt,
            ResolvedAt = resolvedAt,
            FirstResponseDueAt = submittedAt.AddMinutes(firstResponseMinutes),
            ResolutionDueAt = submittedAt.AddMinutes(resolutionMinutes),
            CreatedAtUtc = submittedAt,
            CreatedBy = "SQID-1",
            IsActive = true,
        };
        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return ticket;
    }

    [Fact]
    public async Task PastFirstResponse_Submitted_Triggers_Breach_And_AutoEscalate()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        // Submitted 2 hours ago; first-response SLA is 60 min — breach.
        var now = HelpdeskTestHelpers.ClockNow;
        await SeedTicketAsync(db, submittedAt: now.AddHours(-2));
        var evaluator = HelpdeskTestHelpers.NewEvaluator(db, audit, new HelpdeskTestHelpers.StubClock(now));

        var result = await evaluator.EvaluateAsync(CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeGreaterThanOrEqualTo(1);

        var ticket = await db.SupportTickets.FirstAsync();
        ticket.Status.Should().Be(SupportTicketStatus.Escalated);
        ticket.EscalatedAt.Should().NotBeNull();
        ticket.EscalationReason.Should().Contain("Auto-escalated");

        var events = await db.SupportTicketSlaEvents
            .Where(e => e.TicketId == ticket.Id)
            .ToListAsync();
        events.Should().Contain(e => e.EventKind == SupportTicketSlaEventKind.FirstResponseBreached);
    }

    [Fact]
    public async Task Already_Escalated_Ticket_Not_Escalated_Twice()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        var now = HelpdeskTestHelpers.ClockNow;
        var ticket = await SeedTicketAsync(db, submittedAt: now.AddHours(-2));
        var evaluator = HelpdeskTestHelpers.NewEvaluator(db, audit, new HelpdeskTestHelpers.StubClock(now));

        // First pass — records FirstResponseBreached + Escalated.
        await evaluator.EvaluateAsync(CancellationToken.None);
        // Second pass — must not insert new rows for the same (ticket, event-kind) pairs.
        var second = await evaluator.EvaluateAsync(CancellationToken.None);
        second.IsSuccess.Should().BeTrue();
        second.Value.Should().Be(0);

        var firstResponseBreaches = await db.SupportTicketSlaEvents
            .CountAsync(e => e.TicketId == ticket.Id
                && e.EventKind == SupportTicketSlaEventKind.FirstResponseBreached);
        firstResponseBreaches.Should().Be(1);
    }

    [Fact]
    public async Task Met_Targets_Recorded_For_Acknowledged_Within_Sla()
    {
        using var db = HelpdeskTestHelpers.CreateContext();
        var audit = HelpdeskTestHelpers.NewAuditCapturing(out _);
        var now = HelpdeskTestHelpers.ClockNow;
        // Submitted 5 minutes ago, Acknowledged 1 minute ago — well within the 60-min SLA.
        await SeedTicketAsync(
            db,
            submittedAt: now.AddMinutes(-5),
            status: SupportTicketStatus.Acknowledged,
            firstAcknowledgedAt: now.AddMinutes(-1));
        var evaluator = HelpdeskTestHelpers.NewEvaluator(db, audit, new HelpdeskTestHelpers.StubClock(now));

        var result = await evaluator.EvaluateAsync(CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        var ticket = await db.SupportTickets.FirstAsync();
        var events = await db.SupportTicketSlaEvents.Where(e => e.TicketId == ticket.Id).ToListAsync();
        events.Should().Contain(e => e.EventKind == SupportTicketSlaEventKind.FirstResponseMet);
    }
}
