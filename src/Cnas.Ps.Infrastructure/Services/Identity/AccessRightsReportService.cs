using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Identity;

/// <summary>
/// R2274 / TOR SEC 028 — concrete implementation of
/// <see cref="IAccessRightsReportService"/>. Walks the identity graph
/// (UserProfile + UserGroup + UserGroupParent + UserGroupMembership) to
/// answer the three "who can do what" projections, emits an audit event per
/// query, and renders RFC 4180-compliant CSV exports.
/// </summary>
/// <remarks>
/// <para>
/// <b>No new background work.</b> The service is synchronous and runs inside
/// the inbound request lifetime; CSV export sizes are bounded by the
/// <c>AccessRightsReportPagingValidator.MaxTake</c> cap (500 users) which
/// keeps the largest payload comfortably under MVC's per-response buffer.
/// </para>
/// <para>
/// <b>PII discipline.</b> The audit-event payload records only the report
/// subkind and the row count — individual user identifiers, emails, and
/// display names never appear in the audit trail. PII still flows through
/// the report body itself; that body is treated as Confidential at the API
/// boundary (see controller).
/// </para>
/// <para>
/// <b>Membership source of truth.</b> The <c>UserProfile.Groups</c> string
/// list is the legacy denormalised cache; this service treats
/// <c>UserGroupMembership</c> rows as authoritative for the
/// "is-a-member-of" relationship per iteration 74's design note.
/// </para>
/// <para>
/// <b>Read-replica routing (R1904 / ARH 025).</b> This service carries
/// <see cref="LongRunningReportServiceAttribute"/> because every EF Core
/// query here flows through <see cref="IReadOnlyCnasDbContext"/>; the only
/// "write" performed is an audit-row append that goes via
/// <see cref="IAuditService"/> — NOT through the writable
/// <c>ICnasDbContext</c>. The architecture test
/// <c>LongRunningReportServicesUseReadReplica</c> guards against accidental
/// regressions that would inline a writable-context dependency here.
/// </para>
/// </remarks>
[LongRunningReportService]
public sealed class AccessRightsReportService : IAccessRightsReportService
{
    /// <summary>Stable audit event-code emitted on every report generation.</summary>
    public const string AuditReportGenerated = "ACCESS_RIGHTS_REPORT.GENERATED";

    /// <summary>Subkind value for the by-user projection.</summary>
    public const string SubkindByUser = "BY_USER";

    /// <summary>Subkind value for the by-role projection.</summary>
    public const string SubkindByRole = "BY_ROLE";

    /// <summary>Subkind value for the by-group projection.</summary>
    public const string SubkindByGroup = "BY_GROUP";

    /// <summary>Subkind value for the by-role CSV export.</summary>
    public const string SubkindCsvByRole = "CSV_BY_ROLE";

    /// <summary>Subkind value for the full-matrix CSV export.</summary>
    public const string SubkindCsvFullMatrix = "CSV_FULL_MATRIX";

