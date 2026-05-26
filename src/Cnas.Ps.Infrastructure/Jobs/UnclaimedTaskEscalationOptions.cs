namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// Configuration options for <see cref="UnclaimedTaskEscalationJob"/> (R0202 / CF 20.05).
/// Bound from the <c>Cnas:WorkflowEscalation</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defaults.</b> The defaults baked into this options class express the
/// "reasonable production starting point" — operators can tune via configuration without
/// redeploying the chart:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="TimeoutWindow"/> = 4 hours — long enough for a normal
///     shift, short enough to surface persistent gaps within a single working day.</description></item>
///   <item><description><see cref="Cron"/> = hourly — matches <c>MissingDocsSlaJob</c>'s
///     cadence so operators have one mental model for SLA sweeps.</description></item>
///   <item><description><see cref="MaxBatchSize"/> = 200 — caps the per-fire impact under
///     a pathological backlog so a single bad run can't fan out 50k notifications.</description></item>
/// </list>
/// <para>
/// <b>Stability.</b> Changing <see cref="TimeoutWindow"/> at runtime is safe — the job
/// reads the current value on every fire. Changing <see cref="Cron"/> requires a Quartz
/// reschedule; the current registration in <c>QuartzComposition</c> hard-codes the
/// hourly cron until <c>TODO[r0202-cron]</c> pipes the option through.
/// </para>
/// </remarks>
public sealed class UnclaimedTaskEscalationOptions
{
    /// <summary>Configuration section binding path: <c>Cnas:WorkflowEscalation</c>.</summary>
    public const string SectionName = "Cnas:WorkflowEscalation";

    /// <summary>
    /// Time a task may sit unclaimed in the group inbox before it is escalated. Defaults
    /// to 4 hours — long enough for a normal shift, short enough to surface persistent
    /// gaps within a day.
    /// </summary>
    public TimeSpan TimeoutWindow { get; init; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Quartz cron expression (6-field — seconds-minute-hour-day-month-dow) driving the
    /// job trigger. Defaults to "every hour, on the hour" to mirror
    /// <c>MissingDocsSlaJob.Cron</c>.
    /// </summary>
    public string Cron { get; init; } = "0 0 0/1 * * ?";

    /// <summary>
    /// Per-run cap on the batch size to bound impact under a pathological backlog. The
    /// job orders by <see cref="Cnas.Ps.Core.Domain.WorkflowTask.UnclaimedSinceUtc"/>
    /// ascending so the oldest rows are handled first.
    /// </summary>
    public int MaxBatchSize { get; init; } = 200;
}
