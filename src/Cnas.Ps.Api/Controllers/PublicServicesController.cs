using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0511 / R0512 / R0513 / TOR CF 02.01 — public anonymous service endpoints
/// for medical-certificate status, online-appointment booking, and CNAS-code
/// extraction. The controller is unauthenticated (no <c>[Authorize]</c>);
/// CAPTCHA + IDNP validation happen inside each service so the per-action
/// failure modes can be tested independently and surfaced as stable error
/// codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defensive surface.</b> All anonymous endpoints participate in the
/// <see cref="RateLimitingPolicies.Anonymous"/> rate-limit partition (per-IP
/// bucket — CLAUDE.md §5.3). The CAPTCHA token travels in the request body
/// (rather than the <c>X-Captcha-Token</c> header used by <c>PublicController</c>)
/// so the service-level Result&lt;T&gt; pipeline owns the verification — see
/// each service for the stable error-code mapping.
/// </para>
/// <para>
/// <b>No PII echo.</b> Response bodies never contain raw IDNP, full name, or
/// any other PII. R0511 returns lifecycle metadata only; R0512 returns a
/// rendered URL; R0513 returns either the synthesized CNAS code or a single
/// boolean <c>false</c> bucket. See the DTOs for the field-by-field
/// rationale.
/// </para>
/// </remarks>
[ApiController]
[EnableRateLimiting(RateLimitingPolicies.Anonymous)]
[Route("api/public")]
public sealed class PublicServicesController : ControllerBase
{
    private readonly IMedicalCertificateStatusService _medicalCertificate;
    private readonly IOnlineAppointmentBookingService _appointments;
    private readonly IExtractCnasCodeService _extractCnasCode;

    /// <summary>Constructs the controller with its three service collaborators.</summary>
    /// <param name="medicalCertificate">R0511 service.</param>
    /// <param name="appointments">R0512 service.</param>
    /// <param name="extractCnasCode">R0513 service.</param>
    public PublicServicesController(
        IMedicalCertificateStatusService medicalCertificate,
        IOnlineAppointmentBookingService appointments,
        IExtractCnasCodeService extractCnasCode)
    {
        ArgumentNullException.ThrowIfNull(medicalCertificate);
        ArgumentNullException.ThrowIfNull(appointments);
        ArgumentNullException.ThrowIfNull(extractCnasCode);
        _medicalCertificate = medicalCertificate;
        _appointments = appointments;
        _extractCnasCode = extractCnasCode;
    }

    /// <summary>
    /// R0511 — anonymous lookup of a medical-certificate's current status.
    /// </summary>
    /// <param name="body">Lookup envelope: certificate number + IDNP + DOB + CAPTCHA token.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 200 OK with the sanitised <see cref="MedicalCertificateStatusDto"/>; or
    /// a ProblemDetails 400 / 503 carrying the stable error code from the
    /// service-level Result.
    /// </returns>
    [HttpPost("medical-certificate-status")]
    public async Task<ActionResult<MedicalCertificateStatusDto>> LookupMedicalCertificateAsync(
        [FromBody] MedicalCertificateLookupDto body,
        CancellationToken cancellationToken = default)
    {
        var result = await _medicalCertificate
            .LookupAsync(body, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0512 — anonymous directory of CNAS regional branches plus the
    /// system-wide deep-link template.
    /// </summary>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 OK with <see cref="AppointmentBookingDirectoryDto"/>.</returns>
    [HttpGet("appointments/directory")]
    public async Task<ActionResult<AppointmentBookingDirectoryDto>> GetAppointmentDirectoryAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _appointments
            .GetDirectoryAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0512 — resolves the deep-link URL for a selected branch and writes one
    /// audit row so analytics can chart click-through volume.
    /// </summary>
    /// <param name="branchCode">Stable branch code chosen by the citizen.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 OK with <see cref="AppointmentDeepLinkDto"/>; 404 when the branch is unknown / inactive.</returns>
    [HttpPost("appointments/{branchCode}/resolve")]
    public async Task<ActionResult<AppointmentDeepLinkDto>> ResolveAppointmentDeepLinkAsync(
        string branchCode,
        CancellationToken cancellationToken = default)
    {
        var result = await _appointments
            .ResolveDeepLinkAsync(branchCode, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0513 — anonymous "find my CNAS code" lookup.
    /// </summary>
    /// <param name="body">Lookup envelope: IDNP + DOB + CAPTCHA token.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// 200 OK with the sanitised <see cref="ExtractCnasCodeResultDto"/>; or a
    /// ProblemDetails 400 carrying the stable error code from the
    /// service-level Result.
    /// </returns>
    [HttpPost("extract-cnas-code")]
    public async Task<ActionResult<ExtractCnasCodeResultDto>> ExtractCnasCodeAsync(
        [FromBody] ExtractCnasCodeLookupDto body,
        CancellationToken cancellationToken = default)
    {
        var result = await _extractCnasCode
            .LookupAsync(body, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Maps a service-level <see cref="Result"/> failure to the appropriate
    /// HTTP status. <c>BRANCH_NOT_FOUND</c> / <see cref="ErrorCodes.NotFound"/>
    /// → 404; <see cref="ErrorCodes.CaptchaProviderUnreachable"/> → 503;
    /// everything else → 400.
    /// </summary>
    /// <param name="errorCode">Stable error code from the service.</param>
    /// <param name="errorMessage">Human-readable message (logged but not echoed to anonymous callers if PII-bearing).</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ObjectResult MapFailure(string? errorCode, string? errorMessage)
    {
        var status = errorCode switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.CaptchaProviderUnreachable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };

        var problem = new ProblemDetails
        {
            Status = status,
            Title = "Public service request rejected.",
            Detail = errorMessage,
        };
        problem.Extensions["errorCode"] = errorCode;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
