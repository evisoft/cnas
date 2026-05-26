namespace Cnas.Ps.Web.Components;

/// <summary>
/// R0170 / TOR CF 22.02 — in-process queue that surfaces new in-app
/// notifications as transient toast banners on the citizen portal. The queue is
/// scoped per Blazor circuit; the <see cref="ToastNotificationHost"/> component
/// subscribes to <see cref="Changed"/> and re-renders whenever a new toast
/// arrives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Scoped per-circuit so two browser tabs see independent toast
/// streams. The queue is intentionally an in-memory primitive — toasts
/// represent "in this session" UI state, not persistent business data.
/// </para>
/// <para>
/// <b>Thread-safety.</b> Implementations are expected to lock around the
/// internal list since polling callbacks fire on a thread-pool thread while
/// the UI renders on the renderer's synchronisation context.
/// </para>
/// </remarks>
public interface IClientToastQueue
{
    /// <summary>
    /// Pushes a new toast banner. Returns immediately — rendering is the
    /// host component's responsibility via the <see cref="Changed"/> event.
    /// </summary>
    /// <param name="level">Severity bucket (Info / Success / Warning / Error).</param>
    /// <param name="title">Bold first-line title.</param>
    /// <param name="body">Multi-line body.</param>
    /// <param name="deepLinkUrl">
    /// Optional relative URL the toast renders as a clickable anchor. Composed
    /// by the server via <c>INotificationDeepLinkResolver</c> (R0172).
    /// </param>
    void Enqueue(ToastLevel level, string title, string body, string? deepLinkUrl);

    /// <summary>
    /// Removes a previously-queued toast by its monotonically-increasing id.
    /// No-op when the id is not in the queue.
    /// </summary>
    /// <param name="toastId">Id returned by the snapshot.</param>
    void Dismiss(long toastId);

    /// <summary>
    /// Snapshot of the current queued toasts in arrival order. Returned as a
    /// fresh list so the host can enumerate it without locking concerns.
    /// </summary>
    /// <returns>The list of toasts currently visible.</returns>
    IReadOnlyList<ToastItem> Snapshot();

    /// <summary>
    /// Fires whenever the queue is mutated (Enqueue / Dismiss). Subscribers
    /// re-render their UI in response.
    /// </summary>
    event Action? Changed;
}

/// <summary>
/// R0170 / TOR CF 22.02 — severity bucket for a toast banner. Maps to the CSS
/// modifier class on the rendered element.
/// </summary>
public enum ToastLevel
{
    /// <summary>Neutral informational message.</summary>
    Info = 0,

    /// <summary>Positive confirmation (e.g. "decision approved").</summary>
    Success = 1,

    /// <summary>Warning the user should notice (e.g. "SLA breach").</summary>
    Warning = 2,

    /// <summary>Error / failure (e.g. "report failed").</summary>
    Error = 3,
}

/// <summary>
/// R0170 / TOR CF 22.02 — single toast row carried by
/// <see cref="IClientToastQueue"/>. Immutable record so multiple subscribers
/// can enumerate the snapshot without contention.
/// </summary>
/// <param name="Id">Monotonically-increasing queue id (process-local).</param>
/// <param name="Level">Severity bucket.</param>
/// <param name="Title">Bold first-line title.</param>
/// <param name="Body">Multi-line body.</param>
/// <param name="DeepLinkUrl">Optional clickable deep-link.</param>
public sealed record ToastItem(
    long Id,
    ToastLevel Level,
    string Title,
    string Body,
    string? DeepLinkUrl);

/// <summary>
/// R0170 / TOR CF 22.02 — default in-memory <see cref="IClientToastQueue"/>.
/// Scoped per Blazor circuit.
/// </summary>
public sealed class ClientToastQueue : IClientToastQueue
{
    private readonly object _gate = new();
    private readonly List<ToastItem> _items = new();
    private long _seq;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void Enqueue(ToastLevel level, string title, string body, string? deepLinkUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);
        ToastItem item;
        lock (_gate)
        {
            item = new ToastItem(++_seq, level, title, body, deepLinkUrl);
            _items.Add(item);
        }
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dismiss(long toastId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.RemoveAll(t => t.Id == toastId) > 0;
        }
        if (removed)
        {
            Changed?.Invoke();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ToastItem> Snapshot()
    {
        lock (_gate)
        {
            // Return a copy so callers can enumerate without holding the lock.
            return _items.ToArray();
        }
    }
}
