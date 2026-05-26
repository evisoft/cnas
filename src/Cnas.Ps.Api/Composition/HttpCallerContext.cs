using System.Security.Claims;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.AccessScope;

namespace Cnas.Ps.Api.Composition;

/// <summary>
/// HTTP-scoped <see cref="ICallerContext"/>. Reads the authenticated user from the current
/// <see cref="HttpContext"/> claims, decodes the Sqid id to its internal long, and exposes
/// the correlation id used by structured logs and audit records.
/// </summary>
public sealed class HttpCallerContext(IHttpContextAccessor accessor, ISqidService sqids) : ICallerContext
{
    private readonly IHttpContextAccessor _accessor = accessor;
    private readonly ISqidService _sqids = sqids;

    /// <summary>
    /// Per-instance cached scope envelope. The HTTP request scope owns this instance,
    /// so the cache is implicitly per-request — built on first access from
    /// <see cref="Roles"/>, reused on subsequent reads.
    /// </summary>
    private IAccessScope? _cachedScope;

    /// <inheritdoc />
    public long? UserId
    {
        get
        {
            var sub = UserSqid;
            if (string.IsNullOrEmpty(sub)) return null;
            var decoded = _sqids.TryDecode(sub);
            return decoded.IsSuccess ? decoded.Value : null;
        }
    }

    /// <inheritdoc />
    public string? UserSqid =>
        _accessor.HttpContext?.User?.FindFirstValue("uid")
        ?? _accessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Roles =>
        _accessor.HttpContext?.User?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray()
        ?? [];

    /// <inheritdoc />
    public string? SourceIp =>
        _accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <inheritdoc />
    public string? CorrelationId =>
        _accessor.HttpContext?.TraceIdentifier;

    /// <inheritdoc />
    /// <remarks>
    /// Reads the <c>mpower:principal_idnp</c> claim placed on the
    /// <see cref="ClaimsPrincipal"/> by the MPass SAML handler at sign-in. Whitespace-only
    /// values are normalised to <c>null</c> so callers can treat "not present" and
    /// "explicitly empty" uniformly.
    /// </remarks>
    public string? OnBehalfOfPrincipalIdnp
    {
        get
        {
            var v = _accessor.HttpContext?.User?.FindFirstValue("mpower:principal_idnp");
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the <c>mpower:delegation_id</c> claim placed on the
    /// <see cref="ClaimsPrincipal"/> by the MPass SAML handler at sign-in. Whitespace-only
    /// values are normalised to <c>null</c>.
    /// </remarks>
    public string? DelegationPowerId
    {
        get
        {
            var v = _accessor.HttpContext?.User?.FindFirstValue("mpower:delegation_id");
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Composed lazily from <see cref="Roles"/> through
    /// <see cref="RolesBasedAccessScope.FromRoles(IReadOnlyCollection{string})"/> on first
    /// access and cached in the per-request instance field for subsequent reads. The
    /// per-request scope of this <see cref="HttpCallerContext"/> means the cache is
    /// implicitly request-scoped — every HTTP request rebuilds it once.
    /// </remarks>
    public IAccessScope AccessScope => _cachedScope ??= RolesBasedAccessScope.FromRoles(Roles);

    /// <inheritdoc />
    /// <remarks>
    /// Reads the JWT <c>jti</c> claim placed on the <see cref="ClaimsPrincipal"/> by
    /// the bearer-token pipeline at sign-in. Whitespace-only values are normalised to
    /// <c>null</c>. Returns <c>null</c> for service-to-service / anonymous callers
    /// whose principal carries no session identifier (e.g. background-job execution
    /// scopes).
    /// </remarks>
    public string? SessionId
    {
        get
        {
            var v = _accessor.HttpContext?.User?.FindFirstValue("jti")
                    ?? _accessor.HttpContext?.User?.FindFirstValue("sid");
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }
}
