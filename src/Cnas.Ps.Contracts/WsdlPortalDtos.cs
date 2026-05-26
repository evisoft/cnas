using System.Collections.Generic;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R2164 / TOR §15.4 INT 005 — listing row returned from the WSDL portal landing page
/// (<c>GET /api/wsdl-portal</c>). Carries the controller name + the relative URL of the
/// generated WSDL document so a downstream SOAP client can navigate the catalogue
/// without already knowing the route.
/// </summary>
/// <param name="ControllerName">Stable controller class-name suffix (the <c>Controller</c> suffix is stripped, e.g. <c>Health</c> for <c>HealthController</c>).</param>
/// <param name="WsdlUrl">Relative URL of the WSDL document for the controller (e.g. <c>/api/wsdl-portal/Health.wsdl</c>).</param>
/// <param name="OperationCount">Count of action methods exposed by the controller — informational; helps SOAP consumers gauge surface area.</param>
public sealed record WsdlListingDto(
    string ControllerName,
    string WsdlUrl,
    int OperationCount);

/// <summary>
/// R2164 / TOR §15.4 INT 005 — full WSDL document descriptor returned from
/// <c>GET /api/wsdl-portal/{controllerName}.wsdl</c>. The <see cref="WsdlXml"/> string is
/// a well-formed WSDL 1.1 document (verifiable via <c>XDocument.Parse</c>) sufficient to
/// stub a SOAP compatibility layer over the controller's action surface.
/// </summary>
/// <param name="ControllerName">Stable controller name (no <c>Controller</c> suffix).</param>
/// <param name="WsdlXml">Generated WSDL 1.1 XML body.</param>
/// <param name="Operations">Action method names exposed as SOAP operations.</param>
public sealed record WsdlDescriptorDto(
    string ControllerName,
    string WsdlXml,
    IReadOnlyList<string> Operations);
