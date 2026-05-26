namespace Cnas.Ps.Application.Scheduling;

/// <summary>
/// R2173 / TOR PSR 004 — outcome of a peak-hour gate evaluation. Returned by
/// <see cref="IPeakHourGate.EvaluateAsync(string, System.Threading.CancellationToken)"/>;
/// each registered job inspects this value at the top of its <c>Execute</c> and
/// short-circuits when the gate refuses the fire.
/// </summary>
public enum PeakHourGateDecision
{
    /// <summary>
    /// The gate permits the job to fire. Either the job's profile is
    /// <see cref="JobScheduleProfileMode.Always"/> / <see cref="JobScheduleProfileMode.Anytime"/>,
    /// the current time falls inside the off-peak window, or the global
    /// override toggle is enabled.
    /// </summary>
    Allow = 0,

    /// <summary>
    /// The gate refuses the fire. The job's profile is
    /// <see cref="JobScheduleProfileMode.OffPeakOnly"/> AND the current time
    /// is outside the configured off-peak window. The job must return
    /// without performing its work; the next scheduled fire on the next cron
    /// boundary will re-evaluate.
    /// </summary>
    Skip = 1,
}
