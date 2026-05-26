using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R2164 / TOR §15.4 INT 005 — WSDL portal façade. Exposes auto-generated WSDL 1.1
/// descriptors covering the REST API surface so downstream legacy SOAP clients can
/// shim a compatibility layer without waiting on a hand-written WSDL artefact.
/// </summary>
/// <remarks>
/// <para>
/// INT 005 in the TOR asks for both an OpenAPI portal and a WSDL portal so that
/// integrators on either modern (REST/OpenAPI) or legacy (SOAP/WSDL) tech stacks can
/// discover the system's surface. OpenAPI is already exposed by
/// <c>app.MapOpenApi()</c>; this service closes the WSDL half by reflecting over the
/// API project's controllers and emitting one WSDL 1.1 document per controller.
/// </para>
/// <para>
/// The generated WSDL is minimal by design — operation names and a placeholder string
/// schema — sufficient for SOAP-client tooling that needs <em>a</em> WSDL stub to bind
/// against. The portal does NOT claim semantic fidelity with the underlying REST
/// surface; bodies, status codes, and error shapes remain governed by the REST/OpenAPI
/// contract.
/// </para>
/// </remarks>
public interface IWsdlPortalService
{
    /// <summary>
    /// Returns a WSDL 1.1 descriptor for the controller identified by
    /// <paramref name="controllerName"/>.
    /// </summary>
    /// <param name="controllerName">Stable controller name (no <c>Controller</c> suffix, case-insensitive).</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with a WSDL descriptor on success;
    /// <see cref="ErrorCodes.NotFound"/> when the controller is unknown.
    /// </returns>
    Task<Result<WsdlDescriptorDto>> GetForControllerAsync(
        string controllerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates every controller available through the portal as a compact listing of
    /// <see cref="WsdlListingDto"/> rows. Used by the portal landing page.
    /// </summary>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>Listing rows ordered by controller name; never <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result<IReadOnlyList<WsdlListingDto>>> ListAsync(
        CancellationToken cancellationToken = default);
}
