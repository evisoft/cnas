using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Scheduling;
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
/// R2267 / SEC 020 — Quartz job that sweeps the <c>UserSessions</c> table every 5
/// minutes for live, non-locked rows whose <c>LastActivityUtc</c> is older than
/// <see cref="SessionLimitOptions.IdleLockMinutes"/> (default 15) and flips them to
/// <c>IsLocked=true</c>. Each lock writes a single Notice-severity audit row
/// (<c>USER.SESSION.LOCKED_AUTO</c>) plus a <c>cnas.session.auto_locked</c>
/// counter increment so operators can chart idle pressure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> The predicate excludes already-locked + already-terminated
/// rows so a second fire on the same data set is a no-op (no row is locked twice,
/// no audit row is written twice). <see cref="DisallowConcurrentExecutionAttribute"/>
/// is belt-and-braces.
/// </para>
/// <para>
/// <b>Bounded work.</b> No explicit batch cap today — the steady-state idle set is
/// small (we lock idle sessions every 5 minutes, so the per-fire backlog is at most
/// the 5-minute new-idle stream). If volume grows the predicate can be trimmed
/// exactly like the unclaimed-task job's <c>Take</c>.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SessionAutoLockJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "session-auto-lock";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "session-auto-lock-trigger";

    /// <summary>
    /// Cron expression — every 5 minutes on the second-zero boundary. Mirrors the
    /// cadence of <c>UserAbsenceLifecycleJob</c> so the operator-facing background
    /// fleet stays uniform.
    /// </summary>
    public const string Cron = "0 0/5 * * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (Always profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.SessionAutoLock;

    private readonly IServiceScopeFactory _scopes;
    private readonly ICnasTimeProvider _clock;
    private readonly IPeakHourGate _peakHourGate;
    private readonly SessionLimitOptions _options;
    private readonly ILogger<SessionAutoLockJob> _logger;

    /// <summary>Constructs the auto-lock job with its collaborators.</summary>
    /// <param name="scopes">Scope factory — the job resolves scoped collaborators per fire.</param>
    /// <param name="clock">UTC clock — drives the idle cut-off.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="options">Tunable session-limit knobs (idle threshold).</param>
    /// <param name="logger">Structured logger.</param>
    public SessionAutoLockJob(
        IServiceScopeFactory scopes,
        ICnasTimeProvider clock,
        IPeakHourGate peakHourGate,
        IOptions<SessionLimitOptions> options,
        ILogger<SessionAutoLockJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _clock = clock;
        _peakHourGate = peakHourGate;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;

        // R2173 / TOR PSR 004 — peak-hour gate. Always profile means the gate
        // always allows; the uniform call keeps the counter time-series complete.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        var now = _clock.UtcNow;
        var idleCutoff = now - TimeSpan.FromMinutes(Math.Max(1, _options.IdleLockMinutes));

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICnasDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        // Resolve live, non-locked rows whose last activity is older than the idle
        // cutoff. Materialised eagerly so the audit-write step can iterate without
        // holding a long-lived reader open.
        var due = await db.UserSessions
            .Where(s => s.IsActive
                && !s.IsTerminated
                && !s.IsLocked
                && s.LastActivityUtc < idleCutoff)
            .ToListAsync(ct).ConfigureAwait(false);

        if (due.Count == 0) return;

        foreach (var row in due)
        {
            row.IsLocked = true;
            row.LockedAtUtc = now;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = "system";
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var row in due)
        {
            var details = JsonSerializer.Serialize(new
            {
                userId = row.UserUserId,
                sessionRowId = row.Id,
                idleSinceUtc = row.LastActivityUtc,
                reason = "auto-lock",
            });
            await audit.RecordAsync(
                "USER.SESSION.LOCKED_AUTO",
                AuditSeverity.Notice,
                "system",
                nameof(UserSession),
                row.Id,
                details,
                sourceIp: null,
                correlationId: null,
                ct).ConfigureAwait(false);
        }

        CnasMeter.SessionAutoLocked.Add(due.Count);
        _logger.LogInformation(
            "SessionAutoLockJob locked {Count} idle sessions (idleCutoff={Cutoff:O}).",
            due.Count, idleCutoff);
    }
}