    /// <summary>Stable role-code / group-code shape — uppercase letter then upper/digits/underscore, 2..64 chars.</summary>
    private static readonly Regex CodeRegex = new(
        "^[A-Z][A-Z0-9_]{1,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>Cached JSON serializer options shared across audit-payload builders.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Cached header row for the by-role CSV export (cached to avoid repeat allocation).</summary>
    private static readonly string[] ByRoleCsvHeader =
        ["UserSqid", "DisplayName", "Email", "AccountStatus", "DirectGrant", "GrantingGroups"];

    /// <summary>Cached header row for the full-matrix CSV export.</summary>
    private static readonly string[] FullMatrixCsvHeader =
        ["UserSqid", "DisplayName", "Email", "AccountStatus", "RoleCode", "GrantKind", "GrantingChain"];

    private readonly IReadOnlyCnasDbContext _db;
    private readonly IUserGroupRoleResolver _resolver;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">Read-side EF Core context.</param>
    /// <param name="resolver">Transitive-role resolver shipped in iteration 74.</param>
    /// <param name="sqids">Sqid encoder/decoder used at the boundary.</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly from service code.</param>
    /// <param name="caller">Authenticated-caller information for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    public AccessRightsReportService(
        IReadOnlyCnasDbContext db,
        IUserGroupRoleResolver resolver,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        _db = db;
        _resolver = resolver;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<AccessRightsByUserReportDto>> ReportByUserAsync(
        string userSqid,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(userSqid);
        if (decoded.IsFailure)
        {
            return Result<AccessRightsByUserReportDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var userId = decoded.Value;

        var user = await _db.UserProfiles
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.State, u.Roles })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (user is null)
        {
            return Result<AccessRightsByUserReportDto>.Failure(
                ErrorCodes.NotFound, "User profile not found.");
        }

        // Resolve effective roles (group-inherited).
        var resolved = await _resolver.ResolveEffectiveRolesAsync(userId, ct).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result<AccessRightsByUserReportDto>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        // Union direct + inherited. Direct roles win (empty chain).
        var directRoles = user.Roles ?? [];
        var directSet = new HashSet<string>(directRoles, StringComparer.Ordinal);
        var effective = new List<AccessRightsEffectiveRoleDto>(directRoles.Count + resolved.Value.Roles.Count);

        foreach (var direct in directRoles)
        {
            effective.Add(new AccessRightsEffectiveRoleDto(
                RoleCode: direct,
                GrantKind: nameof(AccessRightsGrantKind.Direct),
                GrantingGroupChain: Array.Empty<string>()));
        }
        foreach (var inh in resolved.Value.Roles)
        {
            if (directSet.Contains(inh.RoleCode))
            {
                continue;
            }
            effective.Add(new AccessRightsEffectiveRoleDto(
                RoleCode: inh.RoleCode,
                GrantKind: nameof(AccessRightsGrantKind.Inherited),
                GrantingGroupChain: inh.GrantingGroupChain));
        }

        // Group memberships — first-class registry.
        var memberships = await (from m in _db.UserGroupMemberships
                                 join g in _db.UserGroups on m.UserGroupId equals g.Id
                                 where m.UserProfileId == userId && m.IsActive && g.IsActive
                                 select new AccessRightsGroupMembershipDto(g.Code, g.DisplayName))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var dto = new AccessRightsByUserReportDto(
            UserSqid: _sqids.Encode(user.Id),
            DisplayName: user.DisplayName,
            Email: user.Email,
            AccountStatus: user.State.ToString(),
            DirectRoles: directRoles.ToList(),
            EffectiveRoles: effective,
            GroupMemberships: memberships);

        await EmitAuditAsync(SubkindByUser, rowCount: effective.Count, ct).ConfigureAwait(false);
        CnasMeter.AccessRightsReportGenerated.Add(1,
            new KeyValuePair<string, object?>("report_kind", "by_user"));
        CnasMeter.AccessRightsReportRowsReturned.Add(effective.Count,
            new KeyValuePair<string, object?>("report_kind", "by_user"));

        return Result<AccessRightsByUserReportDto>.Success(dto);
    }

    /// <inheritdoc />
    public async Task<Result<AccessRightsByRoleReportDto>> ReportByRoleAsync(
        string roleCode,
        AccessRightsReportPagingDto paging,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paging);

        if (string.IsNullOrEmpty(roleCode) || !CodeRegex.IsMatch(roleCode))
        {
            return Result<AccessRightsByRoleReportDto>.Failure(
                ErrorCodes.ValidationFailed,
                "RoleCode must match ^[A-Z][A-Z0-9_]{1,63}$.");
        }
        if (paging.Skip < 0 || paging.Take < 1 || paging.Take > 500)
        {
            return Result<AccessRightsByRoleReportDto>.Failure(
                ErrorCodes.ValidationFailed, "Skip must be >= 0 and Take must be in 1..500.");
        }

