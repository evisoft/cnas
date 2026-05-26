using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Identity;

/// <summary>
/// R2274 / TOR SEC 028 — "who can do what" aggregation service. Answers the
/// three logical query directions over the identity graph by unioning the
/// directly-assigned <c>UserProfile.Roles</c> set with the role grants
/// inherited through transitive <c>UserGroup</c> memberships via the
/// cycle-aware BFS resolver shipped in iteration 74.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source of truth for membership.</b> Although <c>UserProfile</c> carries
/// a denormalised <c>Groups</c> string list (legacy), this service treats
/// <c>UserGroupMembership</c> as authoritative — it is the first-class
/// registry that ships role-resolution semantics. The legacy list is
/// ignored.
/// </para>
/// <para>
/// <b>Audit trail.</b> Every successful query emits an
/// <c>ACCESS_RIGHTS_REPORT.GENERATED</c> audit row with a subkind tag
/// indicating which projection was generated (BY_USER, BY_ROLE, BY_GROUP,
/// CSV_BY_ROLE, CSV_FULL_MATRIX). The audit payload records the report
/// subkind and the row count returned; it deliberately does NOT include
/// individual user identifiers from the report body — those are PII and
/// belong on the wire only.
/// </para>
/// <para>
/// <b>Sqid round-trip.</b> Every identifier crossing the boundary is
/// Sqid-encoded per CLAUDE.md RULE 3 — the service decodes the user/group
/// Sqids internally before touching the DbContext. Role and group codes are
/// stable domain identifiers, NOT Sqids, and stay as plain text.
/// </para>
/// </remarks>
public interface IAccessRightsReportService
{
    /// <summary>
    /// Returns a single user's effective access picture — direct roles +
    /// inherited roles + group memberships.
    /// </summary>
    /// <param name="userSqid">Sqid-encoded user-profile id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The populated DTO on success; <c>NOT_FOUND</c> when the user is missing.</returns>
    Task<Result<AccessRightsByUserReportDto>> ReportByUserAsync(
        string userSqid,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a paged list of users who effectively hold the supplied
    /// role-code, with each row tagged Direct or Inherited.
    /// </summary>
    /// <param name="roleCode">Role code (must match <c>^[A-Z][A-Z0-9_]{1,63}$</c>).</param>
    /// <param name="paging">Validated paging envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The populated DTO on success; <c>VALIDATION_FAILED</c> on bad input.</returns>
    Task<Result<AccessRightsByRoleReportDto>> ReportByRoleAsync(
        string roleCode,
        AccessRightsReportPagingDto paging,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a group's effective access picture — direct members + members
    /// reached through descendant groups, plus the aggregated role-codes
    /// contributed by the queried subtree.
    /// </summary>
    /// <param name="groupSqid">Sqid-encoded group id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The populated DTO on success; <c>NOT_FOUND</c> when the group is missing.</returns>
    Task<Result<AccessRightsByGroupReportDto>> ReportByGroupAsync(
        string groupSqid,
        CancellationToken ct = default);

    /// <summary>
    /// Streams a UTF-8 (no BOM) CSV with one row per effective grant of
    /// <paramref name="roleCode"/>. RFC 4180-quoted, CRLF row separator.
    /// Header: <c>UserSqid,DisplayName,Email,AccountStatus,DirectGrant,GrantingGroups</c>.
    /// </summary>
    /// <param name="roleCode">Role code (must match <c>^[A-Z][A-Z0-9_]{1,63}$</c>).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>UTF-8 CSV bytes on success.</returns>
    Task<Result<byte[]>> ExportByRoleCsvAsync(
        string roleCode,
        CancellationToken ct = default);

    /// <summary>
    /// Streams a UTF-8 (no BOM) CSV with one row per (user, role) tuple
    /// across the entire identity graph, honouring the supplied paging
    /// envelope. Header:
    /// <c>UserSqid,DisplayName,Email,AccountStatus,RoleCode,GrantKind,GrantingChain</c>.
    /// </summary>
    /// <param name="paging">Validated paging envelope (caps the user count, not row count).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>UTF-8 CSV bytes on success.</returns>
    Task<Result<byte[]>> ExportFullAccessMatrixCsvAsync(
        AccessRightsReportPagingDto paging,
        CancellationToken ct = default);
}
