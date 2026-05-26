using Cnas.Ps.Application.Identity;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2270 / TOR SEC 023-024 — REST surface for the
/// <see cref="Cnas.Ps.Core.Domain.UserGroup"/> registry. Exposes the create /
/// modify / disable / enable / delete lifecycle plus the nested-group and
/// direct-membership endpoints AND the effective-role resolver query.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every endpoint is gated by the
/// <c>cnas-admin</c> role — these are role/permission-change paths and must
/// not be reachable by ordinary CNAS staff or citizens. The follow-up phases
/// R2272 / R2273 (delegation + 4-eyes admin) will refine the policy to a
/// dedicated <c>IdentityAdmin</c> name.
/// </para>
/// <para>
/// <b>Sqid round-trip.</b> Route parameters are decoded inside the service
/// layer via <see cref="ISqidService.TryDecode"/>; outbound DTOs carry
/// Sqid-encoded ids per CLAUDE.md RULE 3. The group <c>Code</c> stays as
/// plain text — it is a stable domain identifier, not a Sqid.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "cnas-admin")]
public sealed class UserGroupsController : ControllerBase
{
    private readonly IUserGroupService _svc;
    private readonly IUserGroupRoleResolver _resolver;

    /// <summary>
    /// Sqid service used to decode the user route parameter on the
    /// effective-roles endpoint. Property-injectable so tests can swap a
    /// substitute without going through the DI graph.
    /// </summary>
    public ISqidService? Sqid { get; init; }

    /// <summary>Constructs the controller with its collaborators.</summary>
    /// <param name="svc">User-group registry service façade.</param>
    /// <param name="resolver">Transitive-role resolver.</param>
    public UserGroupsController(IUserGroupService svc, IUserGroupRoleResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(resolver);
        _svc = svc;
        _resolver = resolver;
    }