        // Find the set of groups whose effective role-set includes roleCode.
        // A group "carries" roleCode when its own Roles list contains it AND it
        // is Active; the resolver propagates that grant DOWNWARD to descendant
        // groups. So we collect every group that holds the role directly, then
        // expand to descendants via ResolveDescendantsAsync.
        var seedGroups = await _db.UserGroups
            .Where(g => g.IsActive && g.Status == UserGroupStatus.Active && g.Roles.Contains(roleCode))
            .Select(g => new { g.Id, g.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var grantingGroupIdsByUser = new Dictionary<long, HashSet<string>>();
        foreach (var seed in seedGroups)
        {
            // Direct membership in the seed group: user gets the grant from seed.Code.
            var directMembers = await _db.UserGroupMemberships
                .Where(m => m.UserGroupId == seed.Id && m.IsActive)
                .Select(m => m.UserProfileId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var userId in directMembers)
            {
                if (!grantingGroupIdsByUser.TryGetValue(userId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    grantingGroupIdsByUser[userId] = set;
                }
                set.Add(seed.Code);
            }

            // Descendants — every user a member of a descendant inherits the seed's grant.
            var descendantsResult = await _resolver.ResolveDescendantsAsync(seed.Id, ct).ConfigureAwait(false);
            if (descendantsResult.IsFailure)
            {
                continue;
            }
            foreach (var descendant in descendantsResult.Value)
            {
                var descendantId = _sqids.TryDecode(descendant.Id);
                if (descendantId.IsFailure)
                {
                    continue;
                }
                var descendantMembers = await _db.UserGroupMemberships
                    .Where(m => m.UserGroupId == descendantId.Value && m.IsActive)
                    .Select(m => m.UserProfileId)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                foreach (var userId in descendantMembers)
                {
                    if (!grantingGroupIdsByUser.TryGetValue(userId, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        grantingGroupIdsByUser[userId] = set;
                    }
                    set.Add(seed.Code);
                }
            }
        }

        // Direct grants from UserProfile.Roles.
        var directGrantUserIds = await _db.UserProfiles
            .Where(u => u.IsActive && u.Roles.Contains(roleCode))
            .Select(u => u.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var directSet = new HashSet<long>(directGrantUserIds);

        // Union both sets of user ids.
        var allUserIds = new HashSet<long>(grantingGroupIdsByUser.Keys);
        foreach (var id in directGrantUserIds)
        {
            allUserIds.Add(id);
        }

        // Hydrate user-profile rows.
        var users = await _db.UserProfiles
            .Where(u => allUserIds.Contains(u.Id) && u.IsActive)
            .Where(u => paging.IncludeDisabledAccounts || u.State == UserAccountState.Active)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.State })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Project to row DTOs, sorted by DisplayName for deterministic paging.
        var rows = users
            .OrderBy(u => u.DisplayName, StringComparer.Ordinal)
            .Select(u =>
            {
                var isDirect = directSet.Contains(u.Id);
                var grantingGroups = grantingGroupIdsByUser.TryGetValue(u.Id, out var set)
                    ? (IReadOnlyList<string>)set.OrderBy(s => s, StringComparer.Ordinal).ToList()
                    : Array.Empty<string>();
                return new UserAccessRowDto(
                    UserSqid: _sqids.Encode(u.Id),
                    DisplayName: u.DisplayName,
                    Email: u.Email,
                    AccountStatus: u.State.ToString(),
                    GrantKind: isDirect
                        ? nameof(AccessRightsGrantKind.Direct)
                        : nameof(AccessRightsGrantKind.Inherited),
                    GrantingGroups: grantingGroups);
            })
            .ToList();

        var total = rows.Count;
        var page = rows.Skip(paging.Skip).Take(paging.Take).ToList();

        var dto = new AccessRightsByRoleReportDto(
            RoleCode: roleCode,
            Items: page,
            Total: total,
            Skip: paging.Skip,
            Take: paging.Take);

        await EmitAuditAsync(SubkindByRole, rowCount: page.Count, ct).ConfigureAwait(false);
        CnasMeter.AccessRightsReportGenerated.Add(1,
            new KeyValuePair<string, object?>("report_kind", "by_role"));
        CnasMeter.AccessRightsReportRowsReturned.Add(page.Count,
            new KeyValuePair<string, object?>("report_kind", "by_role"));

        return Result<AccessRightsByRoleReportDto>.Success(dto);
    }

    /// <inheritdoc />
    public async Task<Result<AccessRightsByGroupReportDto>> ReportByGroupAsync(
        string groupSqid,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(groupSqid);
        if (decoded.IsFailure)
        {
            return Result<AccessRightsByGroupReportDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var groupId = decoded.Value;

        var group = await _db.UserGroups
            .Where(g => g.Id == groupId && g.IsActive)
            .Select(g => new { g.Id, g.Code, g.DisplayName, g.Roles, g.Status })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (group is null)
        {
            return Result<AccessRightsByGroupReportDto>.Failure(
                ErrorCodes.NotFound, "User-group not found.");
        }

        // Descendants subtree (excludes the queried group).
        var descendantsResult = await _resolver.ResolveDescendantsAsync(groupId, ct).ConfigureAwait(false);
        if (descendantsResult.IsFailure)
        {
            return Result<AccessRightsByGroupReportDto>.Failure(
                descendantsResult.ErrorCode!, descendantsResult.ErrorMessage!);
        }
        var descendantList = descendantsResult.Value;
        var descendantIds = new List<long>(descendantList.Count);
        var descendantCodeById = new Dictionary<long, string>();
        foreach (var desc in descendantList)
        {
            var idResult = _sqids.TryDecode(desc.Id);
            if (idResult.IsFailure)
            {
                continue;
            }
            descendantIds.Add(idResult.Value);
            descendantCodeById[idResult.Value] = desc.Code;
        }

        // Aggregated role-codes across this group + active descendants.
        // Disabled groups contribute no roles (per resolver semantics).
        var aggregatedRoles = new HashSet<string>(StringComparer.Ordinal);
        if (group.Status == UserGroupStatus.Active && group.Roles is not null)
        {
            foreach (var r in group.Roles)
            {
                aggregatedRoles.Add(r);
            }
        }
        if (descendantIds.Count > 0)
        {
            var descendantGroups = await _db.UserGroups
                .Where(g => descendantIds.Contains(g.Id) && g.IsActive && g.Status == UserGroupStatus.Active)
                .Select(g => new { g.Id, g.Roles })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var dg in descendantGroups)
            {
                if (dg.Roles is null)
                {
                    continue;
                }
                foreach (var r in dg.Roles)
                {
                    aggregatedRoles.Add(r);
                }
            }
        }

        // Member rows — direct + inherited via descendants.
        var memberRows = new List<AccessRightsByGroupMemberRowDto>();

        var directMembers = await (from m in _db.UserGroupMemberships
                                   join u in _db.UserProfiles on m.UserProfileId equals u.Id
                                   where m.UserGroupId == groupId && m.IsActive && u.IsActive
                                   select new { u.Id, u.DisplayName })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var dm in directMembers)
        {
            memberRows.Add(new AccessRightsByGroupMemberRowDto(
                UserSqid: _sqids.Encode(dm.Id),
                DisplayName: dm.DisplayName,
                GrantKind: nameof(AccessRightsGrantKind.DirectInGroup),
                SourceGroupCode: group.Code));
        }

        if (descendantIds.Count > 0)
        {
            var descendantMemberRows = await (from m in _db.UserGroupMemberships
                                              join u in _db.UserProfiles on m.UserProfileId equals u.Id
                                              where descendantIds.Contains(m.UserGroupId) && m.IsActive && u.IsActive
                                              select new { m.UserGroupId, u.Id, u.DisplayName })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var dmr in descendantMemberRows)
            {
                var srcCode = descendantCodeById.TryGetValue(dmr.UserGroupId, out var c)
                    ? c
                    : string.Empty;
                memberRows.Add(new AccessRightsByGroupMemberRowDto(
                    UserSqid: _sqids.Encode(dmr.Id),
                    DisplayName: dmr.DisplayName,
                    GrantKind: nameof(AccessRightsGrantKind.InheritedFromDescendant),
                    SourceGroupCode: srcCode));
            }
        }

        var dto = new AccessRightsByGroupReportDto(
            GroupSqid: _sqids.Encode(group.Id),
            GroupCode: group.Code,
            GroupDisplayName: group.DisplayName,
            DescendantGroupCodes: descendantList.Select(d => d.Code).ToList(),
            Members: memberRows,
            AggregatedRoleCodes: aggregatedRoles.OrderBy(r => r, StringComparer.Ordinal).ToList());

        await EmitAuditAsync(SubkindByGroup, rowCount: memberRows.Count, ct).ConfigureAwait(false);
        CnasMeter.AccessRightsReportGenerated.Add(1,
            new KeyValuePair<string, object?>("report_kind", "by_group"));
        CnasMeter.AccessRightsReportRowsReturned.Add(memberRows.Count,
            new KeyValuePair<string, object?>("report_kind", "by_group"));

        return Result<AccessRightsByGroupReportDto>.Success(dto);
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> ExportByRoleCsvAsync(
        string roleCode,
        CancellationToken ct = default)
    {
        // Reuse the by-role projection at maximum cap; the CSV body is the
        // serialisation of every row (not paged). The hard cap is 500 — the
        // bulk-action equivalent.
        var paging = new AccessRightsReportPagingDto(Skip: 0, Take: 500, IncludeDisabledAccounts: true);
        var report = await ReportByRoleAsync(roleCode, paging, ct).ConfigureAwait(false);
        if (report.IsFailure)
        {
            return Result<byte[]>.Failure(report.ErrorCode!, report.ErrorMessage!);
        }

        var rows = new List<string[]>(report.Value.Items.Count + 1)
        {
            ByRoleCsvHeader,
        };
        foreach (var item in report.Value.Items)
        {
            rows.Add(new[]
            {
                item.UserSqid,
                item.DisplayName,
                item.Email ?? string.Empty,
                item.AccountStatus,
                item.GrantKind == nameof(AccessRightsGrantKind.Direct) ? "true" : "false",
                string.Join("|", item.GrantingGroups),
            });
        }

        var bytes = BuildCsv(rows);

        await EmitAuditAsync(SubkindCsvByRole, rowCount: report.Value.Items.Count, ct).ConfigureAwait(false);
        CnasMeter.AccessRightsReportGenerated.Add(1,
            new KeyValuePair<string, object?>("report_kind", "csv_by_role"));
        CnasMeter.AccessRightsReportRowsReturned.Add(report.Value.Items.Count,
            new KeyValuePair<string, object?>("report_kind", "csv_by_role"));

        return Result<byte[]>.Success(bytes);
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> ExportFullAccessMatrixCsvAsync(
        AccessRightsReportPagingDto paging,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paging);

        if (paging.Skip < 0 || paging.Take < 1 || paging.Take > 500)
        {
            return Result<byte[]>.Failure(
                ErrorCodes.ValidationFailed, "Skip must be >= 0 and Take must be in 1..500.");
        }

        var userQuery = _db.UserProfiles.Where(u => u.IsActive);
        if (!paging.IncludeDisabledAccounts)
        {
            userQuery = userQuery.Where(u => u.State == UserAccountState.Active);
        }

        var users = await userQuery
            .OrderBy(u => u.DisplayName)
            .Skip(paging.Skip)
            .Take(paging.Take)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.State, u.Roles })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var rows = new List<string[]>(users.Count * 4 + 1)
        {
            FullMatrixCsvHeader,
        };

