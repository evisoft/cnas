using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Reporting;

/// <summary>
/// R2461 / Deliverable 7.1 — concrete <see cref="IMonthlySupportReportService"/>.
/// Aggregates monthly <c>SupportTicket</c> metrics + SLA breach rates against
/// the read-replica seam (<see cref="IReadOnlyCnasDbContext"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure-read.</b> Carries <see cref="LongRunningReportServiceAttribute"/> so
/// the architecture suite pins the read-replica-only routing (R1904 / ARH 025).
/// No audit emission, no DbContext writes.
/// </para>
/// <para>
/// <b>Window semantics.</b> Tickets are bucketed by <c>SubmittedAt</c> falling
/// in <c>[Month, Month + 1)</c> UTC. The first-response and resolution averages
/// are computed in minutes from <c>FirstAcknowledgedAt − SubmittedAt</c> and
/// <c>ResolvedAt − SubmittedAt</c> respectively; nulls are excluded from each
/// average so the figure reflects ONLY the tickets where the relevant
/// transition fired. Breach rates are <c>(breached / submitted)</c> with
/// 4-decimal precision, clamped to 0..1.
/// </para>
/// </remarks>
[LongRunningReportService]
public sealed class MonthlySupportReportService : IMonthlySupportReportService
{
    private readonly IReadOnlyCnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly IValidator<MonthlySupportReportInputDto> _validator;

    /// <summary>Constructs the service.</summary>
    /// <param name="db">Read-replica EF Core seam (R0026 / ARH 025).</param>
    /// <param name="clock">UTC time provider (CLAUDE.md RULE 4).</param>
    /// <param name="validator">FluentValidation guard for the input envelope.</param>
    /// <exception cref="ArgumentNullException">When any collaborator is null.</exception>
    public MonthlySupportReportService(
        IReadOnlyCnasDbContext db,
        ICnasTimeProvider clock,
        IValidator<MonthlySupportReportInputDto> validator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(validator);
        _db = db;
        _clock = clock;
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<Result<MonthlySupportReportDto>> ComputeAsync(
        MonthlySupportReportInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Validate the input envelope.
        var validation = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            var msg = string.Join("; ", validation.Errors.Select(e => e.ErrorMessage));
            return Result<MonthlySupportReportDto>.Failure(ErrorCodes.ValidationFailed, msg);
        }

        // 2. Compute the [start, end) UTC window from the requested month.
        var start = new DateTime(input.Month.Year, input.Month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);

        // 3. Pull the in-month ticket slice. Joining to the category gives us
        //    the stable Code we need for the category breakdown filter + buckets.
        var ticketRowsQuery =
            from t in _db.SupportTickets
            join c in _db.SupportTicketCategories on t.CategoryId equals c.Id
            where t.SubmittedAt >= start && t.SubmittedAt < end
            select new TicketRow(
                t.Id,
                c.Code,
                t.Severity,
                t.Status,
                t.SubmittedAt,
                t.FirstAcknowledgedAt,
                t.ResolvedAt,
                t.ClosedAt,
                t.EscalatedAt);

        // Apply optional CategoryCodes filter via in-memory list (EF Core
        // translates Contains over a List<string> just fine and the list size
        // is capped by the validator).
        if (input.CategoryCodes is { Count: > 0 } codes)
        {
            // Materialise to an explicit list so EF's Contains translation
            // works consistently on both providers (PostgreSQL + InMemory).
            var filterList = codes.ToList();
            ticketRowsQuery = ticketRowsQuery.Where(r => filterList.Contains(r.CategoryCode));
        }

        var ticketRows = await ticketRowsQuery.ToListAsync(cancellationToken).ConfigureAwait(false);

        // 4. Pull SLA-event rows for the same ticket set restricted to in-month
        //    detection. Doing this in a second query keeps the LINQ translatable
        //    on every EF provider; the ticket id list is bounded by the slice
        //    above and the InMemory provider materialises Contains() into RAM.
        var ticketIds = ticketRows.Select(r => r.Id).ToList();
        var slaEvents = ticketIds.Count == 0
            ? new List<SupportTicketSlaEvent>()
            : await _db.SupportTicketSlaEvents
                .Where(e => ticketIds.Contains(e.TicketId)
                    && e.DetectedAt >= start && e.DetectedAt < end)
                .Select(e => new SupportTicketSlaEvent
                {
                    TicketId = e.TicketId,
                    EventKind = e.EventKind,
                    DetectedAt = e.DetectedAt,
                })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        // 5. Compute aggregates over the materialised slice.
        var totalSubmitted = ticketRows.Count;
        var totalResolved = ticketRows.Count(r => r.ResolvedAt is not null && r.ResolvedAt >= start && r.ResolvedAt < end);
        var totalClosed = ticketRows.Count(r => r.ClosedAt is not null && r.ClosedAt >= start && r.ClosedAt < end);
        var totalEscalated = ticketRows.Count(r => r.EscalatedAt is not null && r.EscalatedAt >= start && r.EscalatedAt < end);
        var totalCancelled = ticketRows.Count(r => r.Status == SupportTicketStatus.Cancelled);

        var avgFirstResponseMinutes = ComputeAverageMinutes(
            ticketRows.Where(r => r.FirstAcknowledgedAt is not null),
            r => (r.FirstAcknowledgedAt!.Value - r.SubmittedAt).TotalMinutes);

        var avgResolutionMinutes = ComputeAverageMinutes(
            ticketRows.Where(r => r.ResolvedAt is not null),
            r => (r.ResolvedAt!.Value - r.SubmittedAt).TotalMinutes);

        var firstResponseBreaches = slaEvents.Count(e => e.EventKind == SupportTicketSlaEventKind.FirstResponseBreached);
        var resolutionBreaches = slaEvents.Count(e => e.EventKind == SupportTicketSlaEventKind.ResolutionBreached);
        var firstResponseBreachRate = ComputeBreachRate(firstResponseBreaches, totalSubmitted);
        var resolutionBreachRate = ComputeBreachRate(resolutionBreaches, totalSubmitted);

        // 6. Build severity + category breakdowns.
        var severityBreakdown = BuildSeverityBreakdown(ticketRows, start, end);
        var categoryBreakdown = BuildCategoryBreakdown(ticketRows, slaEvents, start, end);

        var dto = new MonthlySupportReportDto(
            Month: input.Month,
            GeneratedAtUtc: _clock.UtcNow,
            TotalSubmitted: totalSubmitted,
            TotalResolved: totalResolved,
            TotalClosed: totalClosed,
            TotalEscalated: totalEscalated,
            TotalCancelled: totalCancelled,
            AvgFirstResponseMinutes: avgFirstResponseMinutes,
            AvgResolutionMinutes: avgResolutionMinutes,
            FirstResponseBreachRate: firstResponseBreachRate,
            ResolutionBreachRate: resolutionBreachRate,
            SeverityBreakdown: severityBreakdown,
            CategoryBreakdown: categoryBreakdown);
        return Result<MonthlySupportReportDto>.Success(dto);
    }

