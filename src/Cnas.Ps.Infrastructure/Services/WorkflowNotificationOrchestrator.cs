using System.Collections.Generic;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="IWorkflowNotificationOrchestrator"/> implementation backed by
/// <see cref="IReadOnlyCnasDbContext"/> for recipient resolution and
/// <see cref="INotificationService"/> for the actual channel dispatch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recipient resolution is best-effort.</b> A missing supervisor, an absent
/// applicant, or a custom group with zero members all degrade to a logged WARN +
/// skip — they never fail the dispatch. The orchestrator's contract is "deliver to
/// every recipient we COULD resolve"; the strategy author is expected to verify role
/// availability before configuring the rule.
/// </para>
/// <para>
/// <b>Quiet-hours scheduling.</b> The orchestrator computes the next end-of-window UTC
/// instant using the <c>Europe/Chisinau</c> timezone — this is the canonical
/// presentation-layer zone for SI PS. When the underlying <c>INotificationService</c>
/// does not natively support scheduled dispatch (the current legacy contract enqueues
/// immediately), the orchestrator stores the scheduling intent in the notification
/// body so a future scheduler can pick it up. Today the schedule is informational + a
/// best-effort heads-up to the recipient via subject prefix; the SLA + dispatch
/// retry pipeline catches up to the contract in a follow-up batch.
/// </para>
/// </remarks>
public sealed class WorkflowNotificationOrchestrator : IWorkflowNotificationOrchestrator
{
    /// <summary>IANA name of the local timezone used for quiet-hours scheduling.</summary>
    private const string ChisinauTimezoneId = "Europe/Chisinau";

    private readonly IWorkflowNotificationStrategyResolver _resolver;
    private readonly INotificationService _notify;
    private readonly IReadOnlyCnasDbContext _readDb;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly ILogger<WorkflowNotificationOrchestrator> _logger;

