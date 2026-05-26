using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Workflow;
using Cnas.Ps.Application.WorkflowAcl;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Application.WorkflowRules;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

// This file groups intentionally-minimal implementations for the UC services whose
// business rules are still being shaped. Each implementation is wired into DI so the
// composition root remains complete and integration tests can exercise the full pipe.
// The behaviour returned here is the minimum required by the use case description in TOR
// and is annotated with the relevant TOR clauses for traceability.

/// <summary>
/// UC03 / UC12 — registry search. Reads through <see cref="IReadOnlyCnasDbContext"/>
/// so registry list/search queries (potentially LIKE-scanning thousands of rows)
/// land on the Postgres streaming-replication replica per R0026 / TOR PSR 006 /
/// ARH 025 — leaving the primary backend free for write workloads.
/// </summary>
/// <remarks>
/// Search is pure-read with no read-your-own-writes expectation — a contributor
/// just inserted may take milliseconds to appear in a subsequent search, which
/// matches the streaming-replication eventual-consistency contract. Tests share
/// the same EF Core InMemory store between writer and reader so the seed→search
/// round-trip is still deterministic in CI.
/// </remarks>
public sealed class DataSearchService(IReadOnlyCnasDbContext db, ISqidService sqids) : IDataSearchService
{
    private readonly IReadOnlyCnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;

    /// <inheritdoc />
    public async Task<Result<PagedResult<SearchRow>>> SearchAsync(
        string registry,
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registry);
        ArgumentNullException.ThrowIfNull(request);

        var pageSize = Math.Clamp(request.Page.PageSize, 1, 200);
        var skip = Math.Max(0, request.Page.Page - 1) * pageSize;
        var trimmed = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
        var like = trimmed is null ? null : $"%{trimmed}%";

