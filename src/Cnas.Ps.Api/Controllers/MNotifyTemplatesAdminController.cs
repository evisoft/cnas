using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Security;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0115 / TOR CF 14.07 — admin REST surface for the MNotify template registry.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/mnotify/templates")]
public sealed class MNotifyTemplatesAdminController : ControllerBase
{
    private readonly IMNotifyTemplateService _svc;
    private readonly IValidator<MNotifyTemplateInputDto> _validator;

    /// <summary>Constructs the controller.</summary>
    /// <param name="svc">Template registry service.</param>
    /// <param name="validator">FluentValidation validator.</param>
    public MNotifyTemplatesAdminController(
        IMNotifyTemplateService svc,
        IValidator<MNotifyTemplateInputDto> validator)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(validator);
        _svc = svc;
        _validator = validator;
    }

    /// <summary>R0115 — list templates.</summary>
    /// <param name="includeInactive">When <c>true</c> deactivated rows are included.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the DTOs on success.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MNotifyTemplateDto>>> ListAsync(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ListAsync(includeInactive, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.ErrorMessage);
    }

    /// <summary>
    /// R0115 — create a new template. Splits cleanly from
    /// <see cref="UpdateAsync"/> so the REST surface is unambiguous: POST is
    /// for "make a new resource", PUT is for "replace an existing one".
    /// </summary>
    /// <param name="input">Template payload (the <c>Code</c> is the natural key).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>201 Created</c> with a <c>Location</c> header pointing at the new
    /// resource on success; <c>400 Bad Request</c> on invalid input;
    /// <c>409 Conflict</c> when a row with the same <c>Code</c> already exists
    /// (use PUT to update it).
    /// </returns>
    [HttpPost("")]
    public async Task<ActionResult<MNotifyTemplateDto>> CreateAsync(
        [FromBody] MNotifyTemplateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Reject creates that collide with an existing Code so the POST verb's
        // "make new" semantics stay honest. We use the list-by-code probe
        // before calling Upsert; the Upsert call itself is idempotent on
        // Code, but the caller asked for a CREATE.
        var existing = await _svc.ListAsync(includeInactive: true, cancellationToken).ConfigureAwait(false);
        if (existing.IsSuccess
            && existing.Value!.Any(t => string.Equals(t.Code, input.Code, StringComparison.Ordinal)))
        {
            return Problem(
                $"MNotify template with Code='{input.Code}' already exists — use PUT to update it.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var result = await _svc.UpsertAsync(input, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Problem(result.ErrorMessage);
        }
        var dto = result.Value!;
        return CreatedAtAction(
            actionName: nameof(GetAsync),
            routeValues: new { sqid = dto.Sqid },
            value: dto);
    }

    /// <summary>
    /// R0115 — fetch a single template by Sqid. Backs the <c>Location</c>
    /// header that <see cref="CreateAsync"/> stamps on the 201 response.
    /// </summary>
    /// <param name="sqid">Sqid-encoded template id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>200</c> with the DTO; <c>404</c> when unknown.</returns>
    [HttpGet("{sqid}")]
    public async Task<ActionResult<MNotifyTemplateDto>> GetAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }
        return result.ErrorCode == ErrorCodes.NotFound
            ? NotFound()
            : Problem(result.ErrorMessage);
    }

    /// <summary>
    /// R0115 — update an existing template addressed by Sqid. Returns
    /// <c>404 Not Found</c> when the row is unknown so callers can distinguish
    /// "update a known resource" from "create a new one" cleanly.
    /// </summary>
    /// <param name="sqid">Sqid-encoded template id.</param>
    /// <param name="input">Template payload — <c>Code</c> MUST match the row addressed by <paramref name="sqid"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>200 OK</c> with the persisted DTO on success; <c>400 Bad Request</c>
    /// on invalid input or when the payload's <c>Code</c> disagrees with the
    /// targeted row; <c>404 Not Found</c> when the row does not exist.
    /// </returns>
    [HttpPut("{sqid}")]
    public async Task<ActionResult<MNotifyTemplateDto>> UpdateAsync(
        [FromRoute] string sqid,
        [FromBody] MNotifyTemplateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Problem(
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Resolve the row by Sqid first so a missing row surfaces as 404 rather
        // than silently turning into a CREATE through the upsert path.
        var existing = await _svc.GetAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return existing.ErrorCode == ErrorCodes.NotFound
                ? NotFound()
                : Problem(existing.ErrorMessage);
        }
        if (!string.Equals(existing.Value!.Code, input.Code, StringComparison.Ordinal))
        {
            return Problem(
                $"Code mismatch: payload='{input.Code}' but row at sqid='{sqid}' has Code='{existing.Value.Code}'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _svc.UpsertAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.ErrorMessage);
    }

    /// <summary>R0115 — deactivate a template.</summary>
    /// <param name="sqid">Sqid-encoded template id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 on success, 404 if unknown.</returns>
    [HttpDelete("{sqid}")]
    public async Task<IActionResult> DeactivateAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.DeactivateAsync(sqid, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return NoContent();
        }
        return result.ErrorCode == ErrorCodes.NotFound
            ? NotFound()
            : Problem(result.ErrorMessage);
    }
}

/// <summary>
/// R0115 / TOR CF 14.07 — public webhook endpoint accepting MNotify bounce /
/// delivery-failure callbacks. Anonymous-allowed (the gateway is identified by
/// an HMAC header validated upstream by the reverse proxy / signature
/// middleware).
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingPolicies.Anonymous)]
[Route("api/webhooks/mnotify")]
public sealed class MNotifyBounceWebhookController : ControllerBase
{
    private readonly IMNotifyBounceHandler _handler;
    private readonly ICallbackSignatureVerifier _signatureVerifier;

    /// <summary>Constructs the controller.</summary>
    /// <param name="handler">Bounce handler.</param>
    /// <param name="signatureVerifier">HMAC verifier for the anonymous webhook surface.</param>
    public MNotifyBounceWebhookController(
        IMNotifyBounceHandler handler,
        ICallbackSignatureVerifier signatureVerifier)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(signatureVerifier);
        _handler = handler;
        _signatureVerifier = signatureVerifier;
    }

    /// <summary>R0115 — accepts a single bounce callback.</summary>
    /// <param name="payload">Webhook payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 on success; 404 when the upstream reference is unknown.</returns>
    [HttpPost("bounce")]
    public async Task<IActionResult> BounceAsync(
        [FromBody] MNotifyBounceWebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var signature = _signatureVerifier.Verify(
            CallbackSignatureProvider.MNotify,
            CanonicalBouncePayload(payload),
            Request.Headers);
        if (!signature.IsSuccess)
        {
            return Unauthorized(new { error = "invalid_callback_signature", detail = signature.ErrorMessage });
        }

        var result = await _handler.HandleBounceAsync(payload, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return NoContent();
        }
        return result.ErrorCode == ErrorCodes.NotFound
            ? NotFound()
            : Problem(result.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
    }

    private static string CanonicalBouncePayload(MNotifyBounceWebhookPayload payload) =>
        string.Join(
            '\n',
            "POST",
            "/api/webhooks/mnotify/bounce",
            payload.NotificationReference,
            payload.BounceCode,
            payload.OccurredAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}
