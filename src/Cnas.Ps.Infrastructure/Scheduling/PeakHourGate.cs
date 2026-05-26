using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Scheduling;

/// <summary>
/// R2173 / TOR PSR 004 — production implementation of
/// <see cref="IPeakHourGate"/>. Combines the per-job profile registry
/// (<see cref="JobScheduleProfileRegistry.Defaults"/>) with the runtime
/// configuration (<see cref="PeakHourGateOptions"/>) and the current
/// Europe/Chisinau local time to decide whether each scheduled fire should
/// proceed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b>
/// </para>
/// <list type="number">
///   <item><description>If <see cref="PeakHourGateOptions.GlobalOverride"/> is
///   <c>true</c> → <see cref="PeakHourGateDecision.Allow"/> (emergency override).</description></item>
///   <item><description>Look up the job's profile mode. Unknown jobs default to
///   <see cref="JobScheduleProfileMode.Anytime"/>.</description></item>
///   <item><description><see cref="JobScheduleProfileMode.Always"/> or
///   <see cref="JobScheduleProfileMode.Anytime"/> → <see cref="PeakHourGateDecision.Allow"/>.</description></item>
///   <item><description><see cref="JobScheduleProfileMode.OffPeakOnly"/> →
///   compute the current local hour-of-day, compare against
///   <see cref="PeakHourGateOptions.OffPeakStartLocalHour"/> and
///   <see cref="PeakHourGateOptions.OffPeakEndLocalHour"/> (wrap-around aware);
///   inside the window → <see cref="PeakHourGateDecision.Allow"/>, otherwise
///   <see cref="PeakHourGateDecision.Skip"/> AND emit a single
///   Information-severity audit row.</description></item>
/// </list>
/// <para>
/// <b>Timezone resolution.</b> <see cref="ChisinauTimezoneId"/> is looked up
/// once per evaluation. A missing/corrupt timezone DB is treated as a soft
/// failure — the gate logs once and degrades to <see cref="PeakHourGateDecision.Allow"/>
/// so misconfiguration never blocks scheduled work.
/// </para>
/// <para>
/// <b>Side-effects.</b> Each evaluation emits exactly one
/// <c>cnas.peak_hour.gate</c> counter increment with <c>decision</c> =
/// <c>allow</c> | <c>skip</c>. A <c>JOB.SKIPPED_BY_PEAK_HOUR_GATE</c>
/// Information-severity audit row is written only on the Skip branch — the
/// Allow branch is the happy path and would otherwise spam the audit trail.
/// </para>
/// </remarks>
public sealed class PeakHourGate : IPeakHourGate
{
    /// <summary>IANA timezone identifier consulted on every evaluation.</summary>
    private const string ChisinauTimezoneId = "Europe/Chisinau";

    private readonly ICnasTimeProvider _clock;
    private readonly IOptionsMonitor<PeakHourGateOptions> _options;
    private readonly PeakHourGateOverrideStore _overrideStore;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PeakHourGate> _logger;

    /// <summary>
    /// Constructs the gate with its collaborators.
    /// </summary>
    /// <param name="clock">UTC clock used to derive the current local-time hour-of-day.</param>
    /// <param name="options">Live options snapshot — the gate honours runtime config refresh.</param>
    /// <param name="overrideStore">Singleton holding the runtime admin-override flag.</param>
    /// <param name="scopes">DI scope factory used to resolve the scoped audit service per Skip emission.</param>
    /// <param name="logger">Structured logger.</param>
    public PeakHourGate(
        ICnasTimeProvider clock,
        IOptionsMonitor<PeakHourGateOptions> options,
        PeakHourGateOverrideStore overrideStore,
        IServiceScopeFactory scopes,
        ILogger<PeakHourGate> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(overrideStore);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);
        _clock = clock;
        _options = options;
        _overrideStore = overrideStore;
        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PeakHourGateDecision> EvaluateAsync(string jobCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobCode);

        var opts = _options.CurrentValue;

        // Emergency-override short-circuit. The store mirrors the boot-time
        // option default and is mutated by the admin endpoint at runtime.
        // Either source wins — operators may flip the toggle either way.
        if (opts.GlobalOverride || _overrideStore.IsOverrideActive())
        {
            EmitDecision(PeakHourGateDecision.Allow);
            return PeakHourGateDecision.Allow;
        }

        // Unknown job codes default to Anytime (fire on the cron, no gating).
        // We never want to suppress a fire because a new job hasn't been added
        // to the registry yet.
        var mode = JobScheduleProfileRegistry.Defaults.TryGetValue(jobCode, out var profile)
            ? profile.Mode
            : JobScheduleProfileMode.Anytime;

        if (mode != JobScheduleProfileMode.OffPeakOnly)
        {
            EmitDecision(PeakHourGateDecision.Allow);
            return PeakHourGateDecision.Allow;
        }

