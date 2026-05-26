namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// Configuration for the MCabinet citizen-portal publisher. Bound from
/// <c>Cnas:MCabinet</c> in app settings.
/// </summary>
/// <remarks>
/// <para>
/// MCabinet is the Moldovan government's unified citizen dashboard
/// (<c>mcabinet.gov.md</c>). CNAS publishes "dossier event" cards there so that the
/// citizen sees their pension-application status in one place. The de-duplication tuple
/// upstream is <c>(<see cref="SystemCode"/>, externalId)</c>, so <see cref="SystemCode"/>
/// must remain stable for the lifetime of the integration — changing it would orphan
/// every previously-published card.
/// </para>
/// <para>
/// <see cref="BaseUrl"/> defaults to empty; when empty the publisher refuses to make
/// HTTP calls and returns
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.MCabinetPublishFailed"/>. This guards local
/// development environments against accidentally hitting the production MCabinet
/// endpoint when a secret is forgotten.
/// </para>
/// </remarks>
public sealed record MCabinetOptions
{
    /// <summary>Section name in app settings.</summary>
    public const string SectionName = "Cnas:MCabinet";

    /// <summary>
    /// MCabinet REST API base URL (e.g. <c>https://mcabinet.gov.md</c>). Empty in local
    /// development disables the integration without throwing.
    /// </summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Legacy static bearer token previously presented to MCabinet in the
    /// <c>Authorization</c> header. Retained for backwards compatibility with
    /// environments whose <c>appsettings.json</c> still binds this value, but the
    /// publisher no longer sends an <c>Authorization</c> header — the real MEGA
    /// protocol uses mTLS (X.509 client certificate) exclusively.
    /// </summary>
    [Obsolete("Replaced by mTLS via ICertificateStore. Configure Cnas:MGov:Mtls:Certificates:mcabinet instead.")]
    public string Bearer { get; init; } = string.Empty;

    /// <summary>
    /// CNAS system code used as the producer half of MCabinet's de-duplication tuple
    /// <c>(systemCode, externalId)</c>. Defaults to <c>CNAS-PS</c>. Must remain stable
    /// across deployments — changing it orphans every previously-published card.
    /// </summary>
    public string SystemCode { get; init; } = "CNAS-PS";
}
