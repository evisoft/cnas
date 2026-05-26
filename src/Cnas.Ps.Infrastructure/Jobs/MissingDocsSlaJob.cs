using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// Quartz job that enforces the 30-day missing-documents SLA on
/// <see cref="ServiceApplication"/> rows parked in
/// <see cref="ApplicationStatus.RejectedIncomplete"/> (R0934 / TOR §2.5.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle rule.</b> When CNAS staff or the decision engine flips an application
/// to <see cref="ApplicationStatus.RejectedIncomplete"/>, the citizen has 30 calendar
/// days to submit the missing supporting documents. After 30 days without action the
/// application is auto-closed: the row flips to <see cref="ApplicationStatus.Rejected"/>,
/// an audit entry with the stable code <c>APPLICATION.AUTO_CLOSED</c> is appended via
/// <see cref="IAuditService"/>, and a Romanian-language in-app notification is queued
/// to the solicitant via <see cref="INotificationService"/>.
/// </para>
/// <para>
/// <b>Filter.</b> The query is sargable thanks to the
/// <c>ServiceApplications.IX_RejectedIncompleteSinceUtc</c> index. Rows whose
/// <see cref="ServiceApplication.RejectedIncompleteSinceUtc"/> is <c>null</c> are
/// ignored — they were either never parked or already cleared by a status transition
/// out of <see cref="ApplicationStatus.RejectedIncomplete"/>.
/// </para>
/// <para>
/// <b>Idempotency.</b> The flip to <see cref="ApplicationStatus.Rejected"/> ALSO clears
/// <see cref="ServiceApplication.RejectedIncompleteSinceUtc"/>, so the predicate
/// excludes the row on subsequent runs — a beneficiary cannot be auto-closed twice for
/// the same dossier.
/// </para>
/// <para>
/// <b>Audit privacy.</b> The audit <c>detailsJson</c> payload carries a stable reason
/// code (<c>missing_docs_timeout</c>) only — never the citizen's IDNP, name, or any
/// other PII. The R0185 redactor would scrub anything we missed, but the policy is
/// "never put it there in the first place" (CLAUDE.md §5.6).
/// </para>
/// <para>
/// <b>Notification text.</b> The body references the application's reference number
/// (a stable Sqid-derived string) — never the citizen's IDNP / name. Citizens see the
/// notification in their in-app inbox, the system-of-record per UC22; the MNotify
/// mirror is best-effort and out of scope for this job.
/// </para>
/// <para>
/// <see cref="DisallowConcurrentExecutionAttribute"/> prevents a second fire from
/// racing the same rows; the underlying flip is idempotent so even a missing guard
/// would only produce a redundant query, never corruption.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class MissingDocsSlaJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "missing-docs-sla";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "missing-docs-sla-trigger";

    /// <summary>
    /// Cron expression for the trigger — every hour, on the hour. The job runs cheaply
    /// against the indexed predicate so a per-hour cadence costs essentially nothing
    /// while keeping the maximum effective overshoot bounded at ~1 hour.
    /// </summary>
    public const string Cron = "0 0 0/1 * * ?";

    /// <summary>The 30-day window mandated by TOR §2.5.1.</summary>
    public static readonly TimeSpan TimeoutWindow = TimeSpan.FromDays(30);

    /// <summary>Stable actor id stamped on every audit row written by this job.</summary>
    private const string SystemActor = "system:missing-docs-sla";

    /// <summary>Stable reason code embedded in the audit <c>detailsJson</c> payload.</summary>
    private const string ReasonCode = "missing_docs_timeout";

    /// <summary>Romanian-language notification subject (citizen-facing default per UC22).</summary>
    private const string NotificationSubject = "Cererea a fost respinsă";

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly INotificationService _notifications;
    private readonly ILogger<MissingDocsSlaJob> _logger;

    /// <summary>Constructs the SLA job with its scoped collaborators.</summary>
    /// <param name="db">Application DbContext; the job reads + writes the Applications set.</param>
    /// <param name="clock">UTC clock used to compute the deadline and stamp <c>UpdatedAtUtc</c>.</param>
    /// <param name="audit">Audit facade that records the auto-close event.</param>
    /// <param name="notifications">In-app inbox + MNotify dispatcher (best-effort outbound).</param>
    /// <param name="logger">Structured logger.</param>
    public MissingDocsSlaJob(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        IAuditService audit,
        INotificationService notifications,
        ILogger<MissingDocsSlaJob> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _clock = clock;
        _audit = audit;
        _notifications = notifications;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var now = _clock.UtcNow;
        var deadline = now - TimeoutWindow;
        var ct = context.CancellationToken;

        // Pull the offending rows into memory rather than emitting a bulk UPDATE —
        // the InMemory provider used by unit tests does not implement ExecuteUpdate,
        // and the per-run volume is bounded by the 30-day arrival rate (small).
        var stale = await _db.Applications
            .Where(a => a.IsActive
                && a.Status == ApplicationStatus.RejectedIncomplete
                && a.RejectedIncompleteSinceUtc != null
                && a.RejectedIncompleteSinceUtc <= deadline)
            .ToListAsync(ct).ConfigureAwait(false);

        if (stale.Count == 0)
        {
            return;
        }

        // ── 1. Flip statuses atomically: one SaveChanges for the entire batch ──
        // The job is the single authorised exit from the timed-out branch so we
        // clear RejectedIncompleteSinceUtc inline rather than going through the
        // ServiceApplication.TransitionStatus helper (which would re-stamp the
        // field if the next status were RejectedIncomplete).
        foreach (var app in stale)
        {
            app.Status = ApplicationStatus.Rejected;
            app.RejectedIncompleteSinceUtc = null;
            app.ClosedAtUtc = now;
            app.UpdatedAtUtc = now;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ── 2. Audit + notify per row (best-effort) ──
        // Audit details carry a stable reason code only — never the citizen's IDNP,
        // name, or any other PII. R0185 would scrub anything we missed, but the
        // policy is "never put it there in the first place" (CLAUDE.md §5.6).
        var detailsJson = JsonSerializer.Serialize(new { reason = ReasonCode });

        foreach (var app in stale)
        {
            await _audit.RecordAsync(
                eventCode: "APPLICATION.AUTO_CLOSED",
                severity: AuditSeverity.Information,
                actorId: SystemActor,
                targetEntity: nameof(ServiceApplication),
                targetEntityId: app.Id,
                detailsJson: detailsJson,
                sourceIp: null,
                correlationId: context.FireInstanceId,
                cancellationToken: ct).ConfigureAwait(false);

            // The reference number is a stable Sqid-derived public identifier; safe to
            // embed in the notification body. We never inline IDNP / name. Falls back
            // to a placeholder when the application predates ReferenceNumber population
            // (defensive — every modern submission stamps the field).
            var referenceNumber = app.ReferenceNumber ?? $"#{app.Id}";
            var body = $"Cererea Dvs. nr. {referenceNumber} a fost închisă automat pentru documente lipsă.";

            await _notifications.EnqueueAsync(
                recipientUserId: app.SolicitantId,
                subject: NotificationSubject,
                body: body,
                correlationId: context.FireInstanceId,
                cancellationToken: ct).ConfigureAwait(false);
        }

        // R0040 — one increment per row auto-closed so the operator dashboard charts
        // both the rate (derivative) and the cumulative volume of SLA-driven closures.
        CnasMeter.ApplicationAutoClosed.Add(stale.Count);

        _logger.LogInformation(
            "MissingDocsSlaJob auto-closed {Count} applications past the 30-day window.",
            stale.Count);
    }
}
