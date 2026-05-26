using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// UC13 — Self-service profile REST surface. Any authenticated caller can read or update
/// their own profile; the caller is resolved server-side via <c>ICallerContext</c>, so the
/// route does not carry an id. Mutations are restricted to the contact + i18n preference
/// fields (the immutable identity attributes — IDNP, IDs, roles, IsActive, CreatedAtUtc —
/// are NOT exposed by <see cref="ProfileUpdateInput"/> per CLAUDE.md §2.4).
/// </summary>
/// <param name="profiles">Underlying profile service.</param>
/// <param name="languageValidator">
/// R0211 / TOR UI 003 — validator for the thin <see cref="ProfileLanguageInputDto"/>
/// payload consumed by <c>PUT /api/profile/language</c>.
/// </param>
/// <param name="contactValidator">
/// R0361 / UC13 — validator for the <see cref="ProfileContactInput"/> payload
/// consumed by <c>PUT /api/profile/contact</c> from the citizen self-service
/// <c>MyProfile.razor</c> page.
/// </param>
[ApiController]
[Authorize]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/profile")]
public sealed class ProfileController(
    IProfileService profiles,
    IValidator<ProfileLanguageInputDto> languageValidator,
    IValidator<ProfileContactInput> contactValidator) : ControllerBase
{
    private readonly IProfileService _profiles = profiles;
    private readonly IValidator<ProfileLanguageInputDto> _languageValidator = languageValidator;
    private readonly IValidator<ProfileContactInput> _contactValidator = contactValidator;

    /// <summary>
    /// Reads the caller's profile. The caller is resolved by the underlying service via
    /// <c>ICallerContext.UserId</c> — the route deliberately carries no id, preventing a
    /// caller from probing other users' profiles by guessing route values.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the profile; 404 when the underlying user row has been deactivated;
    /// 401 ProblemDetails when the caller is anonymous (defense in depth — normally
    /// <c>[Authorize]</c> blocks anonymous access at the pipeline).
    /// </returns>
    [HttpGet("me")]
    public async Task<ActionResult<ProfileOutput>> GetMineAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _profiles.GetMineAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<ProfileOutput>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Updates the caller's contact fields and language preference. PUT — not PATCH —
    /// because <see cref="ProfileUpdateInput"/> carries the full mutable surface and an
    /// omitted nullable field means "clear the value" rather than "leave unchanged" (the
    /// service normalises an empty preferred language to <c>ro</c> as the system default).
    /// </summary>
    /// <param name="input">Allowed mutations — Email, Phone, PreferredLanguage. Immutable
    /// identity fields (IDNP, Id, Roles, IsActive, CreatedAtUtc) are NOT accepted per
    /// CLAUDE.md §2.4 (mass-assignment prevention).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 No Content on success; 400 ProblemDetails on validation failure; 404 when the
    /// underlying user row has been deactivated.
    /// </returns>
    [HttpPut("me")]
    public async Task<IActionResult> UpdateMineAsync(
        [FromBody] ProfileUpdateInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = await _profiles.UpdateMineAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Reads the caller's notification-channel preferences (R0171 / CF 22.02 / CF 04.08).
    /// As with <see cref="GetMineAsync"/>, the caller is identified server-side via
    /// <c>ICallerContext</c> so the route carries no id.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the preferences (the default fully-opted-in shape when the row carries no
    /// stored preferences yet); 401 ProblemDetails when the caller is anonymous; 404 when
    /// the underlying user row has been deactivated.
    /// </returns>
    [HttpGet("notification-preferences")]
    public async Task<ActionResult<NotificationPreferencesDto>> GetNotificationPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _profiles.GetNotificationPreferencesAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureGeneric<NotificationPreferencesDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Replaces the caller's notification-channel preferences (R0171). Whole-object PUT —
    /// not PATCH — because the wire shape is small and the semantics of "omitted field"
    /// would otherwise be ambiguous.
    /// </summary>
    /// <param name="input">Full preferences object. Must NOT be <c>null</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 No Content on success; 400 ProblemDetails when validation rejects the body
    /// (e.g. an oversized category key); 401 / 404 mirror the GET semantics.
    /// </returns>
    [HttpPut("notification-preferences")]
    public async Task<IActionResult> SetNotificationPreferencesAsync(
        [FromBody] NotificationPreferencesDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = await _profiles.SetNotificationPreferencesAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0211 / TOR UI 003 — updates the caller's preferred UI language. Thin payload
    /// (one field) intentionally distinct from the broader <see cref="UpdateMineAsync"/>
    /// route because the language toggle is high-frequency (every locale switch) while
    /// the broader profile update is rare.
    /// </summary>
    /// <param name="input">Allowed payload: <c>{ "language": "ro"|"en"|"ru" }</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 No Content on success; 400 ProblemDetails when the supplied language is not in
    /// the allow-list; 401 ProblemDetails when the caller is anonymous; 404 when the
    /// underlying user row has been deactivated.
    /// </returns>
    [HttpPut("language")]
    public async Task<IActionResult> UpdateLanguageAsync(
        [FromBody] ProfileLanguageInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Validate at the controller boundary so callers receive ValidationFailed without
        // needing the service to re-derive the allow-list. The service-level UpdateMineAsync
        // path silently normalises whitespace to "ro" which is wrong for an explicit language
        // PUT — here we reject anything outside the allow-list.
        var v = await _languageValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Problem(v.Errors[0].ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        // Read the current profile, push a delta that only touches the language field.
        // We reuse IProfileService.UpdateMineAsync to keep the audit / persistence
        // contract in one place; Email + Phone are passed back untouched (the call
        // semantics of ProfileUpdateInput are "set these three fields to these values").
        var read = await _profiles.GetMineAsync(cancellationToken).ConfigureAwait(false);
        if (read.IsFailure)
        {
            return MapFailureBare(read.ErrorCode, read.ErrorMessage);
        }

        var existing = read.Value!;
        var normalised = input.Language.Trim().ToLowerInvariant();
        var update = new ProfileUpdateInput(
            Email: existing.Email,
            Phone: existing.Phone,
            PreferredLanguage: normalised);

        var result = await _profiles.UpdateMineAsync(update, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// R0361 / UC13 — updates the caller's self-service contact fields
    /// (display name, e-mail, phone) WITHOUT touching the language preference.
    /// Used by the <c>MyProfile.razor</c> page so the contact form and the
    /// locale switcher (R0211 / <c>PUT /api/profile/language</c>) own
    /// independent slices of the row.
    /// </summary>
    /// <param name="input">Allowed mutations — DisplayName (required), Email, Phone.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 204 No Content on success;
    /// 400 ProblemDetails on validation failure (FluentValidation rejects the body
    /// before touching the service);
    /// 401 ProblemDetails when the caller is anonymous;
    /// 404 when the underlying user row has been deactivated.
    /// </returns>
    [HttpPut("contact")]
    public async Task<IActionResult> UpdateContactAsync(
        [FromBody] ProfileContactInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Validate at the controller boundary so callers receive a fast 400 with
        // a stable error code BEFORE we round-trip to the database. The service
        // layer ALSO re-validates DisplayName + Phone as defense in depth, so
        // direct service callers (background jobs, tests) still get the same
        // safety guarantee.
        var v = await _contactValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Problem(v.Errors[0].ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _profiles.UpdateMyContactAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps a <see cref="Result{T}"/> failure to an <see cref="ActionResult{T}"/>.</summary>
    /// <typeparam name="T">The DTO type the action would have returned on success.</typeparam>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 401 / 400 ProblemDetails as appropriate.</returns>
    private ActionResult<T> MapFailureGeneric<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>404 / 401 / 400 ProblemDetails as appropriate.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 NotFound, 401 Unauthorized, or 400 BadRequest.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
