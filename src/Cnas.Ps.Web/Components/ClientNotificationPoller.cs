using Cnas.Ps.Web.Backend;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Web.Components;

/// <summary>
/// R0170 / TOR CF 22.02 — on-demand notification fetcher invoked by host
/// pages (e.g. <c>Inbox.razor</c>'s <c>OnAfterRenderAsync</c>) to refresh the
/// citizen's inbox and surface any newly-arrived unread rows as toasts. The
/// poller is intentionally pull-based rather than driven by a long-running
/// hosted service so it stays compatible with the bUnit test harness (which
/// disposes the component tree between tests — a HostedService timer would
/// fight that lifecycle and leak threads).
/// </summary>
/// <remarks>
/// <para>
/// <b>Side-effect surface.</b> Pulls <c>GET /api/notifications/mine</c> with
/// <c>unreadOnly=true</c>, filters out rows older than the last-seen-id
/// watermark (kept in-process), and enqueues a toast for each new arrival
/// via <see cref="IClientToastQueue.Enqueue"/>. The deep-link URL on the
/// notification row (R0172) flows through to the toast.
/// </para>
/// <para>
/// <b>Failure semantics.</b> Transport / authorization errors are swallowed
/// after a warning log — a missing poll is a transient UX regression, not a
/// data-corruption event. A successful refresh updates the watermark
/// regardless of how many toasts were emitted.
/// </para>
/// <para>
/// <b>Lifetime.</b> Scoped per Blazor circuit; the watermark must persist
/// across renders inside the same session but reset when the user signs out
/// (the cookie-auth state invalidation tears down the scope).
/// </para>
/// </remarks>
public sealed class ClientNotificationPoller
{
    private readonly CnasApiClient _api;
    private readonly IClientToastQueue _toasts;
    private readonly ILogger<ClientNotificationPoller> _logger;
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Constructs the poller with its collaborators.
    /// </summary>
    /// <param name="api">REST API client used to call <c>/api/notifications/mine</c>.</param>
    /// <param name="toasts">Per-circuit toast queue to push new arrivals onto.</param>
    /// <param name="logger">Structured logger for transport-failure diagnostics.</param>
    public ClientNotificationPoller(
        CnasApiClient api,
        IClientToastQueue toasts,
        ILogger<ClientNotificationPoller> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(toasts);
        ArgumentNullException.ThrowIfNull(logger);
        _api = api;
        _toasts = toasts;
        _logger = logger;
    }

    /// <summary>
    /// Issues a single notification-inbox fetch and emits a toast for every
    /// unread row whose monotonic id exceeds the current watermark. Idempotent
    /// across calls — repeated invocations with no new rows are no-ops.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The count of toasts emitted on this poll cycle.</returns>
    public async Task<int> PollAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _api.GetMyInboxHistoryAsync(
                page: 1,
                pageSize: 20,
                unreadOnly: true,
                channel: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                _logger.LogWarning(
                    "Notification poll failed: {ErrorCode} {ErrorMessage}",
                    result.ErrorCode, result.ErrorMessage);
                return 0;
            }

            var emitted = 0;
            foreach (var row in result.Value.Items)
            {
                // Per-id seen-set dedup. The id is the Sqid-encoded notification
                // primary key, opaque from this layer — comparing by string
                // identity is the simplest cross-platform invariant that
                // survives GetHashCode collisions (which can hand back negative
                // ints and would break a watermark-comparison approach).
                if (!_seenIds.Add(row.Id))
                {
                    continue;
                }
                _toasts.Enqueue(
                    level: ToastLevel.Info,
                    title: row.Subject,
                    body: row.Body,
                    deepLinkUrl: row.DeepLinkUrl);
                emitted++;
            }
            return emitted;
        }
#pragma warning disable CA1031 // Polling failures MUST NOT break the UI.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification poll threw — swallowed.");
            return 0;
        }
#pragma warning restore CA1031
    }
}