        return registry.ToUpperInvariant() switch
        {
            "CONTRIBUTORS" => await SearchContributorsAsync(trimmed, like, skip, pageSize, cancellationToken).ConfigureAwait(false),
            "INSURED" => await SearchInsuredAsync(trimmed, like, skip, pageSize, cancellationToken).ConfigureAwait(false),
            "APPLICATIONS" => await SearchApplicationsAsync(trimmed, like, skip, pageSize, cancellationToken).ConfigureAwait(false),
            _ => Result<PagedResult<SearchRow>>.Failure(ErrorCodes.NotFound, $"Unknown registry '{registry}'."),
        };
    }

    /// <inheritdoc />
    public Task<Result<Stream>> ExportAsync(string registry, SearchRequest request, ExportFormat format, CancellationToken cancellationToken = default)
    {
        _ = registry;
        _ = request;
        _ = format;
        _ = cancellationToken;
        return Task.FromResult(Result<Stream>.Failure(ErrorCodes.Internal, "Use IReportingService for exports."));
    }

    private async Task<Result<PagedResult<SearchRow>>> SearchContributorsAsync(string? trimmed, string? like, int skip, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.Contributors.Where(c => c.IsActive);
        if (like is not null && trimmed is not null)
        {
            // R0162 / CF 03.13 — diacritic-insensitive search. Denumire carries user-entered
            // diacritics so it is folded both sides; Idno is ASCII-only (TOR clause 005.6) so
            // it is masked but not folded. Note: Idno is encrypted in production (BUG-007 /
            // SEC 035) so the Idno branch only meaningfully matches in tests.
            // R0164 / UI 012 / CF 03.02 — wildcard-mask translation. WildcardMask handles
            // '*' → '%' and escapes user-typed literal LIKE wildcards. Fold is applied
            // FIRST so the diacritic-fold result still carries the user's '*' through to
            // the mask processor. Idno is ASCII so it bypasses the fold step.
            var folded = DiacriticFolding.Fold(trimmed);
            var likeFolded = WildcardMask.ToLikePattern(folded);
            var likeMasked = WildcardMask.ToLikePattern(trimmed);
            if (IsRelationalProvider(_db))
            {
                query = query.Where(c =>
                    EF.Functions.ILike(CnasDbFunctions.Unaccent(c.Denumire), likeFolded) ||
                    EF.Functions.ILike(c.Idno, likeMasked));
            }
            else
            {
                // R0162 + R0164 InMemory fallback — DiacriticFolding.Fold + WildcardMask.ToRegex
                // are static methods the InMemory provider can invoke client-side via its
                // LINQ-to-Objects translator. Keeping the .Where on IQueryable preserves the EF
                // async provider so subsequent LongCountAsync / ToListAsync still translate.
                var regexFolded = WildcardMask.ToRegex(folded);
                var regexAscii = WildcardMask.ToRegex(trimmed);
                query = query.Where(c =>
                    regexFolded.IsMatch(DiacriticFolding.Fold(c.Denumire)) ||
                    regexAscii.IsMatch(c.Idno));
            }
        }

        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderBy(c => c.Denumire)
            .Skip(skip).Take(pageSize)
            .Select(c => new { c.Id, c.Idno, c.Denumire, c.IsInsolvent })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var rows = items.Select(c => new SearchRow(_sqids.Encode(c.Id), new Dictionary<string, string>
        {
            ["idno"] = c.Idno,
            ["denumire"] = c.Denumire,
            ["insolvent"] = c.IsInsolvent.ToString(),
        })).ToList();

        return Result<PagedResult<SearchRow>>.Success(new PagedResult<SearchRow>(rows, skip / pageSize + 1, pageSize, total));
    }

    private async Task<Result<PagedResult<SearchRow>>> SearchInsuredAsync(string? trimmed, string? like, int skip, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.InsuredPersons.Where(p => p.IsActive);
        if (like is not null && trimmed is not null)
        {
            // R0162 / CF 03.13 — diacritic-insensitive search on LastName/FirstName; Idnp
            // is ASCII-only and is masked but not folded.
            // R0164 / UI 012 / CF 03.02 — wildcard mask: '*' → '%' on relational, '.*'
            // (anchored) on InMemory. See SearchContributorsAsync for the rationale.
            var folded = DiacriticFolding.Fold(trimmed);
            var likeFolded = WildcardMask.ToLikePattern(folded);
            var likeMasked = WildcardMask.ToLikePattern(trimmed);
            if (IsRelationalProvider(_db))
            {
                query = query.Where(p =>
                    EF.Functions.ILike(p.Idnp, likeMasked) ||
                    EF.Functions.ILike(CnasDbFunctions.Unaccent(p.LastName), likeFolded) ||
                    EF.Functions.ILike(CnasDbFunctions.Unaccent(p.FirstName), likeFolded));
            }
            else
            {
                // R0162 + R0164 InMemory fallback — see SearchContributorsAsync for the
                // rationale on Fold + WildcardMask.ToRegex client-side translation.
                var regexFolded = WildcardMask.ToRegex(folded);
                var regexAscii = WildcardMask.ToRegex(trimmed);
                query = query.Where(p =>
                    regexAscii.IsMatch(p.Idnp) ||
                    regexFolded.IsMatch(DiacriticFolding.Fold(p.LastName)) ||
                    regexFolded.IsMatch(DiacriticFolding.Fold(p.FirstName)));
            }
        }

        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
            .Skip(skip).Take(pageSize)
            .Select(p => new { p.Id, p.Idnp, p.LastName, p.FirstName })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var rows = items.Select(p => new SearchRow(_sqids.Encode(p.Id), new Dictionary<string, string>
        {
            ["idnp"] = p.Idnp,
            ["lastName"] = p.LastName,
            ["firstName"] = p.FirstName,
        })).ToList();

        return Result<PagedResult<SearchRow>>.Success(new PagedResult<SearchRow>(rows, skip / pageSize + 1, pageSize, total));
    }

    private async Task<Result<PagedResult<SearchRow>>> SearchApplicationsAsync(string? trimmed, string? like, int skip, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.Applications.Where(a => a.IsActive);
        if (like is not null && trimmed is not null)
        {
            if (IsRelationalProvider(_db))
            {
                query = query.Where(a => a.ReferenceNumber != null && EF.Functions.ILike(a.ReferenceNumber, like));
            }
            else
            {
                query = query.Where(a =>
                    a.ReferenceNumber != null &&
                    a.ReferenceNumber.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
            }
        }

        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip(skip).Take(pageSize)
            .Select(a => new { a.Id, a.ReferenceNumber, a.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var rows = items.Select(a => new SearchRow(_sqids.Encode(a.Id), new Dictionary<string, string>
        {
            ["ref"] = a.ReferenceNumber ?? string.Empty,
            ["status"] = a.Status.ToString(),
        })).ToList();

        return Result<PagedResult<SearchRow>>.Success(new PagedResult<SearchRow>(rows, skip / pageSize + 1, pageSize, total));
    }

    /// <summary>
    /// Detects whether the underlying <see cref="ICnasDbContext"/> is backed by a relational
    /// provider (Npgsql in production) vs the in-memory test fake. This is the single seam
    /// that lets each registry's search query stay native PostgreSQL ILIKE in production
    /// while remaining executable against EF Core InMemory in integration tests.
    /// </summary>
    /// <remarks>
    /// The seam wraps ONLY the <c>EF.Functions.ILike</c> branches in
    /// <see cref="SearchContributorsAsync(string?, string?, int, int, CancellationToken)"/>,
    /// <see cref="SearchInsuredAsync(string?, string?, int, int, CancellationToken)"/>, and
    /// <see cref="SearchApplicationsAsync(string?, string?, int, int, CancellationToken)"/>
    /// because <c>EF.Functions.ILike</c> is Postgres-specific; the rest of each query
    /// (the <c>IsActive</c> filter, <c>OrderBy</c>, <c>Skip</c>/<c>Take</c>, and the
    /// projection <c>Select</c>) uses LINQ operators that translate on every EF Core
    /// provider, so they intentionally do not consult this method. Mirrors the
    /// per-class private helper shape used by <c>PublicContentService</c>,
    /// <c>ContributorService</c>, and <c>InsuredPersonService</c> — kept private here per
    /// the per-class pattern; not extracted to a shared utility.
    /// </remarks>
    /// <param name="db">The application's read-only DB context abstraction.</param>
    /// <returns>True for Postgres / SQL Server / SQLite; false for InMemory or other in-process providers.</returns>
    private static bool IsRelationalProvider(IReadOnlyCnasDbContext db)
    {
        if (db is not DbContext concrete)
        {
            return false;
        }
        var providerName = concrete.Database.ProviderName ?? string.Empty;
        return !providerName.Contains("InMemory", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// UC04 — Dashboard. Aggregates DB counters into KPI widgets and composes them
/// with role-aware tile producers per CF 04.01 (per-role personalisation) and
/// CF 04.02 (five-category tile split).
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition pipeline.</b> The service computes the legacy KPI counters
/// (open applications, open dossiers, total insured) AND invokes every
/// <see cref="Cnas.Ps.Application.Dashboard.IDashboardTileProducer"/> whose
/// <c>SupportedRoles</c> intersects the caller's roles. The producers contribute
/// the CF 04.02 category tiles (notifications, task arrivals, workflow updates,
/// items requiring involvement, items awaiting approval).
/// </para>
/// <para>
/// <b>Anonymous fallback.</b> When the caller is unauthenticated
/// (<c>ICallerContext.UserId is null</c>) the service returns only the wildcard
/// widgets the registry exposes to every authenticated caller — producers that
/// require a user id are skipped.
/// </para>
/// <para>
/// <b>Category tagging.</b> Each emitted widget is tagged with the canonical
/// <see cref="Cnas.Ps.Contracts.DashboardCategory"/> name on
/// <see cref="KpiWidget.Category"/> using the registry's lookup. Widgets whose
/// code is unknown to the registry leave the category null (legacy fallthrough).
/// </para>
/// </remarks>
public sealed class DashboardService(
    ICnasDbContext db,
    Cnas.Ps.Application.Dashboard.DashboardWidgetRegistry registry,
    ICallerContext caller,
    IEnumerable<Cnas.Ps.Application.Dashboard.IDashboardTileProducer> producers,
    IEnumerable<Cnas.Ps.Application.Dashboard.IKpiGridProducer>? gridProducers = null) : IDashboardService
{
    private readonly ICnasDbContext _db = db;
    private readonly Cnas.Ps.Application.Dashboard.DashboardWidgetRegistry _registry = registry;
    private readonly ICallerContext _caller = caller;
    private readonly IReadOnlyList<Cnas.Ps.Application.Dashboard.IDashboardTileProducer> _producers
        = producers as IReadOnlyList<Cnas.Ps.Application.Dashboard.IDashboardTileProducer>
            ?? [.. producers];
    /// <summary>
    /// R0533 / TOR CF 04.04 — registered KPI grid producers; null-tolerant so test
    /// compositions that pre-date the grid keep compiling without a behaviour change.
    /// </summary>
    private readonly IReadOnlyList<Cnas.Ps.Application.Dashboard.IKpiGridProducer> _gridProducers
        = gridProducers is null
            ? Array.Empty<Cnas.Ps.Application.Dashboard.IKpiGridProducer>()
            : gridProducers as IReadOnlyList<Cnas.Ps.Application.Dashboard.IKpiGridProducer>
              ?? [.. gridProducers];

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<KpiWidget>>> GetWidgetsAsync(CancellationToken cancellationToken = default)
    {
        // Per-role visibility envelope. Anonymous callers see the wildcard subset.
        var roles = _caller.Roles ?? Array.Empty<string>();
        var visibleDescriptors = _registry.VisibleTo(roles);
        var visibleCodes = new HashSet<string>(
            visibleDescriptors.Select(d => d.Code),
            StringComparer.OrdinalIgnoreCase);

        var widgets = new List<KpiWidget>();

        // ── Legacy KPI tiles. Keep verbatim — pinned by R0530 baseline integration
        //    test. The category is the registry-assigned bucket so the UI's per-
        //    category grouping (CF 04.02) keeps working for these as well.
        if (visibleCodes.Contains("APPS_OPEN"))
        {
            var openApplications = await _db.Applications
                .Where(a => a.IsActive && a.Status != ApplicationStatus.Closed && a.Status != ApplicationStatus.Rejected)
                .LongCountAsync(cancellationToken).ConfigureAwait(false);
            widgets.Add(TagCategory("APPS_OPEN", "Cereri în lucru", openApplications, "cereri"));
        }
        if (visibleCodes.Contains("DOSSIERS_OPEN"))
        {
            var dossiersInExamination = await _db.Dossiers
                .LongCountAsync(d => d.IsActive && d.ClosedAtUtc == null, cancellationToken).ConfigureAwait(false);
            widgets.Add(TagCategory("DOSSIERS_OPEN", "Dosare deschise", dossiersInExamination, "dosare"));
        }
        if (visibleCodes.Contains("INSURED_TOTAL"))
        {
            var insuredTotal = await _db.InsuredPersons.LongCountAsync(p => p.IsActive, cancellationToken).ConfigureAwait(false);
            widgets.Add(TagCategory("INSURED_TOTAL", "Persoane asigurate", insuredTotal, "persoane"));
        }

        // ── R0531 / CF 04.02 — tile producers. Each producer owns one canonical
        //    category and contributes 1..N widgets. The dashboard service filters
        //    by role-intersection first to avoid touching the DB for producers the
        //    caller would not see (e.g. AwaitingApproval for a plain cnas-user).
        if (_caller.UserId is long userId)
        {
            var rolesSet = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);
            foreach (var producer in _producers)
            {
                if (!IntersectsCallerRoles(producer.SupportedRoles, rolesSet))
                {
                    continue;
                }

                var produced = await producer.ProduceAsync(userId, cancellationToken).ConfigureAwait(false);
                if (produced.IsFailure || produced.Value is null) continue;

                foreach (var w in produced.Value)
                {
                    // R0536 / iter 134 — emitted codes are accepted when EITHER they
                    // are explicitly registered in the visibility envelope OR they
                    // match a known dynamic-code prefix (e.g. the per-status
                    // histogram from MyRequestsByStatusKpiProducer emits one widget
                    // per ApplicationStatus value the caller actually has rows in).
                    if (!visibleCodes.Contains(w.Code)
                        && !w.Code.StartsWith(Cnas.Ps.Application.Dashboard.DashboardWidgetRegistry.StatusHistogramCodePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    // The producer's emitted Category is authoritative; preserve it.
                    widgets.Add(w);
                }
            }
        }

        return Result<IReadOnlyList<KpiWidget>>.Success(widgets);
    }

    /// <summary>
    /// Builds a <see cref="KpiWidget"/> for a legacy KPI counter and stamps the
    /// canonical CF 04.02 category by looking the code up in the registry. When
    /// the code is not registered the category falls through as <c>null</c>.
    /// </summary>
    /// <param name="code">Stable widget code.</param>
    /// <param name="title">Localised title.</param>
    /// <param name="value">Aggregate counter value.</param>
    /// <param name="unit">Optional unit suffix.</param>
    /// <returns>The tagged widget.</returns>
    private KpiWidget TagCategory(string code, string title, decimal value, string? unit)
    {
        var descriptor = _registry.FindByCode(code);
        var category = descriptor is null ? null : descriptor.Category.ToString();
        return new KpiWidget(code, title, value, unit, category);
    }

    /// <summary>
    /// Tests whether a producer's supported-role list intersects the caller's role
    /// set, honouring the wildcard <c>"*"</c> convention.
    /// </summary>
    /// <param name="supported">Producer's declared role allow-list.</param>
    /// <param name="callerRoles">Caller's roles (already case-folded into a HashSet).</param>
    /// <returns><c>true</c> when the producer should run for this caller.</returns>
    private static bool IntersectsCallerRoles(IReadOnlyCollection<string> supported, HashSet<string> callerRoles)
    {
        foreach (var role in supported)
        {
            if (role == "*") return true;
            if (callerRoles.Contains(role)) return true;
        }
        return false;
    }

    /// <inheritdoc />
    public async Task<Result<DashboardSnapshotDto>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        // 1. Reuse the existing widget pipeline so the per-category tiles stay in lock-step.
        var widgets = await GetWidgetsAsync(cancellationToken).ConfigureAwait(false);
        if (widgets.IsFailure)
        {
            return Result<DashboardSnapshotDto>.Failure(widgets.ErrorCode!, widgets.ErrorMessage!);
        }

        // 2. Compose the aggregate KPI grid via the role-gated producers.
        var roles = _caller.Roles ?? Array.Empty<string>();
        var rolesSet = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);
        var cells = new List<KpiGridCellDto>();
        if (_caller.UserId is long userId)
        {
            foreach (var producer in _gridProducers)
            {
                if (!IntersectsCallerRoles(producer.SupportedRoles, rolesSet)) continue;
                var produced = await producer.ProduceAsync(userId, cancellationToken).ConfigureAwait(false);
                if (produced.IsFailure || produced.Value is null) continue;
                cells.AddRange(produced.Value);
            }
        }

        return Result<DashboardSnapshotDto>.Success(new DashboardSnapshotDto(
            Widgets: widgets.Value!,
            KpiGrid: cells));
    }
}

/// <summary>UC05 / R0127 — Task inbox + per-task reassignment.</summary>
/// <remarks>
/// <para>
/// <b>Audit + notifications.</b> The reassignment endpoints depend on
/// <see cref="IAuditService"/> (Notice-severity <c>WORKFLOWTASK.REASSIGNED</c> /
/// <c>WORKFLOWTASK.REASSIGNMENT_REVERTED</c> rows) and
/// <see cref="IWorkflowNotificationOrchestrator"/> (R0128 strategy lookup; falls
/// through to legacy default when no strategy is configured for the workflow). The
/// orchestrator is optional in tests — when injected as <c>null</c> the dispatch is
/// skipped and the operation still records the audit row.
/// </para>
/// <para>
/// <b>Workflow definition id.</b> A <see cref="WorkflowTask"/> doesn't carry the
/// <c>WorkflowDefinition.Id</c> directly — the link goes Dossier → Application →
/// ServicePassport.WorkflowCode → WorkflowDefinition. For the orchestrator hand-off we
/// pass <c>0</c>, which the orchestrator interprets as "no strategy configured" and
/// falls through to legacy default behaviour. A future batch will wire the join when
/// per-workflow strategies are exercised for reassignment events.
/// </para>
/// </remarks>
public sealed class TaskInboxService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService? audit = null,
    IWorkflowNotificationOrchestrator? notifications = null,
    IWorkflowAclService? acl = null,
    IWorkflowRuleEngine? ruleEngine = null,
    IWorkflowTaskHistoryService? history = null) : ITaskInboxService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService? _audit = audit;
    private readonly IWorkflowNotificationOrchestrator? _notifications = notifications;
    // R0126 — optional ACL gate; when null the legacy controller-policy gate alone
    // applies. Constructor parameter is optional so the bulk of existing tests that
    // build the service with five required dependencies keep compiling unchanged.
    private readonly IWorkflowAclService? _acl = acl;
    // R0124 — optional rule engine; when null no business rules are evaluated on
    // task completion. Same opt-in defaulting rationale as _acl.
    private readonly IWorkflowRuleEngine? _ruleEngine = ruleEngine;
    // R0125 — optional history projection writer; when null the per-task history is
    // not populated from this service. Same opt-in defaulting rationale as _acl /
    // _ruleEngine — existing test compositions keep compiling unchanged.
    private readonly IWorkflowTaskHistoryService? _history = history;

    /// <inheritdoc />
    public async Task<Result<PagedResult<TaskInboxItem>>> ListAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var userId = _caller.UserId;
        if (userId is null)
        {
            return Result<PagedResult<TaskInboxItem>>.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }

        var pageSize = Math.Clamp(page.PageSize, 1, 200);
        var skip = Math.Max(0, page.Page - 1) * pageSize;

        var query = _db.WorkflowTasks.Where(t => t.IsActive && t.AssignedUserId == userId).OrderBy(t => t.DueAtUtc);
        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .Skip(skip).Take(pageSize)
            .Select(t => new TaskInboxItem(
                _sqids.Encode(t.Id),
                t.Title,
                t.Status.ToString(),
                t.DueAtUtc,
                _sqids.Encode(t.DossierId)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return Result<PagedResult<TaskInboxItem>>.Success(new PagedResult<TaskInboxItem>(items, page.Page, pageSize, total));
    }

    /// <inheritdoc />
    public async Task<Result> ClaimAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(taskId);
        if (decoded.IsFailure) return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);

        var task = await _db.WorkflowTasks.SingleOrDefaultAsync(t => t.Id == decoded.Value && t.IsActive, cancellationToken).ConfigureAwait(false);
        if (task is null) return Result.Failure(ErrorCodes.NotFound, "Task not found.");

        // Deny-by-default (CLAUDE.md §5.4): a task already assigned to a different user
        // cannot be stolen by a second caller — return WORKFLOW_NOT_ASSIGNEE (TasksController
        // maps to HTTP 403). Re-claim by the SAME caller is permitted and remains idempotent,
        // so a retried claim still flips Status to InProgress.
        if (task.AssignedUserId is not null && task.AssignedUserId != _caller.UserId)
        {
            return Result.Failure(ErrorCodes.WorkflowNotAssignee, "Task already claimed by another user.");
        }

        task.AssignedUserId = _caller.UserId;
        task.Status = WorkflowTaskStatus.InProgress;
        // R0202 — the task leaves the unclaimed pool the moment a user claims it. Nulling
        // the stamp is part of the writer-site invariant documented on
        // WorkflowTask.UnclaimedSinceUtc and is what makes the escalation-job predicate
        // miss this row on subsequent fires.
        task.UnclaimedSinceUtc = null;
        task.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> CompleteAsync(string taskId, string resultJson, CancellationToken cancellationToken = default)
    {
        _ = resultJson;
        var decoded = _sqids.TryDecode(taskId);
        if (decoded.IsFailure) return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);

        var task = await _db.WorkflowTasks.SingleOrDefaultAsync(t => t.Id == decoded.Value && t.IsActive, cancellationToken).ConfigureAwait(false);
        if (task is null) return Result.Failure(ErrorCodes.NotFound, "Task not found.");

        if (task.AssignedUserId != _caller.UserId)
        {
            return Result.Failure(ErrorCodes.WorkflowNotAssignee, "Caller is not the assigned user.");
        }

        // R0126 / CF 16.10 — workflow-scoped ACL gate. Skipped when the optional
        // service is not registered (legacy compositions). The task's `Title` carries
        // the step code in the present model — the value is duplicated into a
        // canonical step-code identifier when the orchestrator creates the task; until
        // BPMN-strict step tracking is added, the Title doubles as the step key. The
        // resolution chain (task → dossier → application → passport.WorkflowCode →
        // workflow row) yields the workflow surrogate id; when any link is missing
        // we fall through to the controller-level policy (no extra denial).
        if (_acl is not null && _caller.UserId is long callerId)
        {
            var workflowDefinitionId = await ResolveWorkflowDefinitionIdAsync(task, cancellationToken).ConfigureAwait(false);
            if (workflowDefinitionId is long workflowId)
            {
                var canHandle = await _acl
                    .CanHandleAsync(workflowId, task.Title, callerId, cancellationToken)
                    .ConfigureAwait(false);
                if (!canHandle)
                {
                    return Result.Failure(
                        WorkflowAclConstants.WorkflowAclDeniedCode,
                        "Caller is not permitted to handle this workflow step.");
                }
            }
        }

        // R0124 / CF 16.08 — transition rule pack. Same opt-in pattern as the ACL gate.
        // The `from`/`to` step codes are not strongly modelled yet; we use the task's
        // current Title for both sides (the completion edge is "step → step.completed"
        // in spirit). Block reason flows back to the caller as the failure message.
        if (_ruleEngine is not null)
        {
            var ruleResult = await _ruleEngine
                .EvaluateTransitionAsync(task.Id, task.Title, task.Title, context: null, cancellationToken)
                .ConfigureAwait(false);
            if (!ruleResult.Allowed)
            {
                return Result.Failure(
                    WorkflowAclConstants.WorkflowRuleBlockedCode,
                    ruleResult.BlockReason ?? "Workflow rule blocked the transition.");
            }
        }

        task.Status = WorkflowTaskStatus.Completed;
        task.CompletedAtUtc = _clock.UtcNow;
        task.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // R0125 / CF 16.09 — record the Completed lifecycle event on the history
        // projection. Optional service: when not registered, the projection is not
        // populated from this site (older test compositions keep working). The step
        // code mirrors the task's current step anchor (NodeCode falls back to Title in
        // legacy tasks; see the writer-site invariant on WorkflowTask.NodeCode).
        if (_history is not null)
        {
            await _history.RecordEventAsync(
                workflowTaskId: task.Id,
                eventKind: WorkflowTaskStepEventKind.Completed,
                stepCode: task.NodeCode ?? task.Title,
                actorUserId: _caller.UserId,
                decisionCode: null,
                note: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>
    /// Resolves the workflow definition id for the supplied task by walking the
    /// task → dossier → application → service-passport → workflow-code chain. Returns
    /// <c>null</c> when any link is missing so the ACL gate can fall back to the
    /// controller-level policy rather than producing a misleading denial.
    /// </summary>
    /// <param name="task">The task whose workflow definition is being resolved.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The workflow definition surrogate id, or <c>null</c> when not resolvable.</returns>
    private async Task<long?> ResolveWorkflowDefinitionIdAsync(WorkflowTask task, CancellationToken ct)
    {
        var dossier = await _db.Dossiers
            .Where(d => d.Id == task.DossierId)
            .Select(d => new { d.ApplicationId })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (dossier is null) return null;

        var application = await _db.Applications
            .Where(a => a.Id == dossier.ApplicationId)
            .Select(a => new { a.ServicePassportId })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (application is null) return null;

        var passport = await _db.ServicePassports
            .Where(p => p.Id == application.ServicePassportId)
            .Select(p => new { p.WorkflowCode })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (passport is null || string.IsNullOrWhiteSpace(passport.WorkflowCode)) return null;

        var workflow = await _db.WorkflowDefinitions
            .Where(w => w.Code == passport.WorkflowCode && w.IsCurrent && w.IsActive)
            .Select(w => new { w.Id })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return workflow?.Id;
    }

    /// <inheritdoc />
    public async Task<Result<WorkflowTaskOutputDto>> ReassignAsync(
        long taskId,
        long newAssigneeUserId,
        string reason,
        long? absenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // ─── Load the target task. Terminal states (Completed / Cancelled) reject. ───
        var task = await _db.WorkflowTasks
            .SingleOrDefaultAsync(t => t.Id == taskId && t.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (task is null)
        {
            return Result<WorkflowTaskOutputDto>.Failure(ErrorCodes.NotFound, "Task not found.");
        }
        if (task.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled)
        {
            return Result<WorkflowTaskOutputDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Task is in a terminal status and cannot be reassigned.");
        }

        // ─── Validate the new assignee. Must be an active, non-suspended user. ───
        // Re-using the existing UserProfile state guard. We do NOT check role coverage
        // here — TODO[r0127-role-check]: hook into the per-workflow required-role list
        // once R0056 ABAC lands. Today the absence of role gating is documented on the
        // service contract and the validator side.
        var newAssignee = await _db.UserProfiles
            .SingleOrDefaultAsync(u => u.Id == newAssigneeUserId && u.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (newAssignee is null)
        {
            return Result<WorkflowTaskOutputDto>.Failure(
                ErrorCodes.NotFound,
                "New assignee not found.");
        }
        if (newAssignee.State != UserAccountState.Active)
        {
            return Result<WorkflowTaskOutputDto>.Failure(
                ErrorCodes.Forbidden,
                "New assignee is not in an Active account state.");
        }

        // iter-149 — no-op short-circuit. Reassigning a task to its current owner
        // is a degenerate call: the reassignment counter was being bumped, the
        // unclaimed SLA clock was being reset, an audit row was being written
        // and the notification orchestrator was being kicked even though nothing
        // actually changed. We bail out BEFORE any side-effect so the row stays
        // pristine. The caller still sees a successful Result carrying the
        // current task projection.
        if (task.AssignedUserId == newAssigneeUserId)
        {
            return Result<WorkflowTaskOutputDto>.Success(ToOutputDto(task));
        }

        // ─── Atomic mutation block. ───
        // First reassignment captures the original assignee so a subsequent revert
        // restores the right owner. Subsequent reassignments do NOT touch the column.
        var fromUserId = task.AssignedUserId;
        if (task.OriginalAssigneeUserId is null && task.AssignedUserId is not null)
        {
            task.OriginalAssigneeUserId = task.AssignedUserId;
        }

        task.AssignedUserId = newAssigneeUserId;
        task.ReassignmentReason = reason;
        task.ReassignmentCount += 1;
        task.DelegatedFromAbsenceId = absenceId;

        // R0202 — reset the unclaimed clock so the delegate gets a fresh SLA window. The
        // delegate is now the owner; we should not penalise them for the prior holder's
        // idle minutes. Mirrors the writer-site invariant documented on
        // WorkflowTask.UnclaimedSinceUtc.
        task.UnclaimedSinceUtc = _clock.UtcNow;
        task.UpdatedAtUtc = _clock.UtcNow;
        task.UpdatedBy = _caller.UserSqid;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // ─── Notice-severity audit row. ───
        if (_audit is not null)
        {
            var detailsJson = JsonSerializer.Serialize(new
            {
                fromUserSqid = fromUserId is null ? null : _sqids.Encode(fromUserId.Value),
                toUserSqid = _sqids.Encode(newAssigneeUserId),
                reason,
                count = task.ReassignmentCount,
                absenceSqid = absenceId is null ? null : _sqids.Encode(absenceId.Value),
            });

            await _audit.RecordAsync(
                eventCode: "WORKFLOWTASK.REASSIGNED",
                severity: AuditSeverity.Notice,
                actorId: _caller.UserSqid ?? "system",
                targetEntity: nameof(WorkflowTask),
                targetEntityId: task.Id,
                detailsJson: detailsJson,
                sourceIp: _caller.SourceIp,
                correlationId: _caller.CorrelationId,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // R0192 / TOR SEC 051 — dedicated assignee-change audit. Distinct
            // from the broader REASSIGNED event (which fires on every call,
            // even when the assignee didn't actually change because the
            // service let it through) — ASSIGNEE_CHANGED only fires when the
            // old/new sqids differ and is raised at Critical severity so it
            // bubbles up to MLog per SEC 056. The payload is the minimal
            // before/after pair so SIEM correlators can pivot on it without
            // pulling the full reassignment-reason envelope.
            if (fromUserId != newAssigneeUserId)
            {
                var assigneeChangeJson = JsonSerializer.Serialize(new
                {
                    oldAssigneeSqid = fromUserId is null ? null : _sqids.Encode(fromUserId.Value),
                    newAssigneeSqid = _sqids.Encode(newAssigneeUserId),
                    reason,
                });

                await _audit.RecordAsync(
                    eventCode: "WORKFLOWTASK.ASSIGNEE_CHANGED",
                    severity: AuditSeverity.Critical,
                    actorId: _caller.UserSqid ?? "system",
                    targetEntity: nameof(WorkflowTask),
                    targetEntityId: task.Id,
                    detailsJson: assigneeChangeJson,
                    sourceIp: _caller.SourceIp,
                    correlationId: _caller.CorrelationId,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        // ─── Notify the new assignee via the orchestrator (R0128). ───
        if (_notifications is not null)
        {
            await _notifications.DispatchAsync(
                workflowDefinitionId: 0L,
                workflowTaskId: task.Id,
                eventCode: WorkflowNotificationEvents.TaskReassigned,
                templateContext: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // R0125 / CF 16.09 — record the Reassigned lifecycle event on the history
        // projection. Same opt-in defaulting as the audit / notification chains.
        if (_history is not null)
        {
            await _history.RecordEventAsync(
                workflowTaskId: task.Id,
                eventKind: WorkflowTaskStepEventKind.Reassigned,
                stepCode: task.NodeCode ?? task.Title,
                actorUserId: _caller.UserId,
                decisionCode: null,
                note: reason,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return Result<WorkflowTaskOutputDto>.Success(ToOutputDto(task));
    }

    /// <inheritdoc />
    public async Task<Result> RevertReassignmentAsync(long taskId, CancellationToken cancellationToken = default)
    {
        var task = await _db.WorkflowTasks
            .SingleOrDefaultAsync(t => t.Id == taskId && t.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (task is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Task not found.");
        }
        if (task.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                "Task is in a terminal status and cannot be reverted.");
        }
        if (task.OriginalAssigneeUserId is null)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                "Task has never been reassigned; nothing to revert.");
        }

        var fromUserId = task.AssignedUserId;
        task.AssignedUserId = task.OriginalAssigneeUserId;
        task.DelegatedFromAbsenceId = null;
        task.ReassignmentCount += 1;
        task.UpdatedAtUtc = _clock.UtcNow;
        task.UpdatedBy = _caller.UserSqid;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (_audit is not null)
        {
            var detailsJson = JsonSerializer.Serialize(new
            {
                fromUserSqid = fromUserId is null ? null : _sqids.Encode(fromUserId.Value),
                toUserSqid = _sqids.Encode(task.AssignedUserId!.Value),
                count = task.ReassignmentCount,
            });

            await _audit.RecordAsync(
                eventCode: "WORKFLOWTASK.REASSIGNMENT_REVERTED",
                severity: AuditSeverity.Notice,
                actorId: _caller.UserSqid ?? "system",
                targetEntity: nameof(WorkflowTask),
                targetEntityId: task.Id,
                detailsJson: detailsJson,
                sourceIp: _caller.SourceIp,
                correlationId: _caller.CorrelationId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>
    /// Maps the supplied <see cref="WorkflowTask"/> into the public reassignment-shape DTO
    /// (Sqid-encoded ids per CLAUDE.md RULE 3).
    /// </summary>
    /// <param name="task">Entity to project.</param>
    /// <returns>The DTO snapshot.</returns>
    internal WorkflowTaskOutputDto ToOutputDto(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new WorkflowTaskOutputDto(
            Id: _sqids.Encode(task.Id),
            Title: task.Title,
            Status: task.Status.ToString(),
            AssigneeSqid: task.AssignedUserId is null ? null : _sqids.Encode(task.AssignedUserId.Value),
            OriginalAssigneeSqid: task.OriginalAssigneeUserId is null ? null : _sqids.Encode(task.OriginalAssigneeUserId.Value),
            DelegatedFromAbsenceSqid: task.DelegatedFromAbsenceId is null ? null : _sqids.Encode(task.DelegatedFromAbsenceId.Value),
            ReassignmentCount: task.ReassignmentCount,
            ReassignmentReason: task.ReassignmentReason);
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<SupervisorTeamTaskDto>>> ListTeamQueueAsync(
        string? assigneeFilterSqid,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var supervisorId = _caller.UserId;
        if (supervisorId is null)
        {
            return Result<PagedResult<SupervisorTeamTaskDto>>.Failure(
                ErrorCodes.Unauthorized, "Not authenticated.");
        }

        // ─── Decode optional assignee filter. Malformed Sqid surfaces as 400. ───
        long? assigneeFilterId = null;
        if (!string.IsNullOrWhiteSpace(assigneeFilterSqid))
        {
            var decoded = _sqids.TryDecode(assigneeFilterSqid);
            if (decoded.IsFailure)
            {
                return Result<PagedResult<SupervisorTeamTaskDto>>.Failure(
                    decoded.ErrorCode!, decoded.ErrorMessage!);
            }
            assigneeFilterId = decoded.Value;
        }

        var clampedPageSize = Math.Clamp(pageSize, 1, 50);
        var clampedPage = Math.Max(1, page);
        var skip = (clampedPage - 1) * clampedPageSize;

        // ─── Route the read through the read-only contract (R0026 / PSR 006). ───
        // CnasDbContext implements BOTH writable + read-only interfaces. We surface the
        // read-only view explicitly here so the streaming-replica routing is honoured in
        // production while tests sharing one CnasDbContext keep round-tripping cleanly.
        var readDb = (IReadOnlyCnasDbContext)_db;

        // Discover the supervisor's direct group memberships, then the peers in those
        // groups. Excludes the supervisor themselves so their own tasks don't show up
        // in the team view (those belong on the personal inbox instead).
        var supervisorGroupIds = await readDb.UserGroupMemberships
            .Where(m => m.IsActive && m.UserProfileId == supervisorId.Value)
            .Select(m => m.UserGroupId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (supervisorGroupIds.Count == 0)
        {
            // Supervisor manages no groups → empty queue. Return success+empty rather
            // than an error so the UI can render the "no team yet" empty state.
            return Result<PagedResult<SupervisorTeamTaskDto>>.Success(
                new PagedResult<SupervisorTeamTaskDto>(
                    Array.Empty<SupervisorTeamTaskDto>(), clampedPage, clampedPageSize, 0L));
        }

        var peerIds = await readDb.UserGroupMemberships
            .Where(m => m.IsActive
                && supervisorGroupIds.Contains(m.UserGroupId)
                && m.UserProfileId != supervisorId.Value)
            .Select(m => m.UserProfileId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (peerIds.Count == 0)
        {
            return Result<PagedResult<SupervisorTeamTaskDto>>.Success(
                new PagedResult<SupervisorTeamTaskDto>(
                    Array.Empty<SupervisorTeamTaskDto>(), clampedPage, clampedPageSize, 0L));
        }

        // Open tasks only — Completed / Cancelled rows are noise on a supervisor's
        // queue (they cannot be reassigned). When a filter Sqid is supplied we restrict
        // further; missing/unknown assignee just returns no rows.
        IQueryable<WorkflowTask> taskQuery = readDb.WorkflowTasks
            .Where(t => t.IsActive
                && t.AssignedUserId != null
                && peerIds.Contains(t.AssignedUserId.Value)
                && t.Status != WorkflowTaskStatus.Completed
                && t.Status != WorkflowTaskStatus.Cancelled);

        if (assigneeFilterId is long fid)
        {
            taskQuery = taskQuery.Where(t => t.AssignedUserId == fid);
        }

        var total = await taskQuery.LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Project to an anonymous intermediate so the LINQ can stay translatable, then
        // build the DTO client-side (Sqid encoding is not provider-translatable).
        var rows = await taskQuery
            .OrderBy(t => t.DueAtUtc ?? DateTime.MaxValue)
            .ThenBy(t => t.Id)
            .Skip(skip).Take(clampedPageSize)
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.Status,
                t.DueAtUtc,
                t.DossierId,
                t.AssignedUserId,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Resolve assignee display names once per page in a single round-trip; the
        // server-side map keeps the row-level projection allocation-free.
        var assigneeIds = rows.Where(r => r.AssignedUserId is not null)
            .Select(r => r.AssignedUserId!.Value)
            .Distinct()
            .ToList();
        var assigneeNames = assigneeIds.Count == 0
            ? new Dictionary<long, string>()
            : await readDb.UserProfiles
                .Where(u => assigneeIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName })
                .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken)
                .ConfigureAwait(false);

        var items = rows.Select(r => new SupervisorTeamTaskDto(
            Id: _sqids.Encode(r.Id),
            Title: r.Title,
            Status: r.Status.ToString(),
            DueAtUtc: r.DueAtUtc,
            DossierId: _sqids.Encode(r.DossierId),
            AssigneeSqid: r.AssignedUserId is null ? null : _sqids.Encode(r.AssignedUserId.Value),
            AssigneeDisplayName: r.AssignedUserId is null
                ? null
                : assigneeNames.TryGetValue(r.AssignedUserId.Value, out var dn) ? dn : null))
            .ToList();

        return Result<PagedResult<SupervisorTeamTaskDto>>.Success(
            new PagedResult<SupervisorTeamTaskDto>(items, clampedPage, clampedPageSize, total));
    }

    /// <inheritdoc />
    public async Task<Result<WorkflowTaskOutputDto>> ReassignTaskAsync(
        string taskSqid,
        string newAssigneeSqid,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskSqid);
        ArgumentException.ThrowIfNullOrWhiteSpace(newAssigneeSqid);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // ─── Reason envelope (3..500 chars). Mirrors WorkflowTaskReassignDtoValidator. ───
        // The Sqid-typed wrapper is reachable from the supervisor UI without going through
        // the validator pipeline, so we re-check the envelope defensively here.
        if (reason.Length < 3 || reason.Length > 500)
        {
            return Result<WorkflowTaskOutputDto>.Failure(
                ErrorCodes.ValidationFailed, "Reason must be 3..500 chars.");
        }

        var taskDecoded = _sqids.TryDecode(taskSqid);
        if (taskDecoded.IsFailure)
        {
            return Result<WorkflowTaskOutputDto>.Failure(taskDecoded.ErrorCode!, taskDecoded.ErrorMessage!);
        }
        var assigneeDecoded = _sqids.TryDecode(newAssigneeSqid);
        if (assigneeDecoded.IsFailure)
        {
            return Result<WorkflowTaskOutputDto>.Failure(assigneeDecoded.ErrorCode!, assigneeDecoded.ErrorMessage!);
        }

        var result = await ReassignAsync(
            taskDecoded.Value, assigneeDecoded.Value, reason, absenceId: null, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            // R0381 — bounded-cardinality reason-bucket tag (short / long). The raw reason
            // is unbounded user input and MUST NEVER become a metric label; the bucket is
            // the only safe summarisation.
            var bucket = reason.Length <= 30 ? "short" : "long";
            CnasMeter.TaskReassignTotal.Add(1,
                new KeyValuePair<string, object?>("reason_bucket", bucket));
        }

        return result;
    }
}