    /// <summary>R2270 — creates a new user-group.</summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>201 with the persisted DTO on success; 400/409 on failure.</returns>
    [HttpPost("api/user-groups")]
    public async Task<ActionResult<UserGroupDto>> CreateAsync(
        [FromBody] UserGroupCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : MapFailure<UserGroupDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — modifies an existing user-group.</summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="input">Validated modify payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400/404 on failure.</returns>
    [HttpPut("api/user-groups/{sqid}")]
    public async Task<ActionResult<UserGroupDto>> ModifyAsync(
        string sqid,
        [FromBody] UserGroupModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.ModifyAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<UserGroupDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — disables an Active group.</summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/user-groups/{sqid}/disable")]
    public async Task<ActionResult<UserGroupDto>> DisableAsync(
        string sqid,
        [FromBody] UserGroupReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.DisableAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<UserGroupDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — re-enables a Disabled group.</summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed DTO; 400/404/409 on failure.</returns>
    [HttpPost("api/user-groups/{sqid}/enable")]
    public async Task<ActionResult<UserGroupDto>> EnableAsync(
        string sqid,
        [FromBody] UserGroupReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.EnableAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<UserGroupDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — soft-deletes a user-group.</summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>204 on success; 400/404 on failure.</returns>
    [HttpDelete("api/user-groups/{sqid}")]
    public async Task<ActionResult> DeleteAsync(
        string sqid,
        [FromBody] UserGroupReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.DeleteAsync(sqid, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? NoContent()
            : MapFailureNonGeneric(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — adds a child group under a parent group.</summary>
    /// <param name="parentSqid">Sqid-encoded parent id.</param>
    /// <param name="childSqid">Sqid-encoded child id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed parent DTO; 409 on cycle.</returns>
    [HttpPost("api/user-groups/{parentSqid}/children/{childSqid}")]
    public async Task<ActionResult<UserGroupDto>> AddChildAsync(
        string parentSqid,
        string childSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.AddChildAsync(parentSqid, childSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<UserGroupDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — removes a nesting relation.</summary>
    /// <param name="parentSqid">Sqid-encoded parent id.</param>
    /// <param name="childSqid">Sqid-encoded child id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed parent DTO; 404 when the link is missing.</returns>
    [HttpDelete("api/user-groups/{parentSqid}/children/{childSqid}")]
    public async Task<ActionResult<UserGroupDto>> RemoveChildAsync(
        string parentSqid,
        string childSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RemoveChildAsync(parentSqid, childSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<UserGroupDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — adds a user to a group's direct memberships.</summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="userSqid">Sqid-encoded user-profile id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed group DTO; 404 when missing.</returns>
    [HttpPost("api/user-groups/{sqid}/members/{userSqid}")]
    public async Task<ActionResult<UserGroupDto>> AddMemberAsync(
        string sqid,
        string userSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.AddMemberAsync(sqid, userSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<UserGroupDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — removes a user from a group's direct memberships.</summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="userSqid">Sqid-encoded user-profile id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the refreshed group DTO; 404 when missing.</returns>
    [HttpDelete("api/user-groups/{sqid}/members/{userSqid}")]
    public async Task<ActionResult<UserGroupDto>> RemoveMemberAsync(
        string sqid,
        string userSqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.RemoveMemberAsync(sqid, userSqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<UserGroupDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — fetches a single user-group by Sqid id.</summary>
    /// <param name="sqid">Sqid-encoded group id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO; 404 when missing.</returns>
    [HttpGet("api/user-groups/{sqid}")]
    public async Task<ActionResult<UserGroupDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetByIdAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<UserGroupDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — fetches a single user-group by stable code.</summary>
    /// <param name="code">Group code.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the DTO; 404 when missing.</returns>
    [HttpGet("api/user-groups/by-code/{code}")]
    public async Task<ActionResult<UserGroupDto>> GetByCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var result = await _svc.GetByCodeAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<UserGroupDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — lists user-groups with optional filters.</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="kind">Optional kind filter.</param>
    /// <param name="roleCode">Optional role filter.</param>
    /// <param name="skip">Pagination skip count.</param>
    /// <param name="take">Pagination page size.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the page; 400 on bad input.</returns>
    [HttpGet("api/user-groups")]
    public async Task<ActionResult<UserGroupListPageDto>> ListAsync(
        [FromQuery] string? status,
        [FromQuery] string? kind,
        [FromQuery] string? roleCode,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var filter = new UserGroupListFilterDto(status, kind, roleCode, skip, take);
        var result = await _svc.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<UserGroupListPageDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>R2270 — resolves the user's effective transitive roles.</summary>
    /// <param name="userSqid">Sqid-encoded user-profile id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 with the resolved DTO; 400 on bad sqid.</returns>
    [HttpGet("api/user-groups/users/{userSqid}/effective-roles")]
    public async Task<ActionResult<UserGroupEffectiveRolesDto>> GetEffectiveRolesAsync(
        string userSqid,
        CancellationToken cancellationToken = default)
    {
        var sqidService = Sqid ?? HttpContext?.RequestServices.GetService(typeof(ISqidService)) as ISqidService;
        if (sqidService is null)
        {
            return Problem(
                "Sqid service unavailable.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var decoded = sqidService.TryDecode(userSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _resolver.ResolveEffectiveRolesAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailure<UserGroupEffectiveRolesDto>(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Maps generic-result failures to ProblemDetails.</summary>
    /// <typeparam name="T">DTO type the action would have returned.</typeparam>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult<T> MapFailure<T>(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Non-generic variant of <see cref="MapFailure{T}"/> for the delete path.</summary>
    /// <param name="code">Stable error code from the service.</param>
    /// <param name="message">Human-readable description.</param>
    /// <returns>ProblemDetails ActionResult.</returns>
    private ActionResult MapFailureNonGeneric(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>404 / 409 / 403 / 400 as appropriate.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest,
    };
}
