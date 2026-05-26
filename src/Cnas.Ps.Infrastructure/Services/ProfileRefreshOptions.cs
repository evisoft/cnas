namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0363 — operator-facing options governing the external-data refresh pipeline. Bound
/// from <c>Cnas:ProfileRefresh</c> in <c>appsettings</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scheduled-job gating.</b> The scheduled batch job (<c>ProfileRefreshScheduledJob</c>)
/// is intentionally NOT registered with Quartz unless
/// <see cref="EnableScheduledRefresh"/> is <c>true</c>. The implementation lives in the
/// codebase so it can be tested in isolation but production stays opt-in until the
/// NDA-gated RSP/RSUD/SI SFS WSDLs are in hand.
/// </para>
/// <para>
/// <b>Per-run batch cap.</b> <see cref="MaxContributorsPerRun"/> bounds the per-tick
/// work so a misconfigured cron interval cannot stampede the upstream registries.
/// </para>
/// </remarks>
public sealed class ProfileRefreshOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Cnas:ProfileRefresh";

    /// <summary>
    /// Master switch for the scheduled refresh job. Default <c>false</c> — operators
    /// must explicitly opt in once the upstream integrations are wired.
    /// </summary>
    public bool EnableScheduledRefresh { get; set; }

    /// <summary>
    /// Cron schedule for the batch job in Quartz format. Default <c>0 5 3 * * ?</c> —
    /// daily at 03:05 UTC, picked to minimise overlap with upstream peak hours.
    /// </summary>
    public string CronSchedule { get; set; } = "0 5 3 * * ?";

    /// <summary>
    /// Maximum number of contributors the batch job processes in a single tick. Default
    /// <c>1000</c>; tune up only after the upstream registries can sustain it.
    /// </summary>
    public int MaxContributorsPerRun { get; set; } = 1000;
}