    /// <summary>
    /// Computes the per-severity breakdown rows. The set of buckets equals
    /// the set of severities actually represented in the slice — empty
    /// severities are NOT padded with zero rows because consumers compute
    /// the missing-severity gap from the overall total.
    /// </summary>
    private static IReadOnlyList<MonthlySupportSeverityBreakdownRow> BuildSeverityBreakdown(
        IReadOnlyCollection<TicketRow> rows,
        DateTime start,
        DateTime end)
    {
        var groups = rows.GroupBy(r => r.Severity).OrderBy(g => (int)g.Key);
        var result = new List<MonthlySupportSeverityBreakdownRow>(capacity: 4);
        foreach (var group in groups)
        {
            var groupRows = group.ToList();
            var resolved = groupRows.Where(r => r.ResolvedAt is not null && r.ResolvedAt >= start && r.ResolvedAt < end).ToList();
            var avgResolution = ComputeAverageMinutes(
                resolved,
                r => (r.ResolvedAt!.Value - r.SubmittedAt).TotalMinutes);
            result.Add(new MonthlySupportSeverityBreakdownRow(
                Severity: group.Key.ToString(),
                TotalSubmitted: groupRows.Count,
                TotalResolved: resolved.Count,
                AvgResolutionMinutes: avgResolution));
        }
        return result;
    }

