using System.Threading;
using System.Threading.Tasks;

namespace Cnas.Ps.Application.Scheduling;

/// <summary>
/// R2173 / TOR PSR 004 — peak-hour gate consulted by each background job at
/// the top of its <c>Execute</c> method. The gate decides — based on the job's
/// <see cref="JobScheduleProfile"/>, the current local time, and the global
/// override toggle — whether the fire should proceed or short-circuit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract.</b> The gate MUST NOT throw. On any internal error (timezone
/// lookup failure, options resolution failure) it returns
/// <see cref="PeakHourGateDecision.Allow"/> — the gate is a safety net, not a
/// circuit breaker. Misconfiguration must never prevent scheduled work from
/// firing; the worst-case is one extra fire during peak hours, which the
/// operator can address via the admin override.
/// </para>
/// <para>
/// <b>Side-effects.</b> The gate emits a single counter increment per call
/// (<c>cnas.peak_hour.gate</c> tagged with <c>decision</c>) and, on a
/// <see cref="PeakHourGateDecision.Skip"/> outcome, one Information-severity
/// audit row (<c>JOB.SKIPPED_BY_PEAK_HOUR_GATE</c>). Both are deliberately
/// low-severity so the gate does not spam the security trail during normal
/// operation.
/// </para>
/// </remarks>
public interface IPeakHourGate
{
    /// <summary>
    /// Evaluates whether the job identified by <paramref name="jobCode"/>
    /// should proceed at the current instant.
    /// </summary>
    /// <param name="jobCode">
    /// Stable job code (matches a key in <see cref="JobScheduleProfileRegistry.Defaults"/>).
    /// Unknown codes are permitted and default to
    /// <see cref="JobScheduleProfileMode.Anytime"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token propagated from the Quartz fire.</param>
    /// <returns>
    /// <see cref="PeakHourGateDecision.Allow"/> when the job may proceed,
    /// <see cref="PeakHourGateDecision.Skip"/> when the job must short-circuit.
    /// </returns>
    Task<PeakHourGateDecision> EvaluateAsync(string jobCode, CancellationToken cancellationToken);
}
