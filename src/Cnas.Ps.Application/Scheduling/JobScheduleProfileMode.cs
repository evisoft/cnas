namespace Cnas.Ps.Application.Scheduling;

/// <summary>
/// R2173 / TOR PSR 004 — declares the peak-hour eligibility of a Quartz job.
/// Consumed by <see cref="JobScheduleProfile"/> and the peak-hour gate to decide
/// whether a particular fire should proceed or short-circuit during business hours.
/// </summary>
/// <remarks>
/// <para>
/// Heavy maintenance work (KPI snapshot, ETL projection, audit archive replay)
/// is sensitive to operator-facing latency: an analytical job that pegs DB
/// connections at 14:00 starves the citizen-facing services. The three modes
/// encode the operational policy:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Always"/> — fire unconditionally
///   (security-critical evaluators, SIEM forwarders, fast cache refreshers).</description></item>
///   <item><description><see cref="OffPeakOnly"/> — only fire inside the
///   off-peak window declared in <c>PeakHourGateOptions</c>; skip otherwise.</description></item>
///   <item><description><see cref="Anytime"/> — fire on the schedule, no
///   peak-hour restriction (most operational jobs).</description></item>
/// </list>
/// </remarks>
public enum JobScheduleProfileMode
{
    /// <summary>
    /// Job is allowed to fire unconditionally. Used by security-critical and
    /// low-latency evaluators (e.g. <c>SiemForwarder</c>, <c>SecurityAlertEvaluator</c>,
    /// session auto-lock) whose value comes from running every interval and
    /// whose per-fire cost is bounded.
    /// </summary>
    Always = 0,

    /// <summary>
    /// Job is only allowed to fire inside the off-peak window. Used by heavy
    /// maintenance work (KPI snapshot, ETL projection, audit archive replay,
    /// daily summaries) that defers to business hours.
    /// </summary>
    OffPeakOnly = 1,

    /// <summary>
    /// Job is allowed to fire on its native cron with no peak-hour gating.
    /// Default for unknown / new job codes — the gate degrades to a no-op so
    /// adding a job does not accidentally suppress it. Treasury distribution
    /// and admin-action observers use this mode explicitly because the
    /// financial / operational pipeline must run continuously.
    /// </summary>
    Anytime = 2,
}
