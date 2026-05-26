namespace Cnas.Ps.Application.Scheduling;

/// <summary>
/// R2173 / TOR PSR 004 — declarative profile attaching a
/// <see cref="JobScheduleProfileMode"/> to a single Quartz job code. Consumed by
/// <see cref="JobScheduleProfileRegistry"/> and the peak-hour gate when a job
/// fires.
/// </summary>
/// <remarks>
/// The <see cref="JobCode"/> is the same stable identifier the registry, gate,
/// and audit trail use to refer to the job (e.g. <c>"KpiSnapshot"</c>). It is
/// intentionally distinct from the Quartz <c>JobKey</c> name because the gate
/// is a higher-level policy concept — multiple Quartz registrations could
/// share one profile if they share semantics.
/// </remarks>
/// <param name="JobCode">Stable code identifying the job (matches the constants on each job's <c>JobCode</c> field).</param>
/// <param name="Mode">Peak-hour eligibility mode for the job.</param>
public sealed record JobScheduleProfile(string JobCode, JobScheduleProfileMode Mode);
