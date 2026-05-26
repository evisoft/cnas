namespace Cnas.Ps.Infrastructure.Scheduling;

/// <summary>
/// R2173 / TOR PSR 004 — bound options for the peak-hour gate. Configuration
/// section <c>"Cnas:PeakHourGate"</c>. Operators tune the off-peak window or
/// flip the global override toggle without redeploying.
/// </summary>
/// <remarks>
/// <para>
/// The window is expressed in Europe/Chisinau local hours of day, inclusive.
/// The default 22..06 wraps midnight; the gate handles wrap-around windows
/// explicitly so e.g. 23:30 and 05:30 both evaluate as off-peak.
/// </para>
/// <para>
/// The <see cref="GlobalOverride"/> toggle is intended for emergency manual
/// runs — flipping it to <c>true</c> bypasses every <c>OffPeakOnly</c> profile
/// so a stuck pipeline can be re-fired immediately. Every flip is mirrored by
/// a Critical <c>PEAK_HOUR_GATE.OVERRIDDEN</c> audit row written by the admin
/// controller.
/// </para>
/// </remarks>
public sealed class PeakHourGateOptions
{
    /// <summary>Configuration section name (root: <c>Cnas:PeakHourGate</c>).</summary>
    public const string SectionName = "Cnas:PeakHourGate";

    /// <summary>
    /// Local-time inclusive start hour of the off-peak window (0..23, default
    /// <c>22</c> = 22:00). Combined with <see cref="OffPeakEndLocalHour"/>
    /// the window may wrap midnight (start &gt; end), which the gate treats
    /// as <c>[start..23:59] ∪ [00:00..end]</c>.
    /// </summary>
    public int OffPeakStartLocalHour { get; set; } = 22;

    /// <summary>
    /// Local-time inclusive end hour of the off-peak window (0..23, default
    /// <c>6</c> = 06:59). The off-peak interval is the closed interval
    /// <c>[OffPeakStartLocalHour, OffPeakEndLocalHour]</c>, possibly wrapping
    /// midnight when start &gt; end.
    /// </summary>
    public int OffPeakEndLocalHour { get; set; } = 6;

    /// <summary>
    /// When <c>true</c>, the gate always returns
    /// <see cref="Cnas.Ps.Application.Scheduling.PeakHourGateDecision.Allow"/>
    /// regardless of profile or current time. Flipped by the admin override
    /// endpoint for emergency manual runs of <c>OffPeakOnly</c> jobs.
    /// </summary>
    public bool GlobalOverride { get; set; }
}
