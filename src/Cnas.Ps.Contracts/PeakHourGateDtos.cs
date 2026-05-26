using System.Collections.Generic;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R2173 / TOR PSR 004 — current operational status of the peak-hour gate,
/// returned by <c>GET /api/admin/peak-hour-gate/status</c>. Carries the active
/// window configuration plus the per-job decision the gate would emit now.
/// </summary>
/// <param name="OffPeakStartLocalHour">Inclusive local-time hour-of-day where the off-peak window starts (default 22).</param>
/// <param name="OffPeakEndLocalHour">Inclusive local-time hour-of-day where the off-peak window ends (default 6).</param>
/// <param name="GlobalOverride">When <c>true</c>, the gate always returns <c>Allow</c> regardless of profile or window.</param>
/// <param name="EvaluatedAtLocal">Local-time instant at which the per-job decisions were sampled.</param>
/// <param name="Decisions">Per-job-code decision dictionary; values are <c>"Allow"</c> or <c>"Skip"</c>.</param>
public sealed record PeakHourGateStatusDto(
    int OffPeakStartLocalHour,
    int OffPeakEndLocalHour,
    bool GlobalOverride,
    System.DateTime EvaluatedAtLocal,
    IReadOnlyDictionary<string, string> Decisions);

/// <summary>
/// R2173 / TOR PSR 004 — request body for <c>POST /api/admin/peak-hour-gate/override</c>.
/// Flips the <c>GlobalOverride</c> toggle so emergency manual runs of OffPeakOnly
/// jobs are not blocked during peak hours. Critical audit
/// (<c>PEAK_HOUR_GATE.OVERRIDDEN</c>) is emitted on every successful call.
/// </summary>
/// <param name="Enabled">Target state of the global override toggle.</param>
public sealed record PeakHourGateOverrideInput(bool Enabled);
