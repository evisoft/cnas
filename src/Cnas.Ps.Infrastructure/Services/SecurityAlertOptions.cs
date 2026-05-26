namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Configuration surface for the security-alert evaluator background job
/// (R0189 / SEC 048). Bound from <c>Cnas:SecurityAlerts</c>. All fields ship with
/// production-safe defaults: the evaluator is enabled out of the box because the
/// migration seeds four common rules (failed-login burst, account locked, refresh
/// reuse, admin elevation) — leaving alerting off by default would silently drop a
/// security signal that operators expect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enabled by default.</b> Unlike <c>SiemExporterOptions</c> (R0190) which ships
/// disabled because most environments lack a SIEM endpoint, this evaluator's only
/// outputs are in-app notifications + audit rows — both of which the surrounding
/// system already supports. Leaving it on means the seed rules immediately surface
/// the high-value alerts on day one. Operators may still flip
/// <see cref="Enabled"/> off if they want to vendor an external rule engine.
/// </para>
/// <para>
/// <b>Stable cron seam.</b> <see cref="Cron"/> is a Quartz cron expression
/// (<c>"0 */1 * * * ?"</c> = every minute on the second boundary). The
/// QuartzComposition wiring currently hard-codes the minute cadence; this field is
/// the documented seam for a future per-environment override and is kept on the
/// options surface so the contract is stable today even before the wiring catches
/// up — mirrors the R0190 <c>TODO[r0189-cron]</c> pattern.
/// </para>
/// <para>
/// <b>In-memory candidate cap.</b> <see cref="MaxRowsPerWindow"/> bounds the per-fire
/// audit-row scan so a pathological burst (e.g. tens of thousands of events in a
/// minute) cannot wedge the evaluator on a multi-megabyte materialisation. Default
/// 5000 covers the seed rules' window sizes comfortably; if operators add longer
/// windows or noisier patterns they may raise the cap. Hitting the cap means the
/// scan was truncated and some matched rows may not contribute to the count —
/// operators should investigate the underlying volume rather than blindly raising
/// the cap.
/// </para>
/// </remarks>
public sealed class SecurityAlertOptions
{
    /// <summary>Configuration section name — <c>Cnas:SecurityAlerts</c>.</summary>
    public const string SectionName = "Cnas:SecurityAlerts";

    /// <summary>
    /// Master switch. When <c>false</c> the evaluator background job is a no-op and
    /// does not touch the database. Defaults to <c>true</c> because the seed rules
    /// cover the common SEC 048 cases and the alert outputs (in-app notification +
    /// audit row) do not require external infrastructure.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Quartz cron expression governing the evaluator cadence. Default
    /// <c>"0 */1 * * * ?"</c> = every minute on the second boundary. The wiring
    /// currently hard-codes this cadence — see class remarks for the seam caveat.
    /// </summary>
    public string Cron { get; init; } = "0 */1 * * * ?";

    /// <summary>
    /// Maximum number of audit rows materialised per evaluator iteration. Bounds the
    /// in-memory regex-matching pass so a pathological event burst cannot wedge the
    /// job for minutes at a time. Defaults to 5000 — leaves the evaluator comfortable
    /// headroom over the seed rules' 60-300 s windows under normal event rates.
    /// </summary>
    public int MaxRowsPerWindow { get; init; } = 5000;
}
