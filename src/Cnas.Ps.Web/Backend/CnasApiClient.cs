using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Web.Backend;

/// <summary>
/// Thin <see cref="HttpClient"/> wrapper used by Blazor pages to call the SI PS REST API.
/// Each method maps 1:1 to a controller endpoint. New methods return <see cref="Result{T}"/>
/// per CLAUDE.md §2.1 so pages can surface error codes without try/catch noise. The legacy
/// nullable-return methods (<see cref="GetPublicContentAsync"/>, <see cref="GetDashboardAsync"/>,
/// <see cref="GetMineAsync"/>, <see cref="GetInboxAsync"/>) remain for the existing pages
/// to keep this change non-breaking.
/// </summary>
public sealed class CnasApiClient(HttpClient http, ILogger<CnasApiClient> logger)
{
    private readonly HttpClient _http = http;
    private readonly ILogger<CnasApiClient> _logger = logger;

    /// <summary>
    /// Shared <see cref="JsonSerializerOptions"/> used for all request/response serialisation.
    /// PropertyNameCaseInsensitive accommodates both server casing flavours.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    // -------------------------------------------------------------------------
    // Legacy nullable-return helpers (kept for backwards compatibility with the
    // first-iteration pages that swallowed errors as a null response).
    // -------------------------------------------------------------------------

    /// <summary>UC01 — fetch the public content cards.</summary>
    /// <param name="query">Optional full-text query string.</param>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Page of public content cards, or <c>null</c> when the request fails.</returns>
    public async Task<PagedResult<PublicContentCard>?> GetPublicContentAsync(
        string? query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var url = $"api/public/content?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(query)) url += $"&query={Uri.EscapeDataString(query)}";

        try
        {
            return await _http.GetFromJsonAsync<PagedResult<PublicContentCard>>(url, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Public content request failed.");
            return null;
        }
    }

    /// <summary>UC04 — fetch dashboard widgets (legacy nullable signature).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of KPI widgets, or <c>null</c> on failure.</returns>
    public async Task<IReadOnlyList<KpiWidget>?> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<IReadOnlyList<KpiWidget>>("api/dashboard/widgets", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Dashboard fetch failed.");
            return null;
        }
    }

    /// <summary>
    /// R0533 / TOR CF 04.04 — fetches the combined dashboard snapshot (widgets +
    /// aggregate KPI grid). Each KPI grid cell may carry a <c>DeepLinkUrl</c> the
    /// UI renders as a click-through anchor (R0534).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result wrapping the snapshot, or a transport failure.</returns>
    public Task<Result<DashboardSnapshotDto>> GetDashboardSnapshotAsync(
        CancellationToken cancellationToken = default)
        => GetAsync<DashboardSnapshotDto>("api/dashboard/snapshot", cancellationToken);

