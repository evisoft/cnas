using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Helpdesk;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — production implementation of
/// <see cref="ISupportTicketSlaEvaluator"/>. Sweeps non-terminal helpdesk
/// tickets every 5 minutes (cron driven by
/// <c>SupportTicketSlaEvaluationJob</c>), classifies each against its
/// computed SLA deadlines, and records newly-detected events idempotently.
/// </summary>
public sealed class SupportTicketSlaEvaluator : ISupportTicketSlaEvaluator
{
    /// <summary>Cached JSON serializer options shared across audit payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly ILogger<SupportTicketSlaEvaluator> _logger;

    /// <summary>Constructs the evaluator with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context (system actor when fired by Quartz).</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="logger">Structured logger.</param>
    public SupportTicketSlaEvaluator(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        ILogger<SupportTicketSlaEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<int>> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";

        // Pull non-terminal tickets in memory. The active row count is bounded
        // (terminal rows accumulate but are filtered out here), and the
        // categorisation below requires per-row state knowledge that the
        // InMemory provider used by tests cannot express as a single SQL.
        var nonTerminal = await _db.SupportTickets
            .Where(t => t.Status != SupportTicketStatus.Closed
                     && t.Status != SupportTicketStatus.Cancelled)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (nonTerminal.Count == 0)
        {
            return Result<int>.Success(0);
        }

        // Preload categories used by these tickets so we can tag metrics and
        // never round-trip per row.
        var categoryIds = nonTerminal.Select(t => t.CategoryId).Distinct().ToList();
        var categories = await _db.SupportTicketCategories
            .Where(c => categoryIds.Contains(c.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var categoryById = categories.ToDictionary(c => c.Id);

        // Preload existing SLA event kinds per ticket so dedupe is one query.
        var ticketIds = nonTerminal.Select(t => t.Id).ToList();
        var existingPairs = await _db.SupportTicketSlaEvents
            .Where(e => ticketIds.Contains(e.TicketId))
            .Select(e => new { e.TicketId, e.EventKind })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var existingSet = new HashSet<(long TicketId, SupportTicketSlaEventKind Kind)>(
            existingPairs.Select(p => (p.TicketId, p.EventKind)));

        var newlyInserted = 0;
        foreach (var ticket in nonTerminal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var categoryCode = categoryById.TryGetValue(ticket.CategoryId, out var c) ? c.Code : "UNKNOWN";

            // First-response classification.
            //   - Acknowledged (or further) before due → FirstResponseMet
            //   - Still Submitted past due            → FirstResponseBreached + auto-escalate
            if (ticket.FirstAcknowledgedAt is { } firstAck)
            {
                if (firstAck <= ticket.FirstResponseDueAt
                    && existingSet.Add((ticket.Id, SupportTicketSlaEventKind.FirstResponseMet)))
                {
                    InsertEvent(ticket.Id, SupportTicketSlaEventKind.FirstResponseMet, now, "First response within SLA.", actor);
                    newlyInserted++;
                }
            }
            else if (now > ticket.FirstResponseDueAt
                     && ticket.Status == SupportTicketStatus.Submitted
                     && existingSet.Add((ticket.Id, SupportTicketSlaEventKind.FirstResponseBreached)))
            {
                InsertEvent(ticket.Id, SupportTicketSlaEventKind.FirstResponseBreached, now, "Auto-detected by SLA evaluator.", actor);
                newlyInserted++;
                CnasMeter.SupportTicketSlaBreached.Add(
                    1,
                    new KeyValuePair<string, object?>("category_code", categoryCode),
                    new KeyValuePair<string, object?>("event_kind", SupportTicketSlaEventKind.FirstResponseBreached.ToString()));
                AutoEscalate(ticket, SupportTicketSlaEventKind.FirstResponseBreached, now, actor);
                if (existingSet.Add((ticket.Id, SupportTicketSlaEventKind.Escalated)))
                {
                    InsertEvent(ticket.Id, SupportTicketSlaEventKind.Escalated, now, "Auto-escalated due to FirstResponseBreached.", actor);
                    newlyInserted++;
                }
                CnasMeter.SupportTicketAutoEscalated.Add(
                    1,
                    new KeyValuePair<string, object?>("category_code", categoryCode));
                await EmitAuditAsync(
                    ISupportTicketSlaEvaluator.AuditSlaBreached,
                    AuditSeverity.Critical,
                    actor,
                    ticket.Id,
                    new
                    {
                        ticketSqid = _sqids.Encode(ticket.Id),
                        ticketNumber = ticket.TicketNumber,
                        categoryCode,
                        eventKind = SupportTicketSlaEventKind.FirstResponseBreached.ToString(),
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            // Resolution classification.
            //   - Resolved (or Closed) before due → ResolutionMet
            //   - Still non-resolved past due     → ResolutionBreached + auto-escalate
            var resolvedAt = ticket.ResolvedAt ?? ticket.ClosedAt;
            if (resolvedAt is { } resAt)
            {
                if (resAt <= ticket.ResolutionDueAt
                    && existingSet.Add((ticket.Id, SupportTicketSlaEventKind.ResolutionMet)))
                {
                    InsertEvent(ticket.Id, SupportTicketSlaEventKind.ResolutionMet, now, "Resolution within SLA.", actor);
                    newlyInserted++;
                }
            }
            else if (now > ticket.ResolutionDueAt
                     && ticket.Status != SupportTicketStatus.Resolved
                     && existingSet.Add((ticket.Id, SupportTicketSlaEventKind.ResolutionBreached)))
            {
                InsertEvent(ticket.Id, SupportTicketSlaEventKind.ResolutionBreached, now, "Auto-detected by SLA evaluator.", actor);
                newlyInserted++;
                CnasMeter.SupportTicketSlaBreached.Add(
                    1,
                    new KeyValuePair<string, object?>("category_code", categoryCode),
                    new KeyValuePair<string, object?>("event_kind", SupportTicketSlaEventKind.ResolutionBreached.ToString()));
                AutoEscalate(ticket, SupportTicketSlaEventKind.ResolutionBreached, now, actor);
                if (existingSet.Add((ticket.Id, SupportTicketSlaEventKind.Escalated)))
                {
                    InsertEvent(ticket.Id, SupportTicketSlaEventKind.Escalated, now, "Auto-escalated due to ResolutionBreached.", actor);
                    newlyInserted++;
                }
                CnasMeter.SupportTicketAutoEscalated.Add(
                    1,
                    new KeyValuePair<string, object?>("category_code", categoryCode));
                await EmitAuditAsync(
                    ISupportTicketSlaEvaluator.AuditSlaBreached,
                    AuditSeverity.Critical,
                    actor,
                    ticket.Id,
                    new
                    {
                        ticketSqid = _sqids.Encode(ticket.Id),
                        ticketNumber = ticket.TicketNumber,
                        categoryCode,
                        eventKind = SupportTicketSlaEventKind.ResolutionBreached.ToString(),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (newlyInserted > 0)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "SupportTicketSlaEvaluator inserted {Count} new SLA events.",
                newlyInserted);
        }

        return Result<int>.Success(newlyInserted);
    }

    /// <summary>Inserts a single SLA event row in memory.</summary>
    /// <param name="ticketId">Parent ticket id.</param>
    /// <param name="kind">SLA event kind.</param>
    /// <param name="now">UTC timestamp.</param>
    /// <param name="notes">PII-free annotation.</param>
    /// <param name="actor">Audit attribution.</param>
    private void InsertEvent(long ticketId, SupportTicketSlaEventKind kind, DateTime now, string? notes, string actor)
    {
        _db.SupportTicketSlaEvents.Add(new SupportTicketSlaEvent
        {
            TicketId = ticketId,
            EventKind = kind,
            DetectedAt = now,
            Notes = notes,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        });
    }

    /// <summary>
    /// Flips an open ticket to Escalated when it is not already escalated.
    /// Idempotent — a ticket already in <see cref="SupportTicketStatus.Escalated"/>
    /// stays untouched.
    /// </summary>
    /// <param name="ticket">Ticket to escalate.</param>
    /// <param name="kind">SLA event kind that triggered the escalation.</param>
    /// <param name="now">UTC timestamp.</param>
    /// <param name="actor">Audit attribution.</param>
    private void AutoEscalate(SupportTicket ticket, SupportTicketSlaEventKind kind, DateTime now, string actor)
    {
        if (ticket.Status == SupportTicketStatus.Escalated)
        {
            return;
        }
        ticket.Status = SupportTicketStatus.Escalated;
        ticket.EscalatedAt = now;
        ticket.EscalationReason = string.Create(CultureInfo.InvariantCulture, $"Auto-escalated due to {kind}");
        ticket.UpdatedAtUtc = now;
        ticket.UpdatedBy = actor;
    }

    /// <summary>Writes a single audit row with a serialised details payload.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row.</param>
    /// <param name="details">Arbitrary anonymous object serialised to JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when the audit row is enqueued.</returns>
    private async Task EmitAuditAsync(
        string eventCode,
        AuditSeverity severity,
        string actor,
        long targetEntityId,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode,
            severity,
            actor,
            nameof(SupportTicket),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }
}
