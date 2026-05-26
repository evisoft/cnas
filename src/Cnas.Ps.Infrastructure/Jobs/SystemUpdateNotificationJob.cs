using System;
using System.Linq;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R2504 / TOR PIR 024 — Quartz job that runs daily at 09:00 UTC and ensures
/// CNAS is notified at least <c>NoticeLeadTimeDays</c> ahead of any planned
/// <see cref="SystemUpdateEvent"/>. Honours the peak-hour gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Notification cadence.</b> For each <see cref="SystemUpdateEventStatus.Planned"/>
/// event whose <see cref="SystemUpdateEvent.PlannedDeploymentUtc"/> minus the
/// current instant is at most the parent schedule's
/// <see cref="SystemUpdateSchedule.NoticeLeadTimeDays"/>, the job invokes
/// <see cref="ISystemUpdateEventService.NotifyAsync"/> to flip the row to
/// <see cref="SystemUpdateEventStatus.Notified"/> and record the audit row.
/// </para>
/// <para>
/// <b>Concurrency guard.</b>
/// <see cref="DisallowConcurrentExecutionAttribute"/> keeps two fires from
/// racing the same set of rows. The per-event idempotency comes from the
/// state-machine: only Planned events match the filter.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SystemUpdateNotificationJob : IJob
{
    /// <summary>Stable Quartz job identity.</summary>
    public const string JobIdentity = "system-update-notification";

    /// <summary>Stable Quartz trigger identity.</summary>
    public const string TriggerIdentity = "system-update-notification-trigger";

    /// <summary>Cron expression — daily at 09:00 UTC.</summary>
    public const string Cron = "0 0 9 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate.</summary>
    public const string JobCode = JobScheduleProfileRegistry.SystemUpdateNotification;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<SystemUpdateNotificationJob> _logger;

    /// <summary>Constructs the job.</summary>
    /// <param name="scopes">DI scope factory.</param>
    /// <param name="peakHourGate">Peak-hour gate.</param>
    /// <param name="logger">Structured logger.</param>
    public SystemUpdateNotificationJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<SystemUpdateNotificationJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _peakHourGate = peakHourGate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;

        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<ICnasTimeProvider>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var service = scope.ServiceProvider.GetRequiredService<ISystemUpdateEventService>();

        var nowUtc = clock.UtcNow;

        // Pull every Planned event together with its parent schedule (need lead-time).
        var candidates = await db.SystemUpdateEvents
            .Where(e => e.Status == SystemUpdateEventStatus.Planned)
            .Join(
                db.SystemUpdateSchedules,
                e => e.ScheduleId,
                s => s.Id,
                (e, s) => new { Event = e, Schedule = s })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var daysUntil = (row.Event.PlannedDeploymentUtc - nowUtc).TotalDays;
            // If the lead-time deadline is approaching (≤ NoticeLeadTimeDays
            // away) or has already passed, dispatch the notice.
            if (daysUntil > row.Schedule.NoticeLeadTimeDays)
            {
                continue;
            }

            var eventSqid = sqids.Encode(row.Event.Id);
            var result = await service.NotifyAsync(eventSqid, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "SystemUpdateNotificationJob dispatched notice for event {EventNumber} (schedule={ScheduleCode}).",
                    row.Event.EventNumber, row.Schedule.ScheduleCode);
            }
            else
            {
                _logger.LogWarning(
                    "SystemUpdateNotificationJob refused event {EventNumber}: {ErrorCode} {ErrorMessage}.",
                    row.Event.EventNumber, result.ErrorCode, result.ErrorMessage);
            }
        }
    }
}
