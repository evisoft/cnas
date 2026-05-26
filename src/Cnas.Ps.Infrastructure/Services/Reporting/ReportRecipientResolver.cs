using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Reporting;

/// <summary>
/// R1906 / TOR Annex 6 — production implementation of
/// <see cref="IReportRecipientResolver"/>. Reads through the
/// <see cref="IReadOnlyCnasDbContext"/> projection and never writes —
/// resolution is a pure projection from a rule onto its recipient list.
/// </summary>
public sealed class ReportRecipientResolver : IReportRecipientResolver
{
    private readonly IReadOnlyCnasDbContext _db;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the resolver with its scoped collaborators.</summary>
    /// <param name="db">Read-only DB context.</param>
    /// <param name="sqids">Sqid encoder/decoder for the User recipient kind.</param>
    public ReportRecipientResolver(IReadOnlyCnasDbContext db, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        _db = db;
        _sqids = sqids;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ResolvedRecipientDto>>> ResolveAsync(
        ReportDistributionRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        switch (rule.RecipientKind)
        {
            case ReportRecipientKind.EmailAddress:
                return Result<IReadOnlyList<ResolvedRecipientDto>>.Success(
                    new List<ResolvedRecipientDto>
                    {
                        new(ReportRecipientKind.EmailAddress, "(redacted-email)", rule.RecipientCode),
                    });

            case ReportRecipientKind.MNotifyCategory:
                return Result<IReadOnlyList<ResolvedRecipientDto>>.Success(
                    new List<ResolvedRecipientDto>
                    {
                        new(ReportRecipientKind.MNotifyCategory, rule.RecipientCode, rule.RecipientCode),
                    });

            case ReportRecipientKind.User:
                return Result<IReadOnlyList<ResolvedRecipientDto>>.Success(
                    await ResolveUserAsync(rule.RecipientCode, cancellationToken).ConfigureAwait(false));

            case ReportRecipientKind.Group:
                return Result<IReadOnlyList<ResolvedRecipientDto>>.Success(
                    await ResolveGroupAsync(rule.RecipientCode, cancellationToken).ConfigureAwait(false));

            case ReportRecipientKind.Role:
                return Result<IReadOnlyList<ResolvedRecipientDto>>.Success(
                    await ResolveRoleAsync(rule.RecipientCode, cancellationToken).ConfigureAwait(false));

            default:
                return Result<IReadOnlyList<ResolvedRecipientDto>>.Success(
                    new List<ResolvedRecipientDto>(0));
        }
    }

    /// <summary>
    /// Resolves a User-kind rule whose <c>RecipientCode</c> is either a Sqid
    /// or a local login. Emits zero rows when the user cannot be found.
    /// </summary>
    /// <param name="rawCode">Decrypted recipient code (Sqid or login).</param>
    /// <param name="cancellationToken">Cancellation propagated from the resolver.</param>
    /// <returns>A list with zero or one recipient row.</returns>
    private async Task<IReadOnlyList<ResolvedRecipientDto>> ResolveUserAsync(
        string rawCode,
        CancellationToken cancellationToken)
    {
        // Try Sqid first (most rules carry the encoded surrogate id), then login fallback.
        var decoded = _sqids.TryDecode(rawCode);
        long? userId = decoded.IsSuccess ? decoded.Value : null;

        var user = userId is { } id
            ? await _db.UserProfiles
                .Where(u => u.Id == id && u.IsActive)
                .Select(u => new { u.DisplayName, u.Email, u.LocalLogin })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await _db.UserProfiles
                .Where(u => u.LocalLogin == rawCode && u.IsActive)
                .Select(u => new { u.DisplayName, u.Email, u.LocalLogin })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            return new List<ResolvedRecipientDto>(0);
        }

        var address = !string.IsNullOrWhiteSpace(user.Email) ? user.Email! : (user.LocalLogin ?? string.Empty);
        return new List<ResolvedRecipientDto>
        {
            new(ReportRecipientKind.User, user.DisplayName, address),
        };
    }

    /// <summary>
    /// Resolves a Group-kind rule. Looks up the group by its stable code and
    /// fans out to every direct member. Inherited descendants are NOT walked
    /// in this iteration — the dispatcher consumes one rule per group.
    /// </summary>
    /// <param name="groupCode">Stable group code.</param>
    /// <param name="cancellationToken">Cancellation propagated from the resolver.</param>
    /// <returns>One row per direct member.</returns>
    private async Task<IReadOnlyList<ResolvedRecipientDto>> ResolveGroupAsync(
        string groupCode,
        CancellationToken cancellationToken)
    {
        var groupRow = await _db.UserGroups
            .Where(g => g.Code == groupCode && g.IsActive)
            .Select(g => new { g.Id })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (groupRow is null)
        {
            return new List<ResolvedRecipientDto>(0);
        }

        var groupId = groupRow.Id;
        var users = await _db.UserGroupMemberships
            .Where(m => m.UserGroupId == groupId && m.IsActive)
            .Join(
                _db.UserProfiles.Where(u => u.IsActive),
                m => m.UserProfileId,
                u => u.Id,
                (m, u) => new { u.DisplayName, u.Email, u.LocalLogin })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return users
            .Select(u =>
            {
                var address = !string.IsNullOrWhiteSpace(u.Email) ? u.Email! : (u.LocalLogin ?? string.Empty);
                return new ResolvedRecipientDto(ReportRecipientKind.Group, u.DisplayName, address);
            })
            .ToList();
    }

    /// <summary>
    /// Resolves a Role-kind rule. Returns every Active user whose
    /// <c>UserProfile.Roles</c> list contains the role code. Materialises the
    /// rows in-memory because the <c>Roles</c> property is a List&lt;string&gt;
    /// (jsonb at rest) and the InMemory provider does not translate Contains
    /// over the typed list.
    /// </summary>
    /// <param name="roleCode">Role code (e.g. <c>cnas-admin</c>).</param>
    /// <param name="cancellationToken">Cancellation propagated from the resolver.</param>
    /// <returns>One row per matching user.</returns>
    private async Task<IReadOnlyList<ResolvedRecipientDto>> ResolveRoleAsync(
        string roleCode,
        CancellationToken cancellationToken)
    {
        var users = await _db.UserProfiles
            .Where(u => u.IsActive)
            .Select(u => new { u.DisplayName, u.Email, u.LocalLogin, u.Roles })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return users
            .Where(u => u.Roles.Contains(roleCode, StringComparer.Ordinal))
            .Select(u =>
            {
                var address = !string.IsNullOrWhiteSpace(u.Email) ? u.Email! : (u.LocalLogin ?? string.Empty);
                return new ResolvedRecipientDto(ReportRecipientKind.Role, u.DisplayName, address);
            })
            .ToList();
    }
}
