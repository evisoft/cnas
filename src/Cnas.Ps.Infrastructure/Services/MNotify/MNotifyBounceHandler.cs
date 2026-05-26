using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.MNotify;

/// <summary>
/// R0115 / TOR CF 14.07 — concrete implementation of
/// <see cref="IMNotifyBounceHandler"/>. Looks up the originating
/// <see cref="Notification"/> by its upstream reference (which we persist on
/// <see cref="Notification.CorrelationId"/> alongside the existing trace
/// correlation), flips <see cref="Notification.DeliveryStatus"/> to
/// <see cref="NotificationDeliveryStatus.Failed"/>, and writes a
/// <c>NOTIFY.BOUNCED</c> audit row.
/// </summary>
/// <remarks>
/// Idempotent — a second call for the same reference is a no-op success: the
/// handler returns <see cref="Result.Success"/> when the row is already
/// flagged Failed.
/// </remarks>
public sealed class MNotifyBounceHandler : IMNotifyBounceHandler
{
    /// <summary>Audit code emitted on every successful bounce write.</summary>
    public const string AuditBounced = "NOTIFY.BOUNCED";

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;

    /// <summary>Constructs the handler.</summary>
    /// <param name="db">Per-request write context.</param>
    /// <param name="clock">UTC clock.</param>
    /// <param name="caller">Authenticated caller information (typically the gateway service account).</param>
    /// <param name="audit">Audit journal façade.</param>
    public MNotifyBounceHandler(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);

        _db = db;
        _clock = clock;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result> HandleBounceAsync(
        MNotifyBounceWebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(payload.NotificationReference))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "NotificationReference is required.");
        }
        if (string.IsNullOrWhiteSpace(payload.BounceCode))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "BounceCode is required.");
        }

        var notification = await _db.Notifications
            .SingleOrDefaultAsync(n => n.CorrelationId == payload.NotificationReference,
                cancellationToken)
            .ConfigureAwait(false);
        if (notification is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"Notification reference '{payload.NotificationReference}' not found.");
        }

        // Idempotency: already-Failed rows short-circuit.
        if (notification.DeliveryStatus == NotificationDeliveryStatus.Failed)
        {
            return Result.Success();
        }

        var now = _clock.UtcNow;
        notification.DeliveryStatus = NotificationDeliveryStatus.Failed;
        notification.UpdatedAtUtc = now;
        notification.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            notificationId = notification.Id,
            reference = payload.NotificationReference,
            bounceCode = payload.BounceCode,
            bounceReason = payload.BounceReason,
            occurredAtUtc = payload.OccurredAtUtc,
        });
        await _audit.RecordAsync(
            AuditBounced,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Notification),
            notification.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
