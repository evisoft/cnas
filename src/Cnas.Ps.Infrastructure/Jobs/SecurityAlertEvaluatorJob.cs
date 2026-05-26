using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// Quartz job that periodically scans the <see cref="AuditLog"/> stream against the
/// persisted <see cref="SecurityAlertRule"/> set and fires alerts when a rule's
/// rolling-window threshold is met. R0189 / SEC 048.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cadence.</b> The job is fired by the cron registered in
/// <see cref="QuartzComposition"/> (currently <c>0 */1 * * * ?</c> = every minute).
/// <see cref="SecurityAlertOptions.Cron"/> is the documented seam for a future
/// per-environment override; today the cron is hard-coded at registration time so
/// the wiring does not need to resolve <see cref="IOptions{T}"/> while building the
/// scheduler — mirrors the R0190 <c>TODO[r0189-cron]</c> pattern.
/// </para>
/// <para>
/// <b>Per-iteration algorithm.</b>
/// <list type="number">
///   <item><description>Read the singleton checkpoint row from
///   <see cref="ICnasDbContext.SecurityAlertEvaluatorStates"/>.</description></item>
///   <item><description>Read the active rule set.</description></item>
///   <item><description>Materialise the union window — every audit row with
///   <c>Id &gt; checkpoint</c> AND <c>EventAtUtc &gt;= now - max(WindowSeconds)</c>,
///   capped at <see cref="SecurityAlertOptions.MaxRowsPerWindow"/>.</description></item>
///   <item><description>For each rule: filter to its narrower window, run the cached
///   compiled regex on <see cref="AuditLog.EventCode"/>, and fire if the match count
///   meets <see cref="SecurityAlertRule.ThresholdCount"/> AND the per-rule cooldown
///   has elapsed.</description></item>
///   <item><description>Advance the checkpoint to the highest id scanned and
///   <c>SaveChangesAsync</c> once.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Why client-side regex.</b> Postgres-native <c>~</c> regex matching could let
/// the count happen in SQL, but the EF Core InMemory test provider does not
/// implement it. R0162 / R0164 set the precedent of doing in-memory pattern matching
/// client-side for tests; we follow that precedent so production and test code share
/// the same execution path. The candidate set is bounded by the time window and the
/// safety cap so the in-memory pass is always cheap (≤5000 rows by default).
/// </para>
/// <para>
/// <b>Regex DoS guard.</b> Every compiled regex is constructed with
/// <see cref="RegexOptions.Compiled"/> + <see cref="RegexOptions.CultureInvariant"/>
/// and a 50 ms per-match timeout (<see cref="System.TimeSpan"/>) so a catastrophically
/// backtracking pattern cannot wedge the evaluator. Compile failures are captured by
/// the cache (storing <c>null</c>) and surface as a single ERROR log per pattern per
/// iteration — the rule is skipped and the rest of the rule set continues.
/// </para>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/>
/// prevents two fires from racing the same checkpoint row. Even without it, the
/// strictly-greater-than predicate would only produce redundant scans, never double
/// fires — the cooldown stamps the rule before the next iteration sees it.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SecurityAlertEvaluatorJob : IJob
{
    /// <summary>Stable Quartz job identity — referenced by <see cref="QuartzComposition"/>.</summary>
    public const string JobIdentity = "security-alert-evaluator";

    /// <summary>Stable Quartz trigger identity — referenced by <see cref="QuartzComposition"/>.</summary>
    public const string TriggerIdentity = "security-alert-evaluator-trigger";

    /// <summary>
    /// Singleton-row key used to locate the checkpoint in
    /// <see cref="ICnasDbContext.SecurityAlertEvaluatorStates"/>. The migration seeds
    /// exactly one row with this key; the job is hard-coded to read and write that
    /// one row.
    /// </summary>
    public const string SingletonKey = "default";

    /// <summary>
    /// Audit event code emitted by every successful rule fire. Stable contract for
    /// downstream consumers (R0190 SIEM forwarder, audit explorer in R0193).
    /// </summary>
    public const string FiredEventCode = "SECURITY_ALERT.FIRED";

    /// <summary>
    /// Synthetic actor id used on the audit row written by the evaluator. The
    /// <c>system:</c> prefix matches the convention used by other background jobs
    /// (e.g. <c>system:r0188-replay</c>); the suffix names this job so the audit
    /// trail attributes the fire to the right pipeline.
    /// </summary>
    public const string SystemActor = "system:r0189-evaluator";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (Always profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.SecurityAlertEvaluator;

    /// <summary>
    /// Process-wide cache of compiled regexes keyed by pattern string. <c>null</c>
    /// values mark patterns that failed to compile — the cache prevents log spam
    /// (one ERROR per pattern per iteration would still produce one ERROR per
    /// pattern overall after the cache fills). Static so multiple
    /// <see cref="SecurityAlertEvaluatorJob"/> instances built across re-scoping
    /// share the same cache.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex?> PatternCache =
        new(StringComparer.Ordinal);

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly SecurityAlertOptions _options;
    private readonly ILogger<SecurityAlertEvaluatorJob> _logger;

    /// <summary>
    /// Constructs the evaluator with its scope factory + options snapshot. A scope
    /// factory is injected (rather than the scoped collaborators directly) because
    /// the job's owning scheduler is a singleton — we must materialise a fresh DI
    /// scope per fire to resolve <see cref="ICnasDbContext"/> + <see cref="IAuditService"/>
    /// + <see cref="INotificationService"/> + <see cref="ICnasTimeProvider"/>.
    /// </summary>
    /// <param name="scopes">Scope factory used to resolve scoped collaborators per iteration.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="options">Bound options snapshot from <see cref="SecurityAlertOptions.SectionName"/>.</param>
    /// <param name="logger">Structured logger for warning + error diagnostics.</param>
    public SecurityAlertEvaluatorJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        IOptions<SecurityAlertOptions> options,
        ILogger<SecurityAlertEvaluatorJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _peakHourGate = peakHourGate;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // R2173 / TOR PSR 004 — peak-hour gate. Always profile means the gate
        // always allows; the call is uniform across the job fleet so the
        // counter has a complete time series.
        if (await _peakHourGate.EvaluateAsync(JobCode, context.CancellationToken).ConfigureAwait(false)
            == PeakHourGateDecision.Skip)
        {
            return;
        }

        // Disabled-state short-circuit. Operators may flip Enabled=false to delegate
        // alerting to an external rule engine without touching the rule rows.
        if (!_options.Enabled)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var clock = scope.ServiceProvider.GetRequiredService<ICnasTimeProvider>();
        var ct = context.CancellationToken;

        var state = await db.SecurityAlertEvaluatorStates
            .FirstOrDefaultAsync(s => s.Key == SingletonKey && s.IsActive, ct)
            .ConfigureAwait(false);

        if (state is null)
        {
            // The migration seeds the row; missing state means either the migration
            // has not run or an operator soft-deleted the row. We cannot safely
            // advance the checkpoint, so log and skip.
            _logger.LogWarning(
                "SecurityAlertEvaluatorJob: state row missing (key={Key}); skipping iteration.",
                SingletonKey);
            return;
        }

        var rules = await db.SecurityAlertRules
            .Where(r => r.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rules.Count == 0)
        {
            // No rules → nothing to score. Don't advance the checkpoint either: a
            // future operator enabling a rule will want it to evaluate against
            // recent audit rows, not just rows that arrive AFTER the enable.
            return;
        }

        var now = clock.UtcNow;
        var maxWindow = rules.Max(r => r.WindowSeconds);
        var windowStart = now.AddSeconds(-maxWindow);
        var batchCap = Math.Max(1, _options.MaxRowsPerWindow);

        // One scan for the union window — each rule then filters to its narrower
        // window in process. Sorting by Id keeps the cap deterministic (oldest
        // unscored rows first).
        var candidates = await db.AuditLogs
            .Where(a => a.IsActive && a.Id > state.LastEvaluatedAuditId && a.EventAtUtc >= windowStart)
            .OrderBy(a => a.Id)
            .Take(batchCap)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var maxIdSeen = candidates.Count == 0 ? state.LastEvaluatedAuditId : candidates.Max(c => c.Id);

        foreach (var rule in rules)
        {
            var rx = GetRegex(rule.EventCodePattern);
            if (rx is null)
            {
                _logger.LogError(
                    "SecurityAlertRule {Code} has invalid pattern; skipping.",
                    rule.Code);
                continue;
            }

            var ruleWindowStart = now.AddSeconds(-rule.WindowSeconds);
            var matches = 0;
            foreach (var c in candidates)
            {
                if (c.EventAtUtc < ruleWindowStart)
                {
                    continue;
                }
                if (SafeIsMatch(rx, c.EventCode))
                {
                    matches++;
                }
            }

            if (matches < rule.ThresholdCount)
            {
                continue;
            }

            // Cooldown check — strict greater-than on the elapsed comparison so a
            // rule with CooldownSeconds=0 (degenerate) re-fires every iteration.
            if (rule.LastFiredAtUtc is not null
                && rule.LastFiredAtUtc.Value.AddSeconds(rule.CooldownSeconds) > now)
            {
                continue;
            }

            await FireRuleAsync(
                db, audit, notifications, rule, matches, now, context.FireInstanceId, ct)
                .ConfigureAwait(false);
        }

        state.LastEvaluatedAuditId = maxIdSeen;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fires a single rule: resolves recipients, queues per-user notifications,
    /// records the audit row, increments the counter, and stamps the rule's
    /// cooldown. All writes participate in the per-iteration
    /// <see cref="ICnasDbContext.SaveChangesAsync"/> at the end of
    /// <see cref="Execute"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Empty recipient set.</b> If no <see cref="UserProfile"/> carries the rule's
    /// <see cref="SecurityAlertRule.RecipientGroup"/> code, the rule still fires
    /// (audit + counter + cooldown stamp) and the evaluator emits a WARN log so the
    /// operator can correct the role assignment. Silently swallowing the alert would
    /// be worse than emitting it with zero recipients.
    /// </para>
    /// <para>
    /// <b>Audit details — no PII.</b> The audit row's <c>DetailsJson</c> carries the
    /// rule code, match count, window seconds, and recipient count only. It does NOT
    /// carry user identifiers, IDs of the matched audit rows, or any other field
    /// that could leak personal data. The PII redactor (R0185) is invoked at
    /// <see cref="IAuditService.RecordAsync"/>'s boundary as a defence-in-depth
    /// double-check; this method's payload is engineered to pass redaction
    /// untouched.
    /// </para>
    /// </remarks>
    private async Task FireRuleAsync(
        ICnasDbContext db,
        IAuditService audit,
        INotificationService notifications,
        SecurityAlertRule rule,
        int matches,
        DateTime now,
        string fireInstanceId,
        CancellationToken ct)
    {
        // Recipient resolution — EF Core 10 supports List<string>.Contains on text[]
        // columns on Postgres (Npgsql) and LINQ-to-Objects handles it natively on
        // the InMemory provider, so the same query works in both worlds.
        var recipients = await db.UserProfiles
            .Where(u => u.IsActive && u.State == UserAccountState.Active && u.Roles.Contains(rule.RecipientGroup))
            .Select(u => u.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (recipients.Count == 0)
        {
            _logger.LogWarning(
                "SecurityAlertRule {Code} fired but recipient group {Group} has no active members; alert audit + counter still recorded.",
                rule.Code, rule.RecipientGroup);
        }

        var subject = $"Security alert: {rule.Code}";
        var body = $"Rule '{rule.Code}' matched {matches} event(s) in the last {rule.WindowSeconds} second(s). Severity: {rule.AlertSeverity}.";

        foreach (var uid in recipients)
        {
            // Enqueue per recipient. Failure here is logged by the notification
            // service itself; we do not abort the fire on partial dispatch failure
            // — the audit row + counter still record the rule fire.
            await notifications.EnqueueAsync(uid, subject, body, fireInstanceId, ct).ConfigureAwait(false);
        }

        // Audit details — small JSON, intentionally non-PII (rule code + counts only).
        var detailsJson = JsonSerializer.Serialize(new
        {
            ruleCode = rule.Code,
            matchCount = matches,
            windowSeconds = rule.WindowSeconds,
            thresholdCount = rule.ThresholdCount,
            recipientsCount = recipients.Count,
        });

        await audit.RecordAsync(
            eventCode: FiredEventCode,
            severity: rule.AlertSeverity,
            actorId: SystemActor,
            targetEntity: nameof(SecurityAlertRule),
            targetEntityId: rule.Id,
            detailsJson: detailsJson,
            sourceIp: null,
            correlationId: fireInstanceId,
            cancellationToken: ct).ConfigureAwait(false);

        CnasMeter.SecurityAlertFired.Add(
            1,
            new KeyValuePair<string, object?>("rule.code", rule.Code));

        // Cooldown stamp — written into the tracked rule entity, flushed by the
        // single SaveChangesAsync at the end of the iteration.
        rule.LastFiredAtUtc = now;
    }

    /// <summary>
    /// Resolves a compiled <see cref="Regex"/> for the supplied pattern, caching
    /// successes (and compile failures as <c>null</c>) process-wide. The 50 ms
    /// per-match timeout is the regex-DoS guard mandated by CLAUDE.md — a
    /// catastrophically backtracking pattern surfaces as a
    /// <see cref="RegexMatchTimeoutException"/> which <see cref="SafeIsMatch"/>
    /// swallows so a single malformed rule cannot wedge the evaluator.
    /// </summary>
    /// <param name="pattern">User-supplied regex pattern from
    /// <see cref="SecurityAlertRule.EventCodePattern"/>.</param>
    /// <returns>The compiled regex, or <c>null</c> when the pattern is invalid.</returns>
    private static Regex? GetRegex(string pattern) =>
        PatternCache.GetOrAdd(pattern, p =>
        {
            try
            {
                return new Regex(
                    p,
                    RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException)
            {
                // Invalid pattern — return null sentinel so the caller skips the
                // rule. The .NET docs guarantee invalid-pattern construction throws
                // ArgumentException; we narrow the catch to that to avoid masking
                // unexpected failures.
                return null;
            }
        });

    /// <summary>
    /// Wrapper around <see cref="Regex.IsMatch(string)"/> that swallows
    /// <see cref="RegexMatchTimeoutException"/> — a pathological backtrack on a
    /// single event code returns <c>false</c> so the broken pattern cannot wedge
    /// the evaluator. The 50 ms timeout is set at compile time in
    /// <see cref="GetRegex"/>.
    /// </summary>
    /// <param name="rx">Compiled regex (timeout already set).</param>
    /// <param name="input">Candidate <see cref="AuditLog.EventCode"/> string.</param>
    /// <returns><c>true</c> when the regex matches; <c>false</c> on no-match OR timeout.</returns>
    private static bool SafeIsMatch(Regex rx, string input)
    {
        try
        {
            return rx.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