        var rowCount = 0;
        foreach (var u in users)
        {
            var sqid = _sqids.Encode(u.Id);
            var directRoles = u.Roles ?? [];
            var directSet = new HashSet<string>(directRoles, StringComparer.Ordinal);
            foreach (var direct in directRoles)
            {
                rows.Add(new[]
                {
                    sqid,
                    u.DisplayName,
                    u.Email ?? string.Empty,
                    u.State.ToString(),
                    direct,
                    nameof(AccessRightsGrantKind.Direct),
                    string.Empty,
                });
                rowCount++;
            }

            var resolved = await _resolver.ResolveEffectiveRolesAsync(u.Id, ct).ConfigureAwait(false);
            if (resolved.IsSuccess)
            {
                foreach (var inh in resolved.Value.Roles)
                {
                    if (directSet.Contains(inh.RoleCode))
                    {
                        continue;
                    }
                    rows.Add(new[]
                    {
                        sqid,
                        u.DisplayName,
                        u.Email ?? string.Empty,
                        u.State.ToString(),
                        inh.RoleCode,
                        nameof(AccessRightsGrantKind.Inherited),
                        string.Join("|", inh.GrantingGroupChain),
                    });
                    rowCount++;
                }
            }
        }

        var bytes = BuildCsv(rows);