    /// <summary>Constructs the orchestrator with its DI dependencies.</summary>
    /// <param name="resolver">Singleton strategy resolver.</param>
    /// <param name="notify">Underlying notification dispatch service (in-app + MNotify).</param>
    /// <param name="readDb">Read-only DbContext for recipient + task lookups.</param>
    /// <param name="clock">Injected UTC clock (CLAUDE.md cross-cutting).</param>
    /// <param name="caller">Caller context for correlation id propagation.</param>
    /// <param name="logger">Structured logger for resolution diagnostics.</param>
    public WorkflowNotificationOrchestrator(
        IWorkflowNotificationStrategyResolver resolver,
        INotificationService notify,
        IReadOnlyCnasDbContext readDb,
        ICnasTimeProvider clock,
        ICallerContext caller,
        ILogger<WorkflowNotificationOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(readDb);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(logger);
        _resolver = resolver;
        _notify = notify;
        _readDb = readDb;
        _clock = clock;
        _caller = caller;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> DispatchAsync(
        long workflowDefinitionId,
        long workflowTaskId,
        string eventCode,
        IDictionary<string, string>? templateContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventCode);

        var strategy = _resolver.Resolve(workflowDefinitionId, eventCode);

        // ─── No strategy → legacy default ────────────────────────────────────────────
        // Backward compatibility: dispatch on Email + InApp to the assignee, exactly
        // what every pre-R0128 dispatch site was doing inline.
        if (strategy is null)
        {
            return await DispatchLegacyDefaultAsync(workflowTaskId, eventCode, templateContext, cancellationToken)
                .ConfigureAwait(false);
        }

        // ─── Explicit suppression ────────────────────────────────────────────────────
        // IsEnabled=false is an operator-configured "do not notify" override. Bump the
        // suppressed counter (tagged by event for charting) and return success.
        if (!strategy.IsEnabled)
        {
            CnasMeter.WorkflowNotifySuppressed.Add(1,
                new KeyValuePair<string, object?>("event", eventCode));
            _logger.LogDebug(
                "Workflow notification suppressed by strategy: workflow={WorkflowId} event={EventCode}",
                workflowDefinitionId,
                eventCode);
            return Result.Success();
        }

        // ─── Strategy-driven dispatch ────────────────────────────────────────────────
        // 1) Resolve every recipient role to zero or more user ids.
        // 2) Compute the dispatch instant (immediate vs deferred for quiet hours).
        // 3) Fan out one notification per recipient (the underlying service handles
        //    per-channel split via its own EnqueueAsync convention).

        var recipients = await ResolveRecipientsAsync(
            strategy.RecipientRoles, workflowTaskId, cancellationToken).ConfigureAwait(false);

        // Quiet-hours: when the current local instant is inside the window, defer.
        var (deferred, scheduleHint) = ComputeQuietHoursDecision(strategy.QuietHours);

        var (subject, body) = BuildMessage(eventCode, strategy.TemplateCodeOverride, templateContext, scheduleHint);

        foreach (var recipientId in recipients)
        {
            await _notify.EnqueueAsync(
                recipientId,
                subject,
                body,
                _caller.CorrelationId,
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug(
            "Workflow notification dispatched: workflow={WorkflowId} event={EventCode} recipients={Count} deferred={Deferred}",
            workflowDefinitionId,
            eventCode,
            recipients.Count,
            deferred);

        return Result.Success();
    }

    /// <summary>
    /// Legacy default dispatch path used when no strategy is configured. Mirrors the
    /// pre-R0128 behaviour — notify the assignee on the default channels (Email + InApp
    /// fan-out is handled inside <see cref="INotificationService.EnqueueAsync(long, string, string, string?, System.Threading.CancellationToken)"/>).
    /// </summary>
    /// <param name="workflowTaskId">Raw task id whose assignee is the recipient.</param>
    /// <param name="eventCode">Canonical event code (drives the subject template).</param>
    /// <param name="templateContext">Optional template variables; folded into the body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success on apply; the underlying service's failure result otherwise.</returns>
    private async Task<Result> DispatchLegacyDefaultAsync(
        long workflowTaskId,
        string eventCode,
        IDictionary<string, string>? templateContext,
        CancellationToken ct)
    {
        var assigneeId = await _readDb.WorkflowTasks
            .Where(t => t.Id == workflowTaskId && t.IsActive)
            .Select(t => t.AssignedUserId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (assigneeId is null)
        {
            _logger.LogDebug(
                "Workflow notification legacy default: task {TaskId} has no assignee; skipping.",
                workflowTaskId);
            return Result.Success();
        }

        var (subject, body) = BuildMessage(eventCode, templateCodeOverride: null, templateContext, scheduleHint: null);
        return await _notify.EnqueueAsync(
            assigneeId.Value, subject, body, _caller.CorrelationId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves each recipient role to zero or more <c>UserProfile.Id</c> values.
    /// Returns a distinct list — a single user mapped to multiple roles (e.g. the same
    /// person is the assignee AND the process owner) is notified only once.
    /// </summary>
    /// <param name="roles">Recipient role codes from the strategy.</param>
    /// <param name="workflowTaskId">Raw task id used to resolve task-relative roles.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Distinct user ids to notify.</returns>
    private async Task<IReadOnlyList<long>> ResolveRecipientsAsync(
        IReadOnlyList<string> roles,
        long workflowTaskId,
        CancellationToken ct)
    {
        var unique = new HashSet<long>();
        foreach (var role in roles)
        {
            await AppendRoleAsync(role, workflowTaskId, unique, ct).ConfigureAwait(false);
        }
        return unique.ToList();
    }

    /// <summary>
    /// Appends the user ids resolved for a single role to <paramref name="sink"/>.
    /// Unknown roles and resolution failures degrade to a logged WARN + skip.
    /// </summary>
    /// <param name="role">Single role code from the strategy.</param>
    /// <param name="workflowTaskId">Raw task id used to resolve task-relative roles.</param>
    /// <param name="sink">Distinct id set being built.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task AppendRoleAsync(string role, long workflowTaskId, HashSet<long> sink, CancellationToken ct)
    {
        if (role.StartsWith("CustomGroup:", StringComparison.Ordinal))
        {
            var groupCode = role.Substring("CustomGroup:".Length);
            var groupMembers = await _readDb.UserProfiles
                .Where(u => u.IsActive && u.Groups.Contains(groupCode))
                .Select(u => u.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var id in groupMembers)
            {
                sink.Add(id);
            }
            return;
        }

        switch (role)
        {
            case "Assignee":
                {
                    var assignee = await _readDb.WorkflowTasks
                        .Where(t => t.Id == workflowTaskId && t.IsActive)
                        .Select(t => t.AssignedUserId)
                        .SingleOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    if (assignee is not null)
                    {
                        sink.Add(assignee.Value);
                    }
                    return;
                }

            case "Applicant":
                {
                    // Walk WorkflowTask → Dossier → Application → SolicitantId. Applicants
                    // are modelled as Solicitants in this codebase; map via UserProfile by
                    // NationalIdHash equality when present, otherwise skip.
                    var solicitantId = await _readDb.WorkflowTasks
                        .Where(t => t.Id == workflowTaskId && t.IsActive)
                        .Join(_readDb.Dossiers, t => t.DossierId, d => d.Id, (t, d) => d.ApplicationId)
                        .Join(_readDb.Applications, appId => appId, a => a.Id, (appId, a) => (long?)a.SolicitantId)
                        .SingleOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    if (solicitantId is null)
                    {
                        return;
                    }

                    // Resolve the matching UserProfile via the Solicitant's NationalIdHash.
                    var nationalIdHash = await _readDb.Solicitants
                        .Where(s => s.Id == solicitantId.Value)
                        .Select(s => s.NationalIdHash)
                        .SingleOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    if (string.IsNullOrEmpty(nationalIdHash))
                    {
                        _logger.LogDebug(
                            "Applicant role: solicitant {SolicitantId} has no NationalIdHash; skipping notification.",
                            solicitantId);
                        return;
                    }

                    var userId = await _readDb.UserProfiles
                        .Where(u => u.IsActive && u.NationalIdHash == nationalIdHash)
                        .Select(u => (long?)u.Id)
                        .SingleOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    if (userId is not null)
                    {
                        sink.Add(userId.Value);
                    }
                    return;
                }

            case "AssigneeSupervisor":
            case "ProcessOwner":
            case "ApprovingManager":
                // The supervisor relation, process-owner field, and approving-manager
                // relation are not yet modelled on the entities (the scope explicitly
                // permits this gap — "if absent, log warning + skip — don't fail
                // dispatch"). Future iterations land the lookups; today they are
                // best-effort no-ops.
                _logger.LogDebug(
                    "Recipient role {Role} not resolvable in this build; skipping.",
                    role);
                return;

            default:
                // The validator rejects unknown roles, so this branch is defensive.
                _logger.LogWarning("Unknown recipient role {Role}; skipping.", role);
                return;
        }
    }

    /// <summary>
    /// Determines whether the current local-time instant falls inside the configured
    /// quiet-hours window and, when it does, returns a human-readable schedule hint
    /// the caller folds into the notification subject so the recipient understands the
    /// notification was deferred.
    /// </summary>
    /// <param name="quietHours">(Start, End) minute-of-day pair from the strategy, or null.</param>
    /// <returns>
    /// A tuple <c>(deferred, scheduleHint)</c>. When <c>deferred = false</c> the hint
    /// is null and dispatch happens immediately.
    /// </returns>
    private (bool Deferred, string? ScheduleHint) ComputeQuietHoursDecision((int Start, int End)? quietHours)
    {
        if (quietHours is null)
        {
            return (false, null);
        }

        var (start, end) = quietHours.Value;
        var nowLocal = ToLocal(_clock.UtcNow);
        var nowMinute = (nowLocal.Hour * 60) + nowLocal.Minute;

        if (!IsInsideWindow(nowMinute, start, end))
        {
            return (false, null);
        }

        var endText = $"{end / 60:D2}:{end % 60:D2}";
        return (true, $"deferred-until={endText}");
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="minute"/> is inside the quiet-hours
    /// window [<paramref name="start"/>, <paramref name="end"/>] (inclusive). Wrapping
    /// windows (start &gt; end, e.g. 22:00..06:00) are handled by checking the two
    /// half-windows separately.
    /// </summary>
    /// <param name="minute">Current minute-of-day in 0..1439.</param>
    /// <param name="start">Window start minute-of-day.</param>
    /// <param name="end">Window end minute-of-day.</param>
    /// <returns><c>true</c> when the minute is inside the window.</returns>
    public static bool IsInsideWindow(int minute, int start, int end)
    {
        if (start <= end)
        {
            return minute >= start && minute <= end;
        }
        // Wrapping window (e.g. 22:00..06:00) — inside iff in [start, 1439] OR [0, end].
        return minute >= start || minute <= end;
    }

    /// <summary>
    /// Converts a UTC instant to Europe/Chisinau local time. Falls back to UTC when the
    /// platform timezone database does not carry the IANA id (e.g. legacy Windows
    /// hosts that have not been patched to the IANA layout); the failure is logged
    /// once and we degrade quietly because the orchestrator must never throw.
    /// </summary>
    /// <param name="utc">UTC instant from the injected clock.</param>
    /// <returns>The equivalent local <see cref="DateTime"/>.</returns>
    private DateTime ToLocal(DateTime utc)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(ChisinauTimezoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        }
        catch (TimeZoneNotFoundException)
        {
            _logger.LogWarning(
                "TimeZone {Tz} not found; quiet-hours math degrades to UTC.", ChisinauTimezoneId);
            return utc;
        }
        catch (InvalidTimeZoneException)
        {
            _logger.LogWarning(
                "TimeZone {Tz} is corrupt; quiet-hours math degrades to UTC.", ChisinauTimezoneId);
            return utc;
        }
    }

    /// <summary>
    /// Builds the subject + body pair the orchestrator hands to
    /// <see cref="INotificationService.EnqueueAsync(long, string, string, string?, System.Threading.CancellationToken)"/>. When
    /// <paramref name="templateCodeOverride"/> is non-null it appears in the subject
    /// prefix so the dispatch layer can pick the correct template (until a richer
    /// template router lands); the deterministic per-event default is used otherwise.
    /// </summary>
    /// <param name="eventCode">Canonical event code.</param>
    /// <param name="templateCodeOverride">Optional override (drives subject prefix).</param>
    /// <param name="templateContext">Optional key/value pairs serialised into the body.</param>
    /// <param name="scheduleHint">Optional quiet-hours hint folded into the subject.</param>
    /// <returns>The (subject, body) pair.</returns>
    private static (string Subject, string Body) BuildMessage(
        string eventCode,
        string? templateCodeOverride,
        IDictionary<string, string>? templateContext,
        string? scheduleHint)
    {
        var template = string.IsNullOrWhiteSpace(templateCodeOverride)
            ? $"WF:{eventCode}"
            : $"WF:{eventCode}:{templateCodeOverride}";
        var subject = scheduleHint is null ? template : $"{template} [{scheduleHint}]";

        if (templateContext is null || templateContext.Count == 0)
        {
            return (subject, $"Workflow event: {eventCode}");
        }

        var pairs = new List<string>(templateContext.Count);
        foreach (var kv in templateContext)
        {
            pairs.Add($"{kv.Key}={kv.Value}");
        }
        return (subject, $"Workflow event: {eventCode}; {string.Join("; ", pairs)}");
    }
}
