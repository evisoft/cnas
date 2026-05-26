using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Contracts;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Combines an in-app inbox with MNotify dispatch (UC22). The in-app row is the persistent
/// system of record; MNotify is best-effort delivery to citizens.
/// </summary>
public sealed class NotificationService(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ISqidService sqids,
    IMNotifyClient mnotify,
    ICallerContext caller,
    INotificationDeepLinkResolver? deepLinkResolver = null) : INotificationService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly IMNotifyClient _mnotify = mnotify;
    private readonly ICallerContext _caller = caller;

    /// <summary>
    /// R0172 / TOR CF 22.05 — optional deep-link resolver. When supplied the
    /// inbox projection populates <see cref="NotificationOutput.DeepLinkUrl"/>
    /// from the notification's <c>RelatedEntityType</c> + <c>RelatedEntityId</c>
    /// columns. The constructor parameter is nullable so legacy DI
    /// compositions that have not yet wired the resolver continue to work —
    /// notifications then surface without a deep-link, exactly as before.
    /// </summary>
    private readonly INotificationDeepLinkResolver? _deepLinkResolver = deepLinkResolver;

    /// <inheritdoc />
    public Task<Result<PagedResult<NotificationOutput>>> InboxAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        // Legacy "all-rows" overload delegates to the filtered overload with no filters.
        // Keeps the single SQL plan + Sqid-projection branch in one place.
        return InboxAsync(new NotificationInboxQuery(page), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<NotificationOutput>>> InboxAsync(NotificationInboxQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var userId = _caller.UserId;
        if (userId is null)
        {
            return Result<PagedResult<NotificationOutput>>.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }

        // ─── Channel filter: parse the case-insensitive string to the strongly-typed enum. ───
        // Unknown values surface as VALIDATION_FAILED so the controller can map to 400 — the
        // dashboard pill set is closed (InApp / Email / Sms), drift gets noticed early.
        NotificationChannel? channelFilter = null;
        if (!string.IsNullOrWhiteSpace(query.Channel))
        {
            if (!Enum.TryParse<NotificationChannel>(query.Channel, ignoreCase: true, out var parsed))
            {
                return Result<PagedResult<NotificationOutput>>.Failure(
                    ErrorCodes.ValidationFailed,
                    $"Unknown notification channel '{query.Channel}'.");
            }
            channelFilter = parsed;
        }

        var pageSize = Math.Clamp(query.Page.PageSize, 1, 200);
        var skip = Math.Max(0, query.Page.Page - 1) * pageSize;

        // Compose the filter set defensively so each branch is a small predicate the EF
        // provider can fuse into a single WHERE clause on the streaming-replication side.
        var baseQuery = _db.Notifications
            .Where(n => n.RecipientUserId == userId.Value && n.IsActive);
        if (query.UnreadOnly)
        {
            baseQuery = baseQuery.Where(n => n.ReadAtUtc == null);
        }
        if (channelFilter is not null)
        {
            var ch = channelFilter.Value;
            baseQuery = baseQuery.Where(n => n.Channel == ch);
        }

        var ordered = baseQuery.OrderByDescending(n => n.CreatedAtUtc);
        var total = await ordered.LongCountAsync(cancellationToken).ConfigureAwait(false);

        // First materialise the row into an intermediate envelope carrying the
        // related-entity fields, then resolve the deep link in memory. The
        // resolver is not EF-translatable (Sqid encoding + frozen-dict lookup
        // are CLR-side operations), so we keep the projection projection-only
        // and add the URL field outside the database round-trip.
        var raw = await ordered
            .Skip(skip).Take(pageSize)
            .Select(n => new
            {
                n.Id,
                Channel = n.Channel,
                n.Subject,
                n.Body,
                n.CreatedAtUtc,
                n.ReadAtUtc,
                DeliveryStatus = n.DeliveryStatus,
                n.RelatedEntityType,
                n.RelatedEntityId,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var rows = raw
            .Select(n => new NotificationOutput(
                _sqids.Encode(n.Id),
                n.Channel.ToString(),
                n.Subject,
                n.Body,
                n.CreatedAtUtc,
                n.ReadAtUtc,
                n.DeliveryStatus.ToString(),
                _deepLinkResolver?.Resolve(n.RelatedEntityType, n.RelatedEntityId)))
            .ToList();

        return Result<PagedResult<NotificationOutput>>.Success(
            new PagedResult<NotificationOutput>(rows, query.Page.Page, pageSize, total));
    }

    /// <inheritdoc />
    public async Task<Result> MarkReadAsync(MarkNotificationReadInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var userId = _caller.UserId
            ?? throw new UnauthorizedAccessException("Caller user id is required.");

        var decoded = _sqids.TryDecode(input.NotificationId);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var notification = await _db.Notifications
            .Where(n => n.Id == decoded.Value && n.RecipientUserId == userId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (notification is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Notification not found.");
        }

        notification.ReadAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<int>> MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        var userId = _caller.UserId;
        if (userId is null)
        {
            return Result<int>.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }

        // Touch only the unread rows so the timestamp on already-read rows stays preserved
        // — the audit trail of WHEN the user first saw each notification is meaningful and
        // must not be silently overwritten by a bulk "mark all read" call.
        var now = _clock.UtcNow;
        var unread = await _db.Notifications
            .Where(n => n.RecipientUserId == userId.Value && n.IsActive && n.ReadAtUtc == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in unread)
        {
            row.ReadAtUtc = now;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<int>.Success(unread.Count);
    }

    /// <inheritdoc />
    public Task<Result> EnqueueAsync(
        long recipientUserId,
        string subject,
        string body,
        string? correlationId,
        CancellationToken cancellationToken = default)
        => EnqueueAsync(
            recipientUserId,
            subject,
            body,
            correlationId,
            relatedEntityType: null,
            relatedEntityId: null,
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    public async Task<Result> EnqueueAsync(
        long recipientUserId,
        string subject,
        string body,
        string? correlationId,
        string? relatedEntityType,
        long? relatedEntityId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(body);

        var recipient = await _db.UserProfiles
            .Where(u => u.Id == recipientUserId && u.IsActive)
            .Select(u => new { u.Email, u.NationalId, u.NotificationPreferences })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (recipient is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Recipient not found.");
        }

        // R0171 / CF 22.02 / CF 04.08 — honour the per-channel opt-in flags. The parse is
        // fail-open: null / malformed JSON resolves to the default (everything opted IN),
        // so the dispatcher NEVER silently drops a notification because the preference
        // schema drifted. See NotificationPreferences XML doc for the contract.
        var prefs = NotificationPreferencesJson.Parse(recipient.NotificationPreferences);

        var now = _clock.UtcNow;

        // ────── In-app channel ──────
        // The in-app inbox row IS the dispatch — there is no separate channel adapter — so
        // when allowed we mark it Delivered immediately. When opted out we still persist
        // the row (so the citizen has a record in their history) but flip it to Suppressed
        // and skip the timestamp; the suppression counter is incremented for observability.
        var inAppAllowed = prefs.IsAllowed(NotificationChannel.InApp);
        _db.Notifications.Add(new Notification
        {
            CreatedAtUtc = now,
            RecipientUserId = recipientUserId,
            Channel = NotificationChannel.InApp,
            Subject = subject,
            Body = body,
            CorrelationId = correlationId,
            DeliveryStatus = inAppAllowed ? NotificationDeliveryStatus.Delivered : NotificationDeliveryStatus.Suppressed,
            DispatchedAtUtc = inAppAllowed ? now : null,
            // R0174 / TOR CF 22.03 — anchor the row to its originating business
            // object so the R0172 deep-link resolver renders the subject as a
            // clickable link in the inbox. Either column null leaves the row
            // un-anchored and the UI falls back to plain text.
            RelatedEntityType = relatedEntityType,
            RelatedEntityId = relatedEntityId,
        });
        if (!inAppAllowed)
        {
            CnasMeter.NotificationSuppressed.Add(1,
                new KeyValuePair<string, object?>("channel", NotificationChannel.InApp.ToString()));
        }

        // ────── Email channel (MNotify mirror) ──────
        // Best-effort mirror — only attempted when the recipient has both an email and a
        // national id to address. The opt-out check happens BEFORE the MNotify call so an
        // opted-out citizen never sees a network hit on their behalf.
        if (!string.IsNullOrWhiteSpace(recipient.Email) && !string.IsNullOrWhiteSpace(recipient.NationalId))
        {
            var emailAllowed = prefs.IsAllowed(NotificationChannel.Email);

            // The Email row is created speculatively before the MNotify call; we set the
            // outcome based on the result so the row never lingers in an inferred state.
            var emailRow = new Notification
            {
                CreatedAtUtc = now,
                RecipientUserId = recipientUserId,
                Channel = NotificationChannel.Email,
                Subject = subject,
                Body = body,
                CorrelationId = correlationId,
                DeliveryStatus = emailAllowed ? NotificationDeliveryStatus.Pending : NotificationDeliveryStatus.Suppressed,
                DispatchedAtUtc = emailAllowed ? null : now,
                // R0174 / TOR CF 22.03 — mirror the related-entity anchor onto
                // the email mirror so a future MNotify template can include the
                // deep-link directly (consumer-side rendering).
                RelatedEntityType = relatedEntityType,
                RelatedEntityId = relatedEntityId,
            };
            _db.Notifications.Add(emailRow);

            if (emailAllowed)
            {
                var dispatch = await _mnotify.SendAsync(new MNotifyMessage(
                    recipient.NationalId!,
                    "Email",
                    "GENERIC",
                    new Dictionary<string, string>
                    {
                        ["subject"] = subject,
                        ["body"] = body,
                    }), cancellationToken).ConfigureAwait(false);

                if (dispatch.IsSuccess)
                {
                    emailRow.DeliveryStatus = NotificationDeliveryStatus.Delivered;
                    emailRow.DispatchedAtUtc = now;
                }
                else
                {
                    emailRow.DeliveryStatus = NotificationDeliveryStatus.Failed;
                }
            }
            else
            {
                CnasMeter.NotificationSuppressed.Add(1,
                    new KeyValuePair<string, object?>("channel", NotificationChannel.Email.ToString()));
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