    /// <summary>
    /// Computes the per-category breakdown rows. Each category that appears in
    /// the slice produces one row containing submitted / resolved counts,
    /// average first-response + resolution times, and the two breach rates.
    /// </summary>
    private static IReadOnlyList<MonthlySupportCategoryBreakdownRow> BuildCategoryBreakdown(
        IReadOnlyCollection<TicketRow> rows,
        IReadOnlyCollection<SupportTicketSlaEvent> slaEvents,
        DateTime start,
        DateTime end)
    {
        // Pre-bucket the SLA events by ticket id for O(1) lookup inside the
        // per-category loop.
        var eventsByTicket = slaEvents
            .GroupBy(e => e.TicketId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var groups = rows.GroupBy(r => r.CategoryCode).OrderBy(g => g.Key, StringComparer.Ordinal);
        var result = new List<MonthlySupportCategoryBreakdownRow>(capacity: 8);
        foreach (var group in groups)
        {
            var groupRows = group.ToList();
            var ack = groupRows.Where(r => r.FirstAcknowledgedAt is not null).ToList();
            var resolved = groupRows
                .Where(r => r.ResolvedAt is not null && r.ResolvedAt >= start && r.ResolvedAt < end)
                .ToList();

            var avgFirstResponse = ComputeAverageMinutes(
                ack,
                r => (r.FirstAcknowledgedAt!.Value - r.SubmittedAt).TotalMinutes);
            var avgResolution = ComputeAverageMinutes(
                resolved,
                r => (r.ResolvedAt!.Value - r.SubmittedAt).TotalMinutes);

            var firstBreaches = 0;
            var resolutionBreaches = 0;
            foreach (var row in groupRows)
            {
                if (!eventsByTicket.TryGetValue(row.Id, out var ev))
                {
                    continue;
                }
                foreach (var e in ev)
                {
                    if (e.EventKind == SupportTicketSlaEventKind.FirstResponseBreached) firstBreaches++;
                    if (e.EventKind == SupportTicketSlaEventKind.ResolutionBreached) resolutionBreaches++;
                }
            }

            result.Add(new MonthlySupportCategoryBreakdownRow(
                CategoryCode: group.Key,
                TotalSubmitted: groupRows.Count,
                TotalResolved: resolved.Count,
                AvgFirstResponseMinutes: avgFirstResponse,
                AvgResolutionMinutes: avgResolution,
                FirstResponseBreachRate: ComputeBreachRate(firstBreaches, groupRows.Count),
                ResolutionBreachRate: ComputeBreachRate(resolutionBreaches, groupRows.Count)));
        }
        return result;
    }

    /// <summary>
    /// Average of the projection over the source collection, rounded to two
    /// decimals (matching the front-end display precision). Returns null when
    /// the source is empty so callers can render "—" instead of "0.00".
    /// </summary>
    /// <typeparam name="T">Source row type.</typeparam>
    /// <param name="source">Enumerable to project.</param>
    /// <param name="selector">Projection that yields a minute value.</param>
    /// <returns>Average rounded to 2 decimals, or null when the source is empty.</returns>
    private static decimal? ComputeAverageMinutes<T>(IEnumerable<T> source, Func<T, double> selector)
    {
        var sample = source.Select(selector).ToList();
        if (sample.Count == 0)
        {
            return null;
        }
        var avg = sample.Average();
        return Math.Round((decimal)avg, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Computes <c>numerator / denominator</c> as a decimal rounded to four
    /// decimals and clamped to <c>[0, 1]</c>. Returns 0 when the denominator
    /// is zero.
    /// </summary>
    /// <param name="numerator">Number of breaches.</param>
    /// <param name="denominator">Total tickets in scope.</param>
    /// <returns>Breach rate in <c>[0, 1]</c> with four-decimal precision.</returns>
    private static decimal ComputeBreachRate(int numerator, int denominator)
    {
        if (denominator <= 0) return 0m;
        var raw = (decimal)numerator / denominator;
        if (raw < 0m) raw = 0m;
        if (raw > 1m) raw = 1m;
        return Math.Round(raw, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Internal row shape projected from the join (ticket + category code).
    /// Keeps the in-memory pipeline tight and avoids leaking the full entity
    /// graph into the aggregate helpers.
    /// </summary>
    /// <param name="Id">Ticket row id (raw long).</param>
    /// <param name="CategoryCode">Stable category code.</param>
    /// <param name="Severity">Current ticket severity at snapshot time.</param>
    /// <param name="Status">Current ticket status at snapshot time.</param>
    /// <param name="SubmittedAt">UTC submission instant.</param>
    /// <param name="FirstAcknowledgedAt">UTC first-acknowledgement instant (or null).</param>
    /// <param name="ResolvedAt">UTC resolution instant (or null).</param>
    /// <param name="ClosedAt">UTC close instant (or null).</param>
    /// <param name="EscalatedAt">UTC escalation instant (or null).</param>
    private sealed record TicketRow(
        long Id,
        string CategoryCode,
        SupportTicketSeverity Severity,
        SupportTicketStatus Status,
        DateTime SubmittedAt,
        DateTime? FirstAcknowledgedAt,
        DateTime? ResolvedAt,
        DateTime? ClosedAt,
        DateTime? EscalatedAt);
}
