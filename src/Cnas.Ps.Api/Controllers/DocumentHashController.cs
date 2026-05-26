using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Documents;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0341 / TOR CF 11.06 — admin REST surface for document hash verification.
/// Restricted to the <see cref="AuthorizationComposition.CnasAdmin"/> policy
/// because a mismatch is a tamper / corruption signal that operators must
/// investigate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/admin/documents/{sqid}/verify-hash</c> — runs a verification + emits the audit row.</item>
/// </list>
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/documents")]
public sealed class DocumentHashController : ControllerBase
{
    private readonly IDocumentHashVerifier _verifier;

    /// <summary>Constructs the controller.</summary>
    /// <param name="verifier">Document hash-verifier façade.</param>
    public DocumentHashController(IDocumentHashVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        _verifier = verifier;
    }

    /// <summary>
    /// Verifies that the stored bytes for the supplied document still hash to
    /// the recorded SHA-256 digest. Emits an audit row on every call.
    /// </summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the outcome DTO; 400 / 404 on failure.</returns>
    [HttpPost("{sqid}/verify-hash")]
    public async Task<ActionResult<DocumentHashVerificationDto>> VerifyHashAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _verifier.VerifyAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<DocumentHashVerificationDto>(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Maps failure error codes to HTTP statuses.</summary>
    /// <typeparam name="T">Success DTO type.</typeparam>
    /// <param name="errorCode">Stable error code.</param>
    /// <param name="errorMessage">Human description.</param>
    /// <returns>Action result with the right status.</returns>
    private ActionResult<T> MapFailure<T>(string errorCode, string errorMessage)
        => errorCode switch
        {
            ErrorCodes.InvalidSqid => BadRequest(new { error = errorCode, message = errorMessage }),
            ErrorCodes.NotFound => NotFound(new { error = errorCode, message = errorMessage }),
            ErrorCodes.FileUnavailable => NotFound(new { error = errorCode, message = errorMessage }),
            _ => StatusCode(500, new { error = errorCode, message = errorMessage }),
        };
}
