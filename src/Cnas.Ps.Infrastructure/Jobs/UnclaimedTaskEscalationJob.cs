using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// Quartz job that enforces the unclaimed-task escalation SLA on
/// <see cref="WorkflowTask"/> rows sitting in a group inbox without being claimed past
/// the configured <see cref="UnclaimedTaskEscalationOptions.TimeoutWindow"/>
/// (R0202 / CF 20.05).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle rule.</b> When a workflow task is created with
/// <c>GroupCode != null &amp;&amp; AssignedUserId == null</c>, the writer stamps
/// <see cref="WorkflowTask.UnclaimedSinceUtc"/> with the current UTC instant. The
/// stamp is cleared on claim and re-set if the row is released back to the group.
/// This job sweeps every hour, finds rows whose stamp has elapsed past the configured
/// window, writes an audit row with the stable code <c>WORKFLOW_TASK.ESCALATED</c>,
/// and increments <see cref="CnasMeter.WorkflowTaskEscalated"/>. The row itself is
/// NOT mutated (status stays <see cref="WorkflowTaskStatus.Pending"/>, no
/// auto-reassignment) — escalation is a SIGNAL for operators, not a state change.
/// </para>
/// <para>
/// <b>No auto-reassignment.</b> The TOR CF 20.05 spec invites either auto-reassignment
/// or supervisor notification. We pick neither today: auto-reassignment requires a
/// supervisor-candidate pool that is gated on R0056 (ABAC), and inventing an arbitrary
/// supervisor would mask the policy gap rather than expose it. The audit + counter
/// give operators the visibility they need to claim the task manually; the supervisor
/// notification channel is wired in via a follow-up batch once R0056 ships. See
/// <c>TODO[r0202-supervisor-notify]</c>.
/// </para>
/// <para>
/// <b>Idempotency.</b> After writing the audit + counter the job nulls
/// <see cref="WorkflowTask.UnclaimedSinceUtc"/> on every escalated row. The row
/// therefore drops out of the predicate, so a second fire on the same data is a
/// no-op. A row only re-enters the escalation window if a future writer puts it
/// back into the unclaimed pool (claim → release).
/// </para>
/// <para>
/// <b>Audit privacy.</b> The audit <c>detailsJson</c> payload carries a stable reason
/// code (<c>unclaimed_timeout</c>) and the group code (a team identifier — not PII).
/// It NEVER carries the citizen's IDNP, name, email, or any other PII. The R0185
/// redactor would scrub anything we missed, but the policy is "never put it there in
/// the first place" (CLAUDE.md §5.6).
/// </para>
/// <para>
/// <see cref="DisallowConcurrentExecutionAttribute"/> prevents a second fire from
/// racing the same rows; the underlying mutation (set
/// <see cref="WorkflowTask.UnclaimedSinceUtc"/> to <c>null</c>) is idempotent so even
/// an overlapping fire would only re-query an empty result set.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class UnclaimedTaskEscalationJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "unclaimed-task-escalation";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "unclaimed-task-escalation-trigger";

    /// <summary>Stable actor id stamped on every audit row written by this job.</summary>
    private const string SystemActor = "system:r0202-escalation";

    /// <summary>Stable event code emitted on each escalation.</summary>
    private const string EscalationEventCode = "WORKFLOW_TASK.ESCALATED";

    /// <summary>Stable reason code embedded in the audit <c>detailsJson</c> payload.</summary>
    private const string ReasonCode = "unclaimed_timeout";

    private readonly IServiceScopeFactory _scopes;
    private readonly ICnasTimeProvider _clock;
    private readonly UnclaimedTaskEscalationOptions _options;
    private readonly ILogger<UnclaimedTaskEscalationJob> _logger;

    /// <summary>Constructs the escalation job with its collaborators.</summary>
    /// <param name="scopes">Service-scope factory used to resolve scoped collaborators
    ///   (DbContext, IAuditService) inside the fire scope.</param>
    /// <param name="clock">UTC clock used to compute the deadline and stamp <c>UpdatedAtUtc</c>.</param>
    /// <param name="options">Bound configuration options (timeout window, batch cap).</param>
    /// <param name="logger">Structured logger.</param>
    public UnclaimedTaskEscalationJob(
        IServiceScopeFactory scopes,
        ICnasTimeProvider clock,
        IOptions<UnclaimedTaskEscalationOptions> options,
        ILogger<UnclaimedTaskEscalationJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var now = _clock.UtcNow;
        var deadline = now - _options.TimeoutWindow;
        var ct = context.CancellationToken;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        // Pull the offending rows into memory rather than emitting a bulk UPDATE —
        // the InMemory provider used by unit tests does not implement ExecuteUpdate,
        // and the per-run volume is bounded by MaxBatchSize.
        var stale = await db.WorkflowTasks
            .Where(t => t.IsActive
                && t.Status == WorkflowTaskStatus.Pending
                && t.AssignedUserId == null
                && t.UnclaimedSinceUtc != null
                && t.UnclaimedSinceUtc <= deadline)
            .OrderBy(t => t.UnclaimedSinceUtc)
            .Take(_options.MaxBatchSize)
            .ToListAsync(ct).ConfigureAwait(false);

        if (stale.Count == 0)
        {
            return;
        }

        // ── 1. Clear the idempotency anchor + bump UpdatedAtUtc atomically. We do NOT
        // alter Status here — escalation is a signal, not a state change. See class
        // remarks "No auto-reassignment" for the rationale.
        foreach (var task in stale)
        {
            task.UnclaimedSinceUtc = null;
            task.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ── 2. Audit per row (best-effort). The payload carries a stable reason code
        // and the group code (team identifier — not PII) so operators can chart
        // escalations by group without unbounded cardinality and without leaking PII.
        foreach (var task in stale)
        {
            var detailsJson = JsonSerializer.Serialize(new
            {
                reason = ReasonCode,
                groupCode = task.GroupCode,
            });

            await audit.RecordAsync(
                eventCode: EscalationEventCode,
                severity: AuditSeverity.Notice,
                actorId: SystemActor,
                targetEntity: nameof(WorkflowTask),
                targetEntityId: task.Id,
                detailsJson: detailsJson,
                sourceIp: null,
                correlationId: context.FireInstanceId,
                cancellationToken: ct).ConfigureAwait(false);
        }

        // R0040 — one increment per escalated row so the operator dashboard charts the
        // rate and cumulative volume of SLA-driven escalations.
        CnasMeter.WorkflowTaskEscalated.Add(stale.Count);

        _logger.LogInformation(
            "UnclaimedTaskEscalationJob escalated {Count} tasks past the {Window} window.",
            stale.Count, _options.TimeoutWindow);
    }
}