    /// <summary>UC06 — list the calling user's applications (legacy nullable signature).</summary>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged list, or <c>null</c> when the request fails.</returns>
    public async Task<PagedResult<ApplicationListItemOutput>?> GetMineAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<PagedResult<ApplicationListItemOutput>>(
                $"api/applications/mine?page={page}&pageSize={pageSize}", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Application list fetch failed.");
            return null;
        }
    }

    /// <summary>UC22 — fetch the calling user's notification inbox (legacy nullable signature).</summary>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged inbox, or <c>null</c> on failure.</returns>
    public async Task<PagedResult<NotificationOutput>?> GetInboxAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<PagedResult<NotificationOutput>>(
                $"api/notifications/mine?page={page}&pageSize={pageSize}", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Notification inbox fetch failed.");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Result-returning helpers used by the new citizen-portal pages. These map
    // non-2xx responses to Result<T>.Failure with a stable ErrorCodes value so
    // the UI layer can render error alerts without exception handling.
    // -------------------------------------------------------------------------

    /// <summary>
    /// UC06 — fetch the calling user's applications as a paged result.
    /// </summary>
    /// <param name="page">1-based page index. Defaults to 1.</param>
    /// <param name="pageSize">Items per page. Defaults to 20.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}"/> wrapping the paged list on success, or an error code
    /// from <see cref="ErrorCodes"/> on HTTP/transport failure.
    /// </returns>
    public Task<Result<PagedResult<ApplicationListItemOutput>>> GetMyApplicationsAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
        => GetAsync<PagedResult<ApplicationListItemOutput>>(
            $"api/applications/mine?page={page}&pageSize={pageSize}", cancellationToken);

    /// <summary>
    /// UC06 — fetch a single application by its Sqid identifier.
    /// </summary>
    /// <param name="id">Sqid-encoded application id (string, never raw long — per RULE 3).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Application output or a failure result.</returns>
    public Task<Result<ApplicationOutput>> GetApplicationAsync(
        string id, CancellationToken cancellationToken = default)
        => GetAsync<ApplicationOutput>($"api/applications/{Uri.EscapeDataString(id)}", cancellationToken);

    /// <summary>
    /// UC06 — submit a new application.
    /// </summary>
    /// <param name="input">The submission payload (service-passport Sqid + form JSON + attachment Sqids).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}"/> carrying the new application's Sqid on success. On HTTP 4xx the body
    /// is propagated verbatim into <see cref="Result{T}.ErrorMessage"/> so the UI can display
    /// server-side validation messages.
    /// </returns>
    public async Task<Result<string>> SubmitApplicationAsync(
        SubmitApplicationInput input, CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync("api/applications", input, JsonOpts, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result<string>.Failure(MapStatusCode(resp.StatusCode), body);
            }

            var created = await resp.Content.ReadFromJsonAsync<ApplicationOutput>(JsonOpts, cancellationToken).ConfigureAwait(false);
            if (created is null)
            {
                return Result<string>.Failure(ErrorCodes.Internal, "Empty response body.");
            }
            return Result<string>.Success(created.Id);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Submit application request failed.");
            return Result<string>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Submit application request timed out.");
            return Result<string>.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// UC06 — withdraw an in-flight application by Sqid.
    /// </summary>
    /// <param name="id">Sqid-encoded application id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success <see cref="Result"/>, or failure with a mapped error code.</returns>
    public async Task<Result> WithdrawApplicationAsync(
        string id, CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await _http.PostAsync(
                $"api/applications/{Uri.EscapeDataString(id)}/withdraw",
                content: null,
                cancellationToken).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode) return Result.Success();

            var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
            return Result.Failure(MapStatusCode(resp.StatusCode), body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Withdraw application request failed.");
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Withdraw application request timed out.");
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// UC15 — list available service passports (the "new application" picker).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the list of passports or a transport failure code.</returns>
    public Task<Result<IReadOnlyList<ServicePassportListItem>>> ListServicePassportsAsync(
        CancellationToken cancellationToken = default)
        => GetAsync<IReadOnlyList<ServicePassportListItem>>("api/service-passports", cancellationToken);

    /// <summary>
    /// UC15 — fetch a single service-passport detail including the form schema JSON.
    /// </summary>
    /// <param name="id">Sqid-encoded service passport id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the passport detail or a failure code.</returns>
    public Task<Result<ServicePassportDetailOutput>> GetServicePassportAsync(
        string id, CancellationToken cancellationToken = default)
        => GetAsync<ServicePassportDetailOutput>($"api/service-passports/{Uri.EscapeDataString(id)}", cancellationToken);

    /// <summary>
    /// UC13 — fetch the calling user's profile (display name, language preference, etc).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the profile or a transport failure code (used to detect anonymous callers).</returns>
    public Task<Result<ProfileOutput>> GetMyProfileAsync(CancellationToken cancellationToken = default)
        => GetAsync<ProfileOutput>("api/profile/me", cancellationToken);

    /// <summary>
    /// UC13 — terminates the caller's cookie session via <c>POST /api/auth/logout</c>.
    /// </summary>
    /// <remarks>
    /// CSRF: the backend relies on SameSite cookie semantics rather than an anti-forgery
    /// token because logout carries no business risk if replayed by a third-party site —
    /// the worst case is the user is signed out, which is exactly what they asked for.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result, or a mapped failure (network / authz / timeout).</returns>
    public Task<Result> LogoutAsync(CancellationToken cancellationToken = default)
        => PostEmptyAsync("api/auth/logout", cancellationToken);

    /// <summary>
    /// R0361 / UC13 — pushes the citizen's contact-field edit through
    /// <c>PUT /api/profile/contact</c>. Wraps the response in a
    /// <see cref="Result"/> so the <c>MyProfile.razor</c> page can render
    /// the inline error region without try/catch noise.
    /// </summary>
    /// <param name="input">Allowed mutations — DisplayName, Email, Phone.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success on 204, or a failure carrying the body of the 4xx response.</returns>
    public Task<Result> UpdateMyContactAsync(
        ProfileContactInput input, CancellationToken cancellationToken = default)
        => PutJsonAsync("api/profile/contact", input, cancellationToken);

    /// <summary>
    /// UC22 — fetch the calling user's notification inbox as a paged result.
    /// Mirror of the legacy <see cref="GetInboxAsync"/> but returning a
    /// <see cref="Result{T}"/> so callers can render error alerts without
    /// try/catch noise (per CLAUDE.md §2.1).
    /// </summary>
    /// <param name="page">1-based page index. Defaults to 1.</param>
    /// <param name="pageSize">Items per page. Defaults to 20.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}"/> wrapping the paged inbox on success, or an error
    /// code from <see cref="ErrorCodes"/> on HTTP/transport failure.
    /// </returns>
    public Task<Result<PagedResult<NotificationOutput>>> GetMyInboxAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
        => GetAsync<PagedResult<NotificationOutput>>(
            $"api/notifications/mine?page={page}&pageSize={pageSize}", cancellationToken);

    /// <summary>
    /// R0371 — filtered notification history fetch for the dashboard history view.
    /// Adds <paramref name="unreadOnly"/> + <paramref name="channel"/> query params
    /// over <see cref="GetMyInboxAsync(int, int, CancellationToken)"/>; otherwise
    /// the contract is identical.
    /// </summary>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="unreadOnly">When <c>true</c>, returns only rows with <c>readAtUtc == null</c>.</param>
    /// <param name="channel">
    /// Optional channel filter — <c>InApp</c>, <c>Email</c>, or <c>Sms</c>; case-insensitive.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="Result{T}"/> wrapping the paged inbox.</returns>
    public Task<Result<PagedResult<NotificationOutput>>> GetMyInboxHistoryAsync(
        int page = 1,
        int pageSize = 20,
        bool unreadOnly = false,
        string? channel = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/notifications/mine?page={page}&pageSize={pageSize}";
        if (unreadOnly) url += "&unreadOnly=true";
        if (!string.IsNullOrWhiteSpace(channel)) url += $"&channel={Uri.EscapeDataString(channel)}";
        return GetAsync<PagedResult<NotificationOutput>>(url, cancellationToken);
    }

    /// <summary>
    /// R0371 — bulk-marks the caller's unread notifications as read. Mirrors the
    /// service-side <c>MarkAllReadAsync</c> shape — returns the row count flipped.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="Result{T}"/> with the number of rows transitioned.</returns>
    public Task<Result> MarkAllNotificationsReadAsync(CancellationToken cancellationToken = default)
        => PostEmptyAsync("api/notifications/mine/mark-all-read", cancellationToken);

    /// <summary>
    /// R0381 / UC05 — supervisor team queue fetch. Reads tasks assigned to peers of
    /// the calling supervisor with optional assignee + paging filters.
    /// </summary>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Items per page (server clamps to [1, 50]).</param>
    /// <param name="assigneeSqid">Optional Sqid filter on the task assignee.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged supervisor-view DTO; failure on network / authz errors.</returns>
    public Task<Result<PagedResult<SupervisorTeamTaskDto>>> GetSupervisorTeamQueueAsync(
        int page = 1, int pageSize = 20, string? assigneeSqid = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/tasks/supervisor/team?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(assigneeSqid))
        {
            url += $"&assignee={Uri.EscapeDataString(assigneeSqid)}";
        }
        return GetAsync<PagedResult<SupervisorTeamTaskDto>>(url, cancellationToken);
    }

    /// <summary>
    /// R0381 / CF 16.11 — reassign a task from the supervisor workspace. Posts the
    /// <see cref="TaskReassignInputDto"/> body to <c>POST /api/tasks/{sqid}/reassign</c>.
    /// </summary>
    /// <param name="taskSqid">Sqid-encoded workflow-task id.</param>
    /// <param name="input">Reassign payload — Sqid + reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result, or a mapped failure code.</returns>
    public Task<Result> ReassignTaskAsync(
        string taskSqid, TaskReassignInputDto input, CancellationToken cancellationToken = default)
        => PostJsonAsync(
            $"api/tasks/{Uri.EscapeDataString(taskSqid)}/reassign",
            input,
            cancellationToken);

    // -------------------------------------------------------------------------
    // Staff-portal helpers — UC05 task inbox, UC08 examiner workspace,
    // UC10 decider workflow, UC18 user administration, UC15 service-passport
    // catalog. All ids are Sqid strings per CLAUDE.md RULE 3.
    // -------------------------------------------------------------------------

    /// <summary>
    /// UC05 — list workflow tasks assigned to the calling examiner (or their groups).
    /// </summary>
    /// <param name="page">1-based page index. Defaults to 1.</param>
    /// <param name="pageSize">Items per page. Defaults to 20.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged <see cref="TaskInboxItem"/> result, or a transport failure code.</returns>
    public Task<Result<PagedResult<TaskInboxItem>>> GetMyTasksAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
        => GetAsync<PagedResult<TaskInboxItem>>(
            $"api/tasks/mine?page={page}&pageSize={pageSize}", cancellationToken);

    /// <summary>
    /// UC05 — claim a task from a group inbox so it's assigned to the caller.
    /// </summary>
    /// <param name="taskId">Sqid-encoded task id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success <see cref="Result"/>, or failure with a mapped error code.</returns>
    public Task<Result> ClaimTaskAsync(string taskId, CancellationToken cancellationToken = default)
        => PostEmptyAsync($"api/tasks/{Uri.EscapeDataString(taskId)}/claim", cancellationToken);

    /// <summary>
    /// UC05 — complete a task with a free-form JSON result payload (workflow continues).
    /// </summary>
    /// <param name="taskId">Sqid-encoded task id.</param>
    /// <param name="resultJson">Stringified JSON payload supplied by the examiner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success <see cref="Result"/>, or failure with a mapped error code.</returns>
    public Task<Result> CompleteTaskAsync(
        string taskId, string resultJson, CancellationToken cancellationToken = default)
        => PostJsonAsync(
            $"api/tasks/{Uri.EscapeDataString(taskId)}/complete",
            new { resultJson },
            cancellationToken);

    /// <summary>
    /// UC10 — record the decider's approval for a dossier (with optional note).
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="note">Optional approver note.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success <see cref="Result"/>, or failure with a mapped error code.</returns>
    public Task<Result> ApproveDossierAsync(
        string dossierId, string? note, CancellationToken cancellationToken = default)
        => PostJsonAsync(
            $"api/decisions/{Uri.EscapeDataString(dossierId)}/approve",
            new { note },
            cancellationToken);

    /// <summary>
    /// UC10 — record the decider's rejection together with a mandatory reason.
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="reason">Human-readable rejection reason; required by the API.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success <see cref="Result"/>, or failure with a mapped error code.</returns>
    public Task<Result> RejectDossierAsync(
        string dossierId, string reason, CancellationToken cancellationToken = default)
        => PostJsonAsync(
            $"api/decisions/{Uri.EscapeDataString(dossierId)}/reject",
            new { reason },
            cancellationToken);

    /// <summary>
    /// UC08.04 — request auto-generation of Fișa de calcul + Decizia drafts.
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Result wrapping the pair of newly-created document Sqids, or a transport failure code.
    /// </returns>
    public async Task<Result<DraftDocumentsResult>> GenerateDraftsAsync(
        string dossierId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await _http.PostAsync(
                $"api/examination/dossiers/{Uri.EscapeDataString(dossierId)}/generate-drafts",
                content: null,
                cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result<DraftDocumentsResult>.Failure(MapStatusCode(resp.StatusCode), body);
            }

            var value = await resp.Content.ReadFromJsonAsync<DraftDocumentsResult>(JsonOpts, cancellationToken).ConfigureAwait(false);
            return value is null
                ? Result<DraftDocumentsResult>.Failure(ErrorCodes.Internal, "Empty response body.")
                : Result<DraftDocumentsResult>.Success(value);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Generate drafts request failed.");
            return Result<DraftDocumentsResult>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Generate drafts request timed out.");
            return Result<DraftDocumentsResult>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Generate drafts returned malformed JSON.");
            return Result<DraftDocumentsResult>.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// UC08.06 — examiner forwards the dossier to the șef-direcție for approval.
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success <see cref="Result"/>, or failure with a mapped error code.</returns>
    public Task<Result> SubmitForApprovalAsync(
        string dossierId, CancellationToken cancellationToken = default)
        => PostEmptyAsync(
            $"api/examination/dossiers/{Uri.EscapeDataString(dossierId)}/submit-for-approval",
            cancellationToken);

    /// <summary>
    /// UC08.06 (alt) — examiner refuses the dossier outright with a mandatory reason.
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="reason">Mandatory human-readable reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success <see cref="Result"/>, or failure with a mapped error code.</returns>
    public Task<Result> RefuseDossierAsync(
        string dossierId, string reason, CancellationToken cancellationToken = default)
        => PostJsonAsync(
            $"api/examination/dossiers/{Uri.EscapeDataString(dossierId)}/refuse",
            new { reason },
            cancellationToken);

    /// <summary>
    /// R0332 / CF 12.02 — fetch the consolidated electronic-archive summary
    /// rendered above each tab on the <c>/archive</c> Web UI. Single round-
    /// trip; the payload is depersonalised (counts + last-updated timestamps).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The summary <see cref="ArchiveSummaryDto"/>, or a transport failure.</returns>
    public Task<Result<ArchiveSummaryDto>> GetArchiveSummaryAsync(CancellationToken cancellationToken = default)
        => GetAsync<ArchiveSummaryDto>("api/archive/summary", cancellationToken);

    /// <summary>
    /// R0590 / TOR CF 10.01 — fetches the chip-strip summary aggregate rendered
    /// at the top of the decider's approval workspace (<c>/approvals</c>).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Result wrapping the summary DTO, or a transport / authz failure.
    /// </returns>
    public Task<Result<ApprovalWorkspaceSummaryDto>> GetApprovalSummaryAsync(
        CancellationToken cancellationToken = default)
        => GetAsync<ApprovalWorkspaceSummaryDto>("api/approvals/summary", cancellationToken);

    /// <summary>
    /// R0590 / TOR CF 10.01 — fetches a paged window of the decider's pending
    /// decisions. Maps 1:1 to <c>GET /api/approvals/pending</c>.
    /// </summary>
    /// <param name="page">1-based page index. Defaults to 1.</param>
    /// <param name="pageSize">Items per page. Defaults to 20; server clamps to [1, 100].</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Result wrapping the paged decisions, or a transport / authz failure.
    /// </returns>
    public Task<Result<PagedResult<ApprovalQueueItemDto>>> GetPendingApprovalsAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
        => GetAsync<PagedResult<ApprovalQueueItemDto>>(
            $"api/approvals/pending?page={page}&pageSize={pageSize}", cancellationToken);

    /// <summary>
    /// R0332 / CF 12.02 — proxies the Annex 1 contributors search through the
    /// existing <c>GET /api/contributors</c> endpoint. Used by the
    /// <c>Contributors</c> tab on the archive page; <see cref="GetArchiveSummaryAsync"/>
    /// supplies the chip counts, this call supplies the paged list.
    /// </summary>
    /// <param name="query">Optional substring filter on Denumire / IDNO.</param>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged contributor list-items.</returns>
    public Task<Result<PagedResult<ContributorListItem>>> SearchContributorsAsync(
        string? query, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var url = $"api/contributors?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(query)) url += $"&q={Uri.EscapeDataString(query)}";
        return GetAsync<PagedResult<ContributorListItem>>(url, cancellationToken);
    }

    /// <summary>
    /// R0332 / CF 12.02 — proxies the Annex 2 insured-persons search through
    /// the existing <c>GET /api/insured-persons</c> endpoint. Used by the
    /// <c>InsuredPersons</c> tab on the archive page.
    /// </summary>
    /// <param name="query">Optional substring filter on name / IDNP.</param>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged insured-person list-items.</returns>
    public Task<Result<PagedResult<InsuredPersonListItem>>> SearchInsuredPersonsAsync(
        string? query, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var url = $"api/insured-persons?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(query)) url += $"&query={Uri.EscapeDataString(query)}";
        return GetAsync<PagedResult<InsuredPersonListItem>>(url, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // R0611 / TOR CF 12.02 — per-record tabbed detail UI helpers. Used by the
    // ContributorDetail / InsuredPersonDetail Blazor pages. Every id is a
    // Sqid per CLAUDE.md RULE 3.
    // -------------------------------------------------------------------------

    /// <summary>
    /// R0611 / TOR CF 12.02 — fetch a single contributor's primary attributes
    /// for the detail page's Identity tab.
    /// </summary>
    /// <param name="sqid">Sqid-encoded contributor id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detail DTO or a transport failure code.</returns>
    public Task<Result<ContributorOutput>> GetContributorAsync(
        string sqid, CancellationToken cancellationToken = default)
        => GetAsync<ContributorOutput>(
            $"api/contributors/{Uri.EscapeDataString(sqid)}", cancellationToken);

    /// <summary>
    /// R0611 / R0302 / TOR CF 12.02 — fetch the source-history page for a
    /// contributor. Surfaces the "Contributions" tab on the detail page.
    /// </summary>
    /// <param name="sqid">Sqid-encoded contributor id.</param>
    /// <param name="skip">0-based offset.</param>
    /// <param name="take">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The paged history projection or a transport failure code.</returns>
    public Task<Result<ContributorSourceChangeHistoryPageDto>> GetContributorSourceHistoryAsync(
        string sqid, int skip = 0, int take = 20, CancellationToken cancellationToken = default)
        => GetAsync<ContributorSourceChangeHistoryPageDto>(
            $"api/contributors/{Uri.EscapeDataString(sqid)}/source-history?skip={skip}&take={take}",
            cancellationToken);

    /// <summary>
    /// R0611 / TOR CF 12.02 — triggers the per-tab export for the contributor
    /// detail page. Reuses the iter-125 <c>RegistryExportProjection</c>
    /// pipeline by hitting <c>GET /api/contributors?format=xlsx</c> filtered to
    /// the contributor's IDNO (so the user gets the canonical column set).
    /// </summary>
    /// <param name="sqid">Sqid-encoded contributor id.</param>
    /// <param name="tabKey">Active tab discriminator (currently for telemetry only).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success on download, or a transport failure code.</returns>
    public async Task<Result> DownloadContributorTabExportAsync(
        string sqid, string tabKey, CancellationToken cancellationToken = default)
    {
        _ = tabKey; // kept for future per-tab routing
        var url = $"api/contributors?q={Uri.EscapeDataString(sqid)}&format=Xlsx";
        try
        {
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result.Failure(MapStatusCode(resp.StatusCode), body);
            }
            // Drain the body so the HttpClient releases the connection. The Blazor
            // page only needs to know the call succeeded — actual download UX is
            // handed off to the browser via a future window.location redirect.
            _ = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Contributor tab export failed.");
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Contributor tab export timed out.");
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// R0611 / TOR CF 12.02 — fetch a single insured person's primary attributes
    /// for the detail page's Identity tab.
    /// </summary>
    /// <param name="sqid">Sqid-encoded insured-person id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detail DTO or a transport failure code.</returns>
    public Task<Result<InsuredPersonOutput>> GetInsuredPersonAsync(
        string sqid, CancellationToken cancellationToken = default)
        => GetAsync<InsuredPersonOutput>(
            $"api/insured-persons/{Uri.EscapeDataString(sqid)}", cancellationToken);

    /// <summary>
    /// R0611 / TOR CF 12.02 — triggers the per-tab export for the insured
    /// person detail page. Reuses the iter-125 pipeline by hitting
    /// <c>GET /api/insured-persons?format=xlsx</c> filtered to the IDNP.
    /// </summary>
    /// <param name="sqid">Sqid-encoded insured-person id.</param>
    /// <param name="tabKey">Active tab discriminator (currently for telemetry only).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success on download, or a transport failure code.</returns>
    public async Task<Result> DownloadInsuredPersonTabExportAsync(
        string sqid, string tabKey, CancellationToken cancellationToken = default)
    {
        _ = tabKey;
        var url = $"api/insured-persons?query={Uri.EscapeDataString(sqid)}&format=Xlsx";
        try
        {
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result.Failure(MapStatusCode(resp.StatusCode), body);
            }
            _ = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Insured-person tab export failed.");
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Insured-person tab export timed out.");
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// UC18 — list user accounts with paging (admin only).
    /// </summary>
    /// <param name="page">1-based page index. Defaults to 1.</param>
    /// <param name="pageSize">Items per page. Defaults to 20.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged <see cref="UserListItem"/> result, or a transport failure code.</returns>
    public Task<Result<PagedResult<UserListItem>>> ListUsersAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
        => GetAsync<PagedResult<UserListItem>>(
            $"api/users?page={page}&pageSize={pageSize}", cancellationToken);

    /// <summary>
    /// UC18 — grant a role to a user.
    /// </summary>
    /// <param name="userId">Sqid-encoded user id.</param>
    /// <param name="role">Role name (e.g. <c>cnas-decider</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success <see cref="Result"/>, or failure with a mapped error code.</returns>
    public Task<Result> GrantRoleAsync(
        string userId, string role, CancellationToken cancellationToken = default)
        => PostJsonAsync(
            $"api/users/{Uri.EscapeDataString(userId)}/roles/grant",
            new { role },
            cancellationToken);

    /// <summary>
    /// UC18 — revoke a role from a user.
    /// </summary>
    /// <param name="userId">Sqid-encoded user id.</param>
    /// <param name="role">Role name to revoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success <see cref="Result"/>, or failure with a mapped error code.</returns>
    public Task<Result> RevokeRoleAsync(
        string userId, string role, CancellationToken cancellationToken = default)
        => PostJsonAsync(
            $"api/users/{Uri.EscapeDataString(userId)}/roles/revoke",
            new { role },
            cancellationToken);

    /// <summary>
    /// UC18 — lock a user account (manual override).
    /// </summary>
    /// <param name="userId">Sqid-encoded user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success <see cref="Result"/>, or failure with a mapped error code.</returns>
    public Task<Result> LockUserAsync(string userId, CancellationToken cancellationToken = default)
        => PostEmptyAsync($"api/users/{Uri.EscapeDataString(userId)}/lock", cancellationToken);

    /// <summary>
    /// UC18 — unlock a previously locked user account.
    /// </summary>
    /// <param name="userId">Sqid-encoded user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success <see cref="Result"/>, or failure with a mapped error code.</returns>
    public Task<Result> UnlockUserAsync(string userId, CancellationToken cancellationToken = default)
        => PostEmptyAsync($"api/users/{Uri.EscapeDataString(userId)}/unlock", cancellationToken);

    /// <summary>
    /// UC15 — list service passports (alias of <see cref="ListServicePassportsAsync"/>).
    /// Kept for the staff-portal naming convention used by the new admin pages.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the list of passports or a transport failure code.</returns>
    public Task<Result<IReadOnlyList<ServicePassportListItem>>> ListPassportsAsync(
        CancellationToken cancellationToken = default)
        => ListServicePassportsAsync(cancellationToken);

    /// <summary>
    /// R0640 / TOR CF 15.01-15.04 — fetches a single service passport by its
    /// stable logical <paramref name="code"/>. The admin edit screen uses the
    /// code in the URL (not the Sqid) because the code is the user-facing
    /// identifier — the underlying API surface accepts either by Sqid OR by
    /// code, and the resolver dispatches accordingly.
    /// </summary>
    /// <param name="code">Stable logical passport code OR Sqid id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The passport detail, or a transport failure code.</returns>
    public Task<Result<ServicePassportDetailOutput>> GetServicePassportByCodeAsync(
        string code, CancellationToken cancellationToken = default)
        => GetAsync<ServicePassportDetailOutput>(
            $"api/service-passports/{Uri.EscapeDataString(code)}", cancellationToken);

    /// <summary>
    /// R0640 / TOR CF 15.01-15.04 — pushes the edited
    /// <see cref="ServicePassportInput"/> through the existing
    /// <c>PUT /api/service-passports/{code}</c> endpoint. On 4xx the
    /// response body is surfaced verbatim into the failure's
    /// <see cref="Result.ErrorMessage"/> so the admin UI can render the
    /// server's validation detail inline.
    /// </summary>
    /// <param name="code">Stable logical passport code OR Sqid id.</param>
    /// <param name="input">Replacement passport body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result, or a mapped failure.</returns>
    public Task<Result> UpdateServicePassportAsync(
        string code, ServicePassportInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PutJsonAsync($"api/service-passports/{Uri.EscapeDataString(code)}", input, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // UC16 / R0121 — workflow definitions admin surface used by the visual
    // designer pages. Workflow codes are NOT Sqid-encoded (documented exception
    // on the controller — they ARE the public identifier). Bodies are passed
    // through verbatim so the textarea round-trips byte-for-byte; the server
    // validates the payload shape.
    // -------------------------------------------------------------------------

    /// <summary>
    /// R0121 / CF 16.02 — lists the workflows whose current definition is active,
    /// ordered alphabetically by code. Optional case-insensitive contains-filter
    /// applied against <c>Code</c>.
    /// </summary>
    /// <param name="codeFilter">Optional substring filter on the workflow code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result wrapping the list, or a transport failure code.</returns>
    public Task<Result<IReadOnlyList<WorkflowDefinitionListItem>>> ListWorkflowDefinitionsAsync(
        string? codeFilter = null, CancellationToken cancellationToken = default)
    {
        var url = "api/workflows";
        if (!string.IsNullOrWhiteSpace(codeFilter))
        {
            url += $"?codeFilter={Uri.EscapeDataString(codeFilter)}";
        }
        return GetAsync<IReadOnlyList<WorkflowDefinitionListItem>>(url, cancellationToken);
    }

    /// <summary>
    /// R0121 / CF 16.02 — fetches the raw definition body for <paramref name="workflowCode"/>.
    /// The body is returned verbatim as a string so the textarea can round-trip it
    /// without any normalisation pass. Production bodies are JSON (see
    /// <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition.DefinitionJson"/>); the
    /// front-end's <c>WorkflowGraphParser</c> additionally accepts the simpler
    /// <c>&lt;workflow&gt;</c> XML envelope for the visual preview.
    /// </summary>
    /// <param name="workflowCode">Workflow code (NOT Sqid — see controller XML doc).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result wrapping the raw body string, or a transport failure code.</returns>
    public async Task<Result<string>> GetWorkflowDefinitionBodyAsync(
        string workflowCode, CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await _http.GetAsync(
                $"api/workflows/{Uri.EscapeDataString(workflowCode)}", cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result<string>.Failure(MapStatusCode(resp.StatusCode), body);
            }
            var value = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Result<string>.Success(value);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Get workflow definition body failed.");
            return Result<string>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Get workflow definition body timed out.");
            return Result<string>.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// R0121 / CF 16.02 — saves the supplied definition body for
    /// <paramref name="workflowCode"/>. The server validates the payload
    /// is well-formed JSON (the production schema); XML bodies fail at the
    /// service-layer validator with <see cref="ErrorCodes.ValidationFailed"/>
    /// — see the documented iteration-91 MVP scope.
    /// </summary>
    /// <param name="workflowCode">Workflow code (NOT Sqid — see controller XML doc).</param>
    /// <param name="body">The raw body to persist (production: JSON; preview accepts XML).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result on persist, or a mapped failure.</returns>
    public async Task<Result> SaveWorkflowDefinitionBodyAsync(
        string workflowCode, string body, CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new StringContent(body ?? string.Empty,
                System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync(
                $"api/workflows/{Uri.EscapeDataString(workflowCode)}", content, cancellationToken)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var responseBody = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result.Failure(MapStatusCode(resp.StatusCode), responseBody);
            }
            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Save workflow definition body failed.");
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Save workflow definition body timed out.");
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // R0141 / CF 15.03 — business-rule editor surface (admin only).
    // -------------------------------------------------------------------------

    /// <summary>
    /// R0141 / CF 15.03 — lists the business rules attached to the current
    /// revision of the passport addressed by <paramref name="passportCode"/>.
    /// </summary>
    /// <param name="passportCode">Stable logical passport code (NOT a Sqid).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result wrapping the list, or a transport failure.</returns>
    public Task<Result<IReadOnlyList<BusinessRuleDto>>> ListBusinessRulesAsync(
        string passportCode, CancellationToken cancellationToken = default)
    {
        var url = $"api/service-passports/{Uri.EscapeDataString(passportCode)}/business-rules";
        return GetAsync<IReadOnlyList<BusinessRuleDto>>(url, cancellationToken);
    }

    /// <summary>
    /// R0141 / CF 15.03 — creates or updates a business rule on the passport
    /// addressed by <paramref name="passportCode"/>. The <paramref name="input"/>'s
    /// <c>Id</c> property discriminates create (null) vs update (existing).
    /// </summary>
    /// <param name="passportCode">Stable logical passport code (NOT a Sqid).</param>
    /// <param name="input">Desired rule state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result wrapping the persisted rule, or a transport failure.</returns>
    public async Task<Result<BusinessRuleDto>> UpsertBusinessRuleAsync(
        string passportCode, BusinessRuleInputDto input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var url = $"api/service-passports/{Uri.EscapeDataString(passportCode)}/business-rules";
        try
        {
            using var content = JsonContent.Create(input, options: JsonOpts);
            using var resp = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result<BusinessRuleDto>.Failure(MapStatusCode(resp.StatusCode), body);
            }
            var value = await resp.Content.ReadFromJsonAsync<BusinessRuleDto>(JsonOpts, cancellationToken).ConfigureAwait(false);
            return value is null
                ? Result<BusinessRuleDto>.Failure(ErrorCodes.Internal, "Empty response body.")
                : Result<BusinessRuleDto>.Success(value);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "POST {Url} failed.", url);
            return Result<BusinessRuleDto>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "POST {Url} timed out.", url);
            return Result<BusinessRuleDto>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "POST {Url} returned malformed JSON.", url);
            return Result<BusinessRuleDto>.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// R0141 / CF 15.03 — deletes the business rule addressed by
    /// <paramref name="ruleId"/> from the passport's current revision.
    /// </summary>
    /// <param name="passportCode">Stable logical passport code (NOT a Sqid).</param>
    /// <param name="ruleId">Opaque stable rule id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result, or a transport failure.</returns>
    public async Task<Result> DeleteBusinessRuleAsync(
        string passportCode, string ruleId, CancellationToken cancellationToken = default)
    {
        var url = $"api/service-passports/{Uri.EscapeDataString(passportCode)}/business-rules/{Uri.EscapeDataString(ruleId)}";
        try
        {
            using var resp = await _http.DeleteAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result.Failure(MapStatusCode(resp.StatusCode), body);
            }
            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "DELETE {Url} failed.", url);
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "DELETE {Url} timed out.", url);
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// R0204 / TOR CF 20.07-08 — lists the current state of every Quartz job + trigger
    /// registered with the running scheduler. Backs the admin Jobs dashboard table.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result wrapping the (possibly empty) list, or a transport failure.</returns>
    public Task<Result<IReadOnlyList<JobStateDto>>> ListJobStatesAsync(
        CancellationToken cancellationToken = default)
        => GetAsync<IReadOnlyList<JobStateDto>>("api/automation/jobs/state", cancellationToken);

    /// <summary>
    /// R0204 / TOR CF 20.07-08 — pages the failed-jobs dead-letter queue, newest first.
    /// Optional <paramref name="jobName"/> narrows to a single Quartz job.
    /// </summary>
    /// <param name="jobName">When supplied, restricts the page to one Quartz job name.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Items per page (server clamps to [1, 200]).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result wrapping the paged list, or a transport failure.</returns>
    public Task<Result<PagedResult<FailedJobOutput>>> ListFailedJobsAsync(
        string? jobName,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/admin/failed-jobs?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(jobName))
        {
            url += $"&jobName={Uri.EscapeDataString(jobName)}";
        }
        return GetAsync<PagedResult<FailedJobOutput>>(url, cancellationToken);
    }

    /// <summary>
    /// R0204 / TOR CF 20.07-08 — fires the named automation immediately. Maps to
    /// <c>POST /api/automation/{code}/run-now</c> with an empty parameter map.
    /// </summary>
    /// <param name="jobCode">Stable Quartz job name (NOT a Sqid).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result, or a transport failure.</returns>
    public Task<Result> TriggerJobAsync(
        string jobCode, CancellationToken cancellationToken = default)
        => PostEmptyAsync(
            $"api/automation/{Uri.EscapeDataString(jobCode)}/run-now",
            cancellationToken);

    /// <summary>
    /// R0204 / TOR CF 20.07-08 — schedules a one-shot replay of the supplied DLQ entry
    /// via the admin replay endpoint. The server fires the original Quartz job with the
    /// original (PII-scrubbed) job data map.
    /// </summary>
    /// <param name="failedJobSqid">Sqid-encoded DLQ row id (RULE 3 boundary).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result, or a transport failure.</returns>
    public Task<Result> ReplayFailedJobAsync(
        string failedJobSqid, CancellationToken cancellationToken = default)
        => PostEmptyAsync(
            $"api/admin/failed-jobs/{Uri.EscapeDataString(failedJobSqid)}/replay",
            cancellationToken);

    /// <summary>
    /// R0200 / TOR CF 20.01-03, MR 012 — lists every Quartz job with its current
    /// effective cron expression (override-or-default). Backs the admin Cron-schedules
    /// dashboard.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result wrapping the (possibly empty) list of job-schedule rows.</returns>
    public Task<Result<IReadOnlyList<JobScheduleOverrideDto>>> ListCronSchedulesAsync(
        CancellationToken cancellationToken = default)
        => GetAsync<IReadOnlyList<JobScheduleOverrideDto>>(
            "api/automation/schedules", cancellationToken);

    /// <summary>
    /// R0200 / TOR CF 20.01-03, MR 012 — upserts the cron expression on the named
    /// Quartz job. The server validates the expression and applies the change to the
    /// live scheduler.
    /// </summary>
    /// <param name="jobCode">Stable Quartz job code (NOT a Sqid).</param>
    /// <param name="cronExpression">New Quartz cron expression.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result, or a transport / validation failure.</returns>
    public Task<Result> UpsertCronScheduleAsync(
        string jobCode,
        string cronExpression,
        CancellationToken cancellationToken = default)
        => PostJsonAsync(
            $"api/automation/schedules/{Uri.EscapeDataString(jobCode)}",
            new CronExpressionInputDto(cronExpression),
            cancellationToken);

    /// <summary>
    /// R0200 / TOR CF 20.01-03, MR 012 — pauses the named Quartz job.
    /// </summary>
    /// <param name="jobCode">Stable Quartz job code (NOT a Sqid).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result, or a transport failure.</returns>
    public Task<Result> PauseCronScheduleAsync(
        string jobCode,
        CancellationToken cancellationToken = default)
        => PostEmptyAsync(
            $"api/automation/schedules/{Uri.EscapeDataString(jobCode)}/pause",
            cancellationToken);

    /// <summary>
    /// R0200 / TOR CF 20.01-03, MR 012 — resumes a previously-paused Quartz job.
    /// </summary>
    /// <param name="jobCode">Stable Quartz job code (NOT a Sqid).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result, or a transport failure.</returns>
    public async Task<Result> ResumeCronScheduleAsync(
        string jobCode,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/automation/schedules/{Uri.EscapeDataString(jobCode)}/pause";
        try
        {
            using var resp = await _http.DeleteAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result.Failure(MapStatusCode(resp.StatusCode), body);
            }
            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "DELETE {Url} failed.", url);
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "DELETE {Url} timed out.", url);
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Internals.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Issues a GET, parses the JSON body, and maps non-2xx responses to a
    /// <see cref="Result{T}"/> failure with a stable error code.
    /// </summary>
    /// <typeparam name="T">Response body type.</typeparam>
    /// <param name="url">Relative or absolute URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the deserialised body or a failure code.</returns>
    private async Task<Result<T>> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result<T>.Failure(MapStatusCode(resp.StatusCode), body);
            }

            var value = await resp.Content.ReadFromJsonAsync<T>(JsonOpts, cancellationToken).ConfigureAwait(false);
            return value is null
                ? Result<T>.Failure(ErrorCodes.Internal, "Empty response body.")
                : Result<T>.Success(value);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "GET {Url} failed.", url);
            return Result<T>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "GET {Url} timed out.", url);
            return Result<T>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "GET {Url} returned malformed JSON.", url);
            return Result<T>.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// Issues a POST with no request body and maps non-2xx responses to a
    /// <see cref="Result"/> failure with a stable error code. Used for state-changing
    /// endpoints that take no input (claim/lock/unlock/submit-for-approval/...).
    /// </summary>
    /// <param name="url">Relative or absolute URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result or a mapped failure.</returns>
    private async Task<Result> PostEmptyAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.PostAsync(url, content: null, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result.Failure(MapStatusCode(resp.StatusCode), body);
            }
            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "POST {Url} failed.", url);
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "POST {Url} timed out.", url);
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// R0932 — POST the supplied edited Fișa rows and return the refreshed total.
    /// Maps to <c>POST /api/decisions/fisa-de-calcul/recalc</c>.
    /// </summary>
    /// <param name="input">Operator-edited row set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result wrapping the refreshed envelope, or a transport failure.</returns>
    public async Task<Result<FisaDeCalculRecalcResultDto>> RecalculateFisaDeCalculAsync(
        FisaDeCalculRecalcInputDto input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = JsonContent.Create(input, options: JsonOpts);
            using var resp = await _http.PostAsync(
                "api/decisions/fisa-de-calcul/recalc", content, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result<FisaDeCalculRecalcResultDto>.Failure(MapStatusCode(resp.StatusCode), body);
            }
            var value = await resp.Content.ReadFromJsonAsync<FisaDeCalculRecalcResultDto>(
                JsonOpts, cancellationToken).ConfigureAwait(false);
            return value is null
                ? Result<FisaDeCalculRecalcResultDto>.Failure(ErrorCodes.Internal, "Empty response body.")
                : Result<FisaDeCalculRecalcResultDto>.Success(value);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "POST /api/decisions/fisa-de-calcul/recalc failed.");
            return Result<FisaDeCalculRecalcResultDto>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "POST /api/decisions/fisa-de-calcul/recalc timed out.");
            return Result<FisaDeCalculRecalcResultDto>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "POST /api/decisions/fisa-de-calcul/recalc returned malformed JSON.");
            return Result<FisaDeCalculRecalcResultDto>.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// Issues a POST with a JSON-serialized body and maps non-2xx responses to a
    /// <see cref="Result"/> failure with a stable error code. The response body is
    /// ignored when present — callers that need the body should use the GET-style helper.
    /// </summary>
    /// <param name="url">Relative or absolute URL.</param>
    /// <param name="body">The request body to serialize as JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result or a mapped failure.</returns>
    private async Task<Result> PostJsonAsync(string url, object body, CancellationToken cancellationToken)
    {
        try
        {
            using var content = JsonContent.Create(body, options: JsonOpts);
            using var resp = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var responseBody = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result.Failure(MapStatusCode(resp.StatusCode), responseBody);
            }
            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "POST {Url} failed.", url);
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "POST {Url} timed out.", url);
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// Issues a PUT with a JSON-serialized body and maps non-2xx responses to a
    /// <see cref="Result"/> failure with a stable error code. Mirrors
    /// <see cref="PostJsonAsync(string, object, CancellationToken)"/> for the PUT
    /// verb — the response body is ignored when present.
    /// </summary>
    /// <param name="url">Relative or absolute URL.</param>
    /// <param name="body">The request body to serialize as JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty success result or a mapped failure.</returns>
    private async Task<Result> PutJsonAsync(string url, object body, CancellationToken cancellationToken)
    {
        try
        {
            using var content = JsonContent.Create(body, options: JsonOpts);
            using var resp = await _http.PutAsync(url, content, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var responseBody = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result.Failure(MapStatusCode(resp.StatusCode), responseBody);
            }
            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "PUT {Url} failed.", url);
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "PUT {Url} timed out.", url);
            return Result.Failure(ErrorCodes.Internal, ex.Message);
        }
    }

    /// <summary>
    /// Maps an HTTP status code onto the closest <see cref="ErrorCodes"/> constant.
    /// 401/403/404/409 map directly; everything else lands in <see cref="ErrorCodes.Internal"/>.
    /// </summary>
    /// <param name="status">HTTP status from the upstream response.</param>
    /// <returns>Stable error-code string.</returns>
    private static string MapStatusCode(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => ErrorCodes.Unauthorized,
        HttpStatusCode.Forbidden => ErrorCodes.Forbidden,
        HttpStatusCode.NotFound => ErrorCodes.NotFound,
        HttpStatusCode.Conflict => ErrorCodes.Conflict,
        HttpStatusCode.TooManyRequests => ErrorCodes.RateLimited,
        HttpStatusCode.BadRequest => ErrorCodes.ValidationFailed,
        _ => ErrorCodes.Internal,
    };

    /// <summary>
    /// Reads the response body as a string, swallowing read errors so we always
    /// have a fall-back error message rather than throwing on top of an HTTP failure.
    /// </summary>
    /// <param name="response">The HTTP response to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Body string (possibly empty); never throws.</returns>
    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// R1601 / R1604 / TOR Annex 3.9 — paged list of the canonical
    /// RegistrulDeciziilor projection.
    /// </summary>
    /// <param name="from">Optional inclusive lower bound on issuance UTC.</param>
    /// <param name="to">Optional exclusive upper bound on issuance UTC.</param>
    /// <param name="type">Optional stable type code (e.g. <c>DECIZIE_RECUPERARE_SUME</c>).</param>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The paged register-rows envelope.</returns>
    public Task<Result<PagedResult<DecisionRegisterRowDto>>> ListDecisionsRegisterAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? type = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/registers/decisions?page={page}&pageSize={pageSize}";
        if (from.HasValue) url += $"&from={Uri.EscapeDataString(from.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture))}";
        if (to.HasValue) url += $"&to={Uri.EscapeDataString(to.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture))}";
        if (!string.IsNullOrWhiteSpace(type)) url += $"&type={Uri.EscapeDataString(type)}";
        return GetAsync<PagedResult<DecisionRegisterRowDto>>(url, cancellationToken);
    }

    /// <summary>
    /// R1602 / R1604 / TOR Annex 3.10 — paged list of the
    /// RegistrulConturilorDePlata projection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The optional <paramref name="beneficiaryIdnp"/> filter is transmitted via
    /// the <c>X-Beneficiary-Idnp</c> request header rather than a query-string
    /// parameter. Query strings are routinely captured in reverse-proxy access
    /// logs, browser history, and HTTP referrer headers; surfacing a Moldovan
    /// national identifier through any of those channels would leak PII to
    /// logs that were not designed to carry it. Request headers are not logged
    /// by default by the reverse proxy fronting CNAS, which keeps the
    /// identifier out of the routine log corpus.
    /// </para>
    /// </remarks>
    /// <param name="beneficiaryIdnp">Optional raw IDNP filter (sent via <c>X-Beneficiary-Idnp</c> header).</param>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The paged register-rows envelope.</returns>
    public async Task<Result<PagedResult<BeneficiaryPaymentAccountRowDto>>> ListPaymentAccountsRegisterAsync(
        string? beneficiaryIdnp = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/registers/payment-accounts?page={page}&pageSize={pageSize}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(beneficiaryIdnp))
        {
            request.Headers.TryAddWithoutValidation("X-Beneficiary-Idnp", beneficiaryIdnp);
        }
        try
        {
            using var resp = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, cancellationToken).ConfigureAwait(false);
                return Result<PagedResult<BeneficiaryPaymentAccountRowDto>>.Failure(MapStatusCode(resp.StatusCode), body);
            }
            var value = await resp.Content
                .ReadFromJsonAsync<PagedResult<BeneficiaryPaymentAccountRowDto>>(JsonOpts, cancellationToken)
                .ConfigureAwait(false);
            return value is null
                ? Result<PagedResult<BeneficiaryPaymentAccountRowDto>>.Failure(ErrorCodes.Internal, "Empty response body.")
                : Result<PagedResult<BeneficiaryPaymentAccountRowDto>>.Success(value);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "GET {Url} failed.", url);
            return Result<PagedResult<BeneficiaryPaymentAccountRowDto>>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "GET {Url} timed out.", url);
            return Result<PagedResult<BeneficiaryPaymentAccountRowDto>>.Failure(ErrorCodes.Internal, ex.Message);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "GET {Url} returned malformed JSON.", url);
            return Result<PagedResult<BeneficiaryPaymentAccountRowDto>>.Failure(ErrorCodes.Internal, ex.Message);
        }
    }
}
