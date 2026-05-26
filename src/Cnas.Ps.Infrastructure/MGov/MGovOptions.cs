namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// Configuration for the suite of shared Moldovan government platform services
/// (MPass, MSign, MPay, MConnect, MNotify, MLog). Bound from <c>MGov:*</c>.
/// </summary>
/// <remarks>
/// All <c>*BaseUrl</c> values default to empty. A client whose base URL is empty refuses
/// to make HTTP calls and returns <c>INTERNAL_ERROR</c>; this guards local development
/// environments against accidentally hitting production MGov endpoints when a secret is
/// forgotten. Bearer tokens come from the secret store and are sent verbatim in the
/// <c>Authorization</c> header — never log them.
/// </remarks>
public sealed class MGovOptions
{
    /// <summary>Section name in app settings.</summary>
    public const string SectionName = "MGov";

    /// <summary>MPass OIDC issuer URL.</summary>
    public string MPassIssuer { get; set; } = string.Empty;

    /// <summary>MPass client id allocated by AGE.</summary>
    public string MPassClientId { get; set; } = string.Empty;

    /// <summary>MPass client secret (loaded from secret store).</summary>
    public string MPassClientSecret { get; set; } = string.Empty;

    /// <summary>MSign service endpoint (REST).</summary>
    public string MSignBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Legacy static bearer token previously presented to MSign in the
    /// <c>Authorization</c> header. Retained for backwards compatibility with
    /// environments whose <c>appsettings.json</c> still binds this value, but the
    /// adapter no longer sends an <c>Authorization</c> header — the real MEGA protocol
    /// uses mTLS (X.509 client certificate) exclusively. See
    /// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MSign".
    /// </summary>
    [Obsolete("MSign uses mTLS exclusively; the Bearer header is no longer sent. Configure the client certificate under Cnas:MGov:Mtls:Certificates:msign instead.")]
    public string MSignBearer { get; set; } = string.Empty;

    /// <summary>MPay service endpoint.</summary>
    public string MPayBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Legacy static bearer token previously presented to MPay in the
    /// <c>Authorization</c> header. Retained for backwards compatibility with
    /// environments whose <c>appsettings.json</c> still binds this value, but the
    /// adapter no longer sends an <c>Authorization</c> header — the real MEGA protocol
    /// uses mTLS (X.509 client certificate) plus an X.509 XML-DSig signature embedded
    /// in the SOAP envelope. See <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MPay".
    /// </summary>
    [Obsolete("MPay uses mTLS exclusively; the Bearer header is no longer sent. Configure the client certificate under Cnas:MGov:Mtls:Certificates:mpay instead.")]
    public string MPayBearer { get; set; } = string.Empty;

    /// <summary>MConnect interoperability platform endpoint.</summary>
    public string MConnectBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Legacy static bearer token previously presented to MConnect in the
    /// <c>Authorization</c> header. Retained for backwards compatibility with
    /// environments whose <c>appsettings.json</c> still binds this value, but the
    /// adapter no longer sends an <c>Authorization</c> header — the real MEGA protocol
    /// is a SOAP envelope over mTLS (X.509 client certificate) plus an X.509 XML-DSig
    /// signature embedded in a <c>wsse:Security</c> header inside the envelope. See
    /// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MConnect".
    /// </summary>
    [Obsolete("MConnect uses mTLS exclusively; the Bearer header is no longer sent. Configure the client certificate under Cnas:MGov:Mtls:Certificates:mconnect instead.")]
    public string MConnectBearer { get; set; } = string.Empty;

    /// <summary>MNotify dispatch endpoint.</summary>
    public string MNotifyBaseUrl { get; set; } = string.Empty;

    /// <summary>Static bearer token presented to MNotify in the <c>Authorization</c> header.</summary>
    public string MNotifyBearer { get; set; } = string.Empty;

    /// <summary>MLog journaling endpoint.</summary>
    public string MLogBaseUrl { get; set; } = string.Empty;

    /// <summary>Static bearer token presented to MLog in the <c>Authorization</c> header.</summary>
    public string MLogBearer { get; set; } = string.Empty;

    /// <summary>MPower powers-of-attorney service endpoint.</summary>
    public string MPowerBaseUrl { get; set; } = string.Empty;

    /// <summary>Static bearer token presented to MPower in the <c>Authorization</c> header.</summary>
    public string MPowerBearer { get; set; } = string.Empty;

    /// <summary>
    /// MConnect Events service endpoint (CloudEvents v1.0 producer / WebSocket consumer).
    /// Producer uses <c>POST {base}/ce/produce/event</c> and <c>POST {base}/ce/produce/events</c>;
    /// consumer connects to <c>wss://{base}/ce/consume/ws</c> with sub-protocol
    /// <c>cloudevents.json</c>. Empty string disables the integration (local dev safety).
    /// </summary>
    public string MConnectEventsBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Legacy static bearer token previously presented to MConnect Events in the
    /// <c>Authorization</c> header. Retained for backwards compatibility with
    /// environments whose <c>appsettings.json</c> still binds this value, but the
    /// producer no longer sends an <c>Authorization</c> header — the real MEGA protocol
    /// uses mTLS (X.509 client certificate) exclusively. See
    /// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MConnect Events".
    /// </summary>
    [Obsolete("Replaced by mTLS via ICertificateStore. Configure Cnas:MGov:Mtls:Certificates:mconnect-events instead.")]
    public string MConnectEventsBearer { get; set; } = string.Empty;

    /// <summary>
    /// MDocs managed-document storage service endpoint. Empty string disables the
    /// integration (local dev safety) — the client returns
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.Internal"/> on every call until configured.
    /// </summary>
    public string MDocsBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Legacy static bearer token previously presented to MDocs in the
    /// <c>Authorization</c> header. Retained for backwards compatibility with
    /// environments whose <c>appsettings.json</c> still binds this value, but the
    /// client no longer sends an <c>Authorization</c> header — the real MEGA protocol
    /// uses mTLS (X.509 client certificate) exclusively. See
    /// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MDocs".
    /// </summary>
    [Obsolete("Replaced by mTLS via ICertificateStore. Configure Cnas:MGov:Mtls:Certificates:mdocs instead.")]
    public string MDocsBearer { get; set; } = string.Empty;

    /// <summary>
    /// R0104 / TOR CF 14.03 — operator opt-in for the MConnect partner-direct fallback
    /// path. Defaults to <c>false</c> (production-safe) so the fallback is never
    /// invoked unless the operator explicitly enables it AND the per-partner NDA flag
    /// (see <see cref="PartnerHasNdaByCode"/>) is set to <c>true</c>.
    /// </summary>
    public bool AllowFallback { get; set; }

    /// <summary>
    /// R0104 / TOR CF 14.03 — per-partner NDA gate. Each key is a partner system code
    /// (e.g. <c>RSP</c>, <c>SFS</c>, <c>RSUD</c>) and the value indicates whether CNAS
    /// holds an NDA with that partner authorising the direct-API fallback. Partners
    /// absent from the dictionary are treated as <c>false</c> (no NDA, no fallback).
    /// </summary>
    /// <remarks>
    /// Bound from configuration via <c>Cnas:MGov:PartnerHasNdaByCode:&lt;PARTNER&gt; = true</c>.
    /// The dictionary is case-sensitive on the wire; production config uses uppercase
    /// partner codes to match the typed-facade naming.
    /// </remarks>
    public IDictionary<string, bool> PartnerHasNdaByCode { get; set; }
        = new Dictionary<string, bool>(StringComparer.Ordinal);
}