        // OffPeakOnly — consult the wall clock.
        var localHour = CurrentLocalHour(opts);
        if (IsHourInsideWindow(localHour, opts.OffPeakStartLocalHour, opts.OffPeakEndLocalHour))
        {
            EmitDecision(PeakHourGateDecision.Allow);
            return PeakHourGateDecision.Allow;
        }

        // Skip branch — emit counter + audit row.
        EmitDecision(PeakHourGateDecision.Skip);
        await EmitSkipAuditAsync(jobCode, localHour, opts, cancellationToken).ConfigureAwait(false);
        return PeakHourGateDecision.Skip;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="hour"/> falls inside the
    /// closed window <c>[start, end]</c>. Wrap-around windows (start &gt; end)
    /// are treated as <c>[start..23] ∪ [0..end]</c> so e.g. <c>22..06</c>
    /// captures both 23 and 5.
    /// </summary>
    /// <param name="hour">Current local hour-of-day in 0..23.</param>
    /// <param name="start">Inclusive window start hour-of-day in 0..23.</param>
    /// <param name="end">Inclusive window end hour-of-day in 0..23.</param>
    /// <returns><c>true</c> when the hour is inside the window.</returns>
    internal static bool IsHourInsideWindow(int hour, int start, int end)
    {
        // Normalise out-of-range inputs to 0..23 so misconfiguration cannot
        // throw — the gate is a safety net, not a validator.
        var h = ((hour % 24) + 24) % 24;
        var s = ((start % 24) + 24) % 24;
        var e = ((end % 24) + 24) % 24;

        if (s <= e)
        {
            return h >= s && h <= e;
        }
        // Wrap-around window.
        return h >= s || h <= e;
    }

    /// <summary>
    /// Returns the current Europe/Chisinau hour-of-day (0..23). Degrades to
    /// UTC on timezone-database failure — the gate must not throw.
    /// </summary>
    /// <param name="opts">Options snapshot (reserved for future per-tenant TZ overrides).</param>
    /// <returns>Local hour-of-day.</returns>
    private int CurrentLocalHour(PeakHourGateOptions opts)
    {
        _ = opts; // Reserved for future per-tenant TZ override.
        var utc = _clock.UtcNow;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(ChisinauTimezoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, tz).Hour;
        }
        catch (TimeZoneNotFoundException)
        {
            _logger.LogWarning(
                "PeakHourGate: timezone {Tz} not found; falling back to UTC for hour-of-day evaluation.",
                ChisinauTimezoneId);
            return utc.Hour;
        }
        catch (InvalidTimeZoneException)
        {
            _logger.LogWarning(
                "PeakHourGate: timezone {Tz} is corrupt; falling back to UTC for hour-of-day evaluation.",
                ChisinauTimezoneId);
            return utc.Hour;
        }
    }

    /// <summary>
    /// Emits the <c>cnas.peak_hour.gate</c> counter increment tagged with the
    /// decision outcome.
    /// </summary>
    /// <param name="decision">The decision the gate produced.</param>
    private static void EmitDecision(PeakHourGateDecision decision)
    {
        var tag = decision == PeakHourGateDecision.Allow ? "allow" : "skip";
        CnasMeter.PeakHourGate.Add(1,
            new System.Collections.Generic.KeyValuePair<string, object?>("decision", tag));
    }

    /// <summary>
    /// Writes a single Information-severity audit row capturing a Skip outcome.
    /// The row carries the job code + the local hour so operators can correlate
    /// the skip with the configured window when reviewing the audit trail.
    /// Audit-write failures are swallowed — the gate must not throw.
    /// </summary>
    /// <param name="jobCode">Job code that was skipped.</param>
    /// <param name="localHour">Local hour-of-day at the time of evaluation.</param>
    /// <param name="opts">Options snapshot capturing the active window.</param>
    /// <param name="cancellationToken">Cancellation propagated from the Quartz fire.</param>
    private async Task EmitSkipAuditAsync(
        string jobCode,
        int localHour,
        PeakHourGateOptions opts,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            var details = JsonSerializer.Serialize(new
            {
                jobCode,
                localHour,
                offPeakStartLocalHour = opts.OffPeakStartLocalHour,
                offPeakEndLocalHour = opts.OffPeakEndLocalHour,
            });
            await audit.RecordAsync(
                "JOB.SKIPPED_BY_PEAK_HOUR_GATE",
                AuditSeverity.Information,
                actorId: "system",
                targetEntity: "QuartzJob",
                targetEntityId: null,
                detailsJson: details,
                sourceIp: null,
                correlationId: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defence-in-depth — the gate must never throw out to Quartz.
            _logger.LogWarning(
                ex,
                "PeakHourGate: failed to emit Skip audit for jobCode={JobCode}; continuing.",
                jobCode);
        }
    }
}
