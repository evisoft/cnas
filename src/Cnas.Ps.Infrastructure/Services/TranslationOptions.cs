namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0210 / TOR UI 007 / CF 17.16 — tunable parameters for the translation resolver
/// + the background cache-refresh job. Bound from <c>Cnas:Translation</c> so
/// operators can adjust the cadence without redeploying.
/// </summary>
public sealed class TranslationOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:Translation";

    /// <summary>
    /// Refresh cadence in seconds for the in-memory translation snapshot. Defaults
    /// to 60 seconds, matching the R0182 audit-policy resolver cadence. The cache
    /// is also invalidated synchronously on every CRUD mutation so the background
    /// cadence is the ceiling on staleness, not the typical case.
    /// </summary>
    public int RefreshIntervalSeconds { get; init; } = 60;
}

/// <summary>
/// R0225 / TOR UI 015 — tunable parameters for the help-topic resolver + cache job.
/// </summary>
public sealed class HelpOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:Help";

    /// <summary>Refresh cadence in seconds; defaults to 60 s.</summary>
    public int RefreshIntervalSeconds { get; init; } = 60;
}
