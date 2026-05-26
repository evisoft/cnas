namespace Cnas.Ps.Infrastructure.Services.MessageBus;

/// <summary>
/// R0117 / CF 14.11 — configuration for the Portalul guvernamental de date (PGD) publisher.
/// Bound from <c>Cnas:Pgd</c> in app settings.
/// </summary>
/// <remarks>
/// <para>
/// PGD is the Moldovan government open-data portal. CNAS publishes public-interest
/// datasets so citizens and downstream systems can consume them through the canonical
/// portal rather than scraping CNAS-specific UIs.
/// </para>
/// <para>
/// <see cref="BaseUrl"/> defaults to empty. When empty the publisher refuses to make
/// HTTP calls and returns a deterministic
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.PgdNotConfigured"/> failure. This guards
/// local development and CI from accidentally pushing test datasets to the production
/// portal when a secret is forgotten — mirroring the safety guard on the MCabinet
/// publisher.
/// </para>
/// </remarks>
public sealed record PgdPublisherOptions
{
    /// <summary>Section name in app settings.</summary>
    public const string SectionName = "Cnas:Pgd";

    /// <summary>
    /// PGD REST API base URL (e.g. <c>https://date.gov.md</c>). Empty in local
    /// development disables the integration without throwing.
    /// </summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Optional API key for upstream auth. Empty by default; the publisher sends no
    /// <c>Authorization</c> header when blank. Real production deployment uses mTLS via
    /// the shared certificate store rather than this field.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Stable CNAS system code identifying the publisher to PGD. Defaults to
    /// <c>CNAS-PS</c>. Must remain stable across deployments — changing it would
    /// orphan every previously-published dataset on the portal.
    /// </summary>
    public string SystemCode { get; init; } = "CNAS-PS";
}
