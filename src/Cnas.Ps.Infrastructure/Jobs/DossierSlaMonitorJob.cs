using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// SLA monitor that flips <see cref="WorkflowTask"/> rows whose <see cref="WorkflowTask.DueAtUtc"/>
/// has passed into <see cref="WorkflowTaskStatus.Overdue"/> and notifies the assignee.
/// </summary>
/// <remarks>
/// Per TOR §4.5 (PSR) and MR 003 (heart-beat clauses) overdue work must be surfaced to the
/// șef-direcție inbox before it becomes an incident. The job is registered every 15 minutes
/// (see <see cref="QuartzComposition"/>) and is safe to re-run: tasks already marked as
/// <see cref="WorkflowTaskStatus.Overdue"/> are skipped, so a beneficiary cannot be notified
/// twice for the same SLA breach. Time-source is injected via <see cref="ICnasTimeProvider"/>
/// to honour CLAUDE.md "UTC Everywhere".
/// </remarks>
[DisallowConcurrentExecution]
public sealed class DossierSlaMonitorJob(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    INotificationService notify,
    ILogger<DossierSlaMonitorJob> logger,
    INotificationTriggerDispatcher? triggers = null) : IJob
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly INotificationService _notify = notify;
    private readonly ILogger<DossierSlaMonitorJob> _logger = logger;

    /// <summary>
    /// R0174 / TOR CF 22.03 — optional canonical-trigger dispatcher. When wired
    /// the SLA notifications carry the <c>WorkflowTask</c> deep-link anchor so
    /// the inbox renders a clickable link (R0172). Nullable for back-compat with
    /// legacy DI scopes that have not yet wired the dispatcher.
    /// </summary>
    private readonly INotificationTriggerDispatcher? _triggers = triggers;

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Capture the clock instant ONCE so every row stamped in this run shares the same
        // UpdatedAtUtc — useful when correlating an SLA sweep in the audit trail.
        var now = _clock.UtcNow;
        var ct = context.CancellationToken;

        // Pull overdue tasks together with the parent dossier number so the notification body
        // can reference both the task title and the dossier reference without an N+1 query.
        var rows = await _db.WorkflowTasks
            .Where(t => t.IsActive
                        && t.DueAtUtc != null
                        && t.DueAtUtc < now
                        && (t.Status == WorkflowTaskStatus.Pending
                            || t.Status == WorkflowTaskStatus.InProgress))
            .Join(_db.Dossiers,
                  t => t.DossierId,
                  d => d.Id,
                  (t, d) => new { Task = t, d.DossierNumber })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return;
        }

        // Flip in-memory; one SaveChanges at the end keeps the operation atomic. Idempotency
        // relies on the Status filter above — re-running the job sees no overdue rows again.
        foreach (var row in rows)
        {
            row.Task.Status = WorkflowTaskStatus.Overdue;
            row.Task.UpdatedAtUtc = now;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "DossierSlaMonitorJob transitioned {Count} workflow tasks to Overdue at {NowUtc:o}.",
            rows.Count, now);

        // Best-effort notification — failures inside Enqueue do NOT roll back the status flip;
        // the row stays Overdue and a subsequent run will retry the notification only if it
        // was added back to Pending by an operator. Notifications must therefore never throw.
        foreach (var row in rows)
        {
            // Fallback group inbox when the task is unassigned (e.g. lives on a group queue).
            // The notification service interprets long.MinValue as "use the group code"; here we
            // simply skip the notify call when no concrete user is set, so the group inbox view
            // surfaces the SLA breach naturally on the next list refresh.
            if (row.Task.AssignedUserId is not long userId)
            {
                continue;
            }

            var body = $"Sarcina '{row.Task.Title}' pentru dosarul {row.DossierNumber} a depășit termenul de execuție.";
            if (_triggers is not null)
            {
                // R0174 / TOR CF 22.03 — emit the canonical SlaBreach trigger so the
                // inbox row carries the WorkflowTask deep-link anchor.
                await _triggers.DispatchAsync(
                    NotificationTriggerKind.SlaBreach,
                    new NotificationTriggerPayload(
                        RecipientUserId: userId,
                        Subject: "Sarcină depășită",
                        Body: body,
                        CorrelationId: null,
                        RelatedEntityType: NotificationRelatedEntityTypes.WorkflowTask,
                        RelatedEntityId: row.Task.Id),
                    ct).ConfigureAwait(false);
            }
            else
            {
                await _notify.EnqueueAsync(
                    userId,
                    "Sarcină depășită",
                    body,
                    correlationId: null,
                    cancellationToken: ct).ConfigureAwait(false);
            }
        }
    }
}
