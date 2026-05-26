namespace Cnas.Ps.Application.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — bound options envelope for the daily Treasury feed
/// adapter. Bound from the <c>TreasuryFeed</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default source.</b> This iteration ships <c>InMemoryTest</c> as the
/// default so the chart starts safely without an external dependency.
/// Production operators enable the HTTPS path via configuration; the SFTP
/// path is introduced in a later iteration.
/// </para>
/// </remarks>
public sealed class TreasuryFeedOptions
{
    /// <summary>Well-known configuration section name (<c>TreasuryFeed</c>).</summary>
    public const string SectionName = "TreasuryFeed";

    /// <summary>
    /// Stable identifier of the configured source ("InMemoryTest" / "Https" /
    /// "Sftp" / "Manual"). Today only <c>InMemoryTest</c> is wired by default;
    /// operators flip to <c>Https</c> via the host's
    /// <c>UseHttpsTreasuryFeedSource()</c> extension.
    /// </summary>
    public string SourceKind { get; set; } = "InMemoryTest";

    /// <summary>
    /// HTTPS base URL used by the placeholder <c>HttpsTreasuryFeedSource</c>.
    /// Blank by default — when blank the source returns a deterministic
    /// <c>TREASURY_FEED.NOT_CONFIGURED</c> failure so production deployments
    /// that forget to wire the URL fail loudly rather than silently.
    /// </summary>
    public string HttpsBaseUrl { get; set; } = string.Empty;
}