        await EmitAuditAsync(SubkindCsvFullMatrix, rowCount, ct).ConfigureAwait(false);
        CnasMeter.AccessRightsReportGenerated.Add(1,
            new KeyValuePair<string, object?>("report_kind", "csv_full_matrix"));
        CnasMeter.AccessRightsReportRowsReturned.Add(rowCount,
            new KeyValuePair<string, object?>("report_kind", "csv_full_matrix"));

        return Result<byte[]>.Success(bytes);
    }

    /// <summary>
    /// Renders a list of row arrays to a UTF-8 (no BOM) CSV byte buffer per
    /// RFC 4180: comma separator, CRLF line ending, double-quote wrapping
    /// when a field contains comma / quote / CR / LF; embedded quotes are
    /// doubled.
    /// </summary>
    /// <param name="rows">Header + data rows.</param>
    /// <returns>UTF-8 encoded CSV bytes (no BOM).</returns>
    internal static byte[] BuildCsv(IEnumerable<string[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (int i = 0; i < row.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                AppendCsvField(sb, row[i] ?? string.Empty);
            }
            sb.Append("\r\n");
        }
        // UTF-8 without BOM.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
    }

    /// <summary>Appends a single field, RFC 4180-quoting when necessary.</summary>
    /// <param name="sb">Target buffer.</param>
    /// <param name="value">Field value (treated as <c>""</c> when null).</param>
    private static void AppendCsvField(StringBuilder sb, string value)
    {
        bool needsQuoting = false;
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == ',' || c == '"' || c == '\n' || c == '\r')
            {
                needsQuoting = true;
                break;
            }
        }
        if (!needsQuoting)
        {
            sb.Append(value);
            return;
        }
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"')
            {
                sb.Append("\"\"");
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// Emits the canonical audit row. The payload carries only the
    /// non-PII facets (subkind, row count, the requested instant); no user
    /// identifiers from the report body are recorded.
    /// </summary>
    /// <param name="subkind">Stable subkind tag (<see cref="SubkindByUser"/>, ...).</param>
    /// <param name="rowCount">Number of rows returned by the projection.</param>
    /// <param name="ct">Standard cancellation token.</param>
    private async Task EmitAuditAsync(string subkind, int rowCount, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            subkind,
            rowCount,
            atUtc = _clock.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        }, CachedJsonOptions);
        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            AuditReportGenerated,
            AuditSeverity.Information,
            actor,
            targetEntity: nameof(UserProfile),
            targetEntityId: _caller.UserId,
            payload,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);
    }
}
