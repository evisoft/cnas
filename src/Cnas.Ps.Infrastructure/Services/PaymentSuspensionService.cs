using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Documents.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R1504 / TOR §3.7-E — concrete implementation of
/// <see cref="IPaymentSuspensionService"/>. Mints the Decizie / Dispozitie
/// document via the existing Annex 7 templates, flips the related
/// <see cref="MPayOrder"/> rows between active and suspended, writes the
/// audit row, and fires the citizen ActionResult notification.
/// </summary>
public sealed class PaymentSuspensionService : IPaymentSuspensionService
{
    /// <summary>Stable audit event code emitted on a successful suspend.</summary>
    public const string AuditSuspended = "PAYMENT.SUSPENDED";

    /// <summary>Stable audit event code emitted on a successful resume.</summary>
    public const string AuditResumed = "PAYMENT.RESUMED";

    /// <summary>Storage bucket name for suspension / resume documents.</summary>
    private const string StorageBucket = "cnas-documents";

    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly ILogger<PaymentSuspensionService> _logger;
    private readonly INotificationTriggerDispatcher? _triggers;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="caller">Authenticated caller information for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="triggers">Optional notification-trigger dispatcher (R0174).</param>
    public PaymentSuspensionService(
        ICnasDbContext db,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService audit,
        ILogger<PaymentSuspensionService> logger,
        INotificationTriggerDispatcher? triggers = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _audit = audit;
        _logger = logger;
        _triggers = triggers;
    }

    /// <inheritdoc />
    public async Task<Result<PaymentSuspensionDto>> SuspendAsync(
        string decisionSqid,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var reasonCheck = ValidateReason(reason);
        if (reasonCheck.IsFailure)
        {
            return Result<PaymentSuspensionDto>.Failure(reasonCheck.ErrorCode!, reasonCheck.ErrorMessage!);
        }

        var decoded = _sqids.TryDecode(decisionSqid);
        if (decoded.IsFailure)
        {
            return Result<PaymentSuspensionDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var decision = await _db.Applications
            .Include(a => a.Solicitant)
            .SingleOrDefaultAsync(a => a.Id == decoded.Value && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (decision is null)
        {
            return Result<PaymentSuspensionDto>.Failure(
                ErrorCodes.NotFound,
                $"Decision id={decoded.Value} not found.");
        }

        // Double-suspend guard — reject when there is already an active suspension.
        var existing = await _db.PaymentSuspensionRecords
            .AnyAsync(r =>
                r.DecisionId == decision.Id
                && r.ResumedAtUtc == null
                && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (existing)
        {
            return Result<PaymentSuspensionDto>.Failure(
                ErrorCodes.Conflict,
                "Payments are already suspended for this decision.");
        }

        // ATOMICITY (R1504): persisting the Document, PaymentSuspensionRecord,
        // and MPayOrder mutations must commit as a single logical unit. A
        // crash between the two original SaveChanges calls would leave the
        // DB inconsistent (record without document link, or orders mutated
        // without the audit document). Restructured below to:
        //   1. Add Document first; single SaveChangesAsync to populate doc.Id.
        //   2. Populate record.SuspensionDocumentId from doc.Id, mutate orders.
        //   3. Single second SaveChangesAsync persists everything atomically.
        // All inside an explicit DB transaction when the provider supports it
        // (Postgres in prod); the InMemory test provider silently no-ops
        // BeginTransactionAsync per IsRelational guard below.
        var now = _clock.UtcNow;
        var useTx = _db is DbContext rel && rel.Database.IsRelational();
        var tx = useTx
            ? await ((DbContext)_db).Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            var doc = new Document
            {
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                DossierId = decision.DossierId,
                Kind = DocumentKind.Decision,
                Title = $"{DecizieSuspendarePlataTemplate.Code} (#{decision.Id})",
                MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                SizeBytes = 0,
                StorageObjectKey = $"suspension/{Guid.NewGuid():N}",
                StorageBucket = StorageBucket,
                ContentSha256Hex = string.Empty,
                IsSigned = false,
                IsActive = true,
            };
            _db.Documents.Add(doc);
            // First save populates doc.Id so the suspension record can carry
            // a FK reference from the very first persistence step. Done inside
            // the transaction so a crash before step 2 rolls everything back.
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var record = new PaymentSuspensionRecord
            {
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                DecisionId = decision.Id,
                SuspendedAtUtc = now,
                SuspendedByUserId = _caller.UserId ?? 0,
                SuspensionReason = reason,
                SuspensionDocumentId = doc.Id,
                IsActive = true,
            };
            _db.PaymentSuspensionRecords.Add(record);

            // Flip every active, unconfirmed MPay order for the same beneficiary into the
            // suspended state. Beneficiary IDNP is the natural join key — see MPayOrder.
            var beneficiaryIdnp = decision.Solicitant?.NationalId;
            if (!string.IsNullOrEmpty(beneficiaryIdnp))
            {
                var orders = await _db.MPayOrders
                    .Where(o => o.BeneficiaryIdnp == beneficiaryIdnp
                                && o.IsActive
                                && !o.IsSuspended
                                && o.ConfirmedAtUtc == null)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var order in orders)
                {
                    order.IsSuspended = true;
                    order.SuspendedAtUtc = now;
                    order.UpdatedAtUtc = now;
                    order.UpdatedBy = _caller.UserSqid;
                }
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            // Stash for the post-tx callsite that issues audit + notification.
            // Both are best-effort and live OUTSIDE the transaction so a notification
            // failure cannot roll back the suspension lifecycle write.
            return await EmitSuspendAuditAndNotifyAsync(
                decision, decisionSqid, doc, record, reason, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Best-effort post-commit audit + citizen notification for the Suspend
    /// path. Lives outside the persistence transaction so a notification
    /// failure CANNOT roll back the suspension lifecycle write. Returns the
    /// canonical DTO that <see cref="SuspendAsync"/> propagates to the caller.
    /// </summary>
    private async Task<Result<PaymentSuspensionDto>> EmitSuspendAuditAndNotifyAsync(
        ServiceApplication decision,
        string decisionSqid,
        Document doc,
        PaymentSuspensionRecord record,
        string reason,
        CancellationToken cancellationToken)
    {

        var detailsJson = JsonSerializer.Serialize(new
        {
            decisionId = decision.Id,
            suspensionRecordId = record.Id,
            documentId = doc.Id,
            reason,
        });
        await _audit.RecordAsync(
            AuditSuspended,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(PaymentSuspensionRecord),
            record.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        await TryDispatchAsync(
            recipientUserId: decision.SolicitantId,
            subject: "Suspendarea plății prestației",
            body: string.Format(
                CultureInfo.InvariantCulture,
                "Plata prestației este suspendată. Motiv: {0}.",
                reason),
            relatedId: record.Id,
            cancellationToken).ConfigureAwait(false);

        return Result<PaymentSuspensionDto>.Success(BuildDto(record, decisionSqid));
    }

    /// <inheritdoc />
    public async Task<Result<PaymentSuspensionDto>> ResumeAsync(
        string suspensionSqid,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var reasonCheck = ValidateReason(reason);
        if (reasonCheck.IsFailure)
        {
            return Result<PaymentSuspensionDto>.Failure(reasonCheck.ErrorCode!, reasonCheck.ErrorMessage!);
        }

        var decoded = _sqids.TryDecode(suspensionSqid);
        if (decoded.IsFailure)
        {
            return Result<PaymentSuspensionDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var record = await _db.PaymentSuspensionRecords
            .SingleOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return Result<PaymentSuspensionDto>.Failure(
                ErrorCodes.NotFound,
                $"Payment suspension id={decoded.Value} not found.");
        }

        if (record.ResumedAtUtc is not null)
        {
            return Result<PaymentSuspensionDto>.Failure(
                ErrorCodes.Conflict,
                "This suspension has already been resumed.");
        }

        var decision = await _db.Applications
            .Include(a => a.Solicitant)
            .SingleOrDefaultAsync(a => a.Id == record.DecisionId && a.IsActive, cancellationToken)
            .ConfigureAwait(false);

        // ATOMICITY (R1504): same posture as SuspendAsync — the resume document,
        // the lifecycle stamps on the existing record, and the MPay order flag
        // flip must commit as ONE logical unit. Restructured to add the
        // Document first (gets its Id), then populate
        // record.ResumeDocumentId from doc.Id, then a SINGLE second
        // SaveChangesAsync persists the lifecycle + order updates. The whole
        // sequence is wrapped in a relational transaction when the provider
        // supports one (Postgres in prod; InMemory test provider no-ops).
        var now = _clock.UtcNow;
        var useTx = _db is DbContext rel && rel.Database.IsRelational();
        var tx = useTx
            ? await ((DbContext)_db).Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            var doc = new Document
            {
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                DossierId = decision?.DossierId,
                Kind = DocumentKind.Decision,
                Title = $"{DispozitieReluareTemplate.Code} (#{record.DecisionId})",
                MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                SizeBytes = 0,
                StorageObjectKey = $"resume/{Guid.NewGuid():N}",
                StorageBucket = StorageBucket,
                ContentSha256Hex = string.Empty,
                IsSigned = false,
                IsActive = true,
            };
            _db.Documents.Add(doc);
            // First save populates doc.Id so the resume record can carry a FK
            // reference from the very first persistence step.
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            record.ResumedAtUtc = now;
            record.ResumedByUserId = _caller.UserId;
            record.ResumeReason = reason;
            record.ResumeDocumentId = doc.Id;
            record.UpdatedAtUtc = now;
            record.UpdatedBy = _caller.UserSqid;

            var beneficiaryIdnp = decision?.Solicitant?.NationalId;
            if (!string.IsNullOrEmpty(beneficiaryIdnp))
            {
                var orders = await _db.MPayOrders
                    .Where(o => o.BeneficiaryIdnp == beneficiaryIdnp
                                && o.IsActive
                                && o.IsSuspended)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var order in orders)
                {
                    order.IsSuspended = false;
                    order.UpdatedAtUtc = now;
                    order.UpdatedBy = _caller.UserSqid;
                }
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            var detailsJson = JsonSerializer.Serialize(new
            {
                suspensionRecordId = record.Id,
                decisionId = record.DecisionId,
                documentId = doc.Id,
                reason,
            });
            await _audit.RecordAsync(
                AuditResumed,
                AuditSeverity.Critical,
                _caller.UserSqid ?? "?",
                nameof(PaymentSuspensionRecord),
                record.Id,
                detailsJson,
                _caller.SourceIp,
                _caller.CorrelationId,
                cancellationToken).ConfigureAwait(false);

            if (decision is not null)
            {
                await TryDispatchAsync(
                    recipientUserId: decision.SolicitantId,
                    subject: "Reluarea plății prestației",
                    body: string.Format(
                        CultureInfo.InvariantCulture,
                        "Plata prestației se reia. Motiv: {0}.",
                        reason),
                    relatedId: record.Id,
                    cancellationToken).ConfigureAwait(false);
            }

            return Result<PaymentSuspensionDto>.Success(BuildDto(record, _sqids.Encode(record.DecisionId)));
        }
        catch
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Projects the persisted state into the canonical wire DTO.</summary>
    private PaymentSuspensionDto BuildDto(PaymentSuspensionRecord record, string decisionSqid) =>
        new(
            Sqid: _sqids.Encode(record.Id),
            DecisionSqid: decisionSqid,
            SuspensionReason: record.SuspensionReason,
            SuspendedAtUtc: record.SuspendedAtUtc,
            ResumedAtUtc: record.ResumedAtUtc,
            ResumeReason: record.ResumeReason,
            SuspensionDocumentSqid: record.SuspensionDocumentId is { } sid ? _sqids.Encode(sid) : null,
            ResumeDocumentSqid: record.ResumeDocumentId is { } rid ? _sqids.Encode(rid) : null);

    /// <summary>Validates the reason field exists and respects the 3-500 char bounds.</summary>
    private static Result ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)
            || reason.Length < 3
            || reason.Length > 500)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                "Reason must be 3-500 characters.");
        }
        return Result.Success();
    }

    /// <summary>
    /// Best-effort R0174 trigger for the citizen inbox. Any failure is logged at
    /// Warning and swallowed — a missed notification MUST NOT roll back the
    /// suspension lifecycle write.
    /// </summary>
    private async Task TryDispatchAsync(
        long recipientUserId,
        string subject,
        string body,
        long relatedId,
        CancellationToken ct)
    {
        if (_triggers is null)
        {
            return;
        }
        try
        {
            await _triggers.DispatchAsync(
                NotificationTriggerKind.ActionResult,
                new NotificationTriggerPayload(
                    RecipientUserId: recipientUserId,
                    Subject: subject,
                    Body: body,
                    CorrelationId: _caller.CorrelationId,
                    RelatedEntityType: NotificationRelatedEntityTypes.Application,
                    RelatedEntityId: relatedId),
                ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort notification — MUST NOT break the suspension pipeline.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ActionResult trigger dispatch failed for suspension {RecordId}; lifecycle unaffected.",
                relatedId);
        }
#pragma warning restore CA1031
    }
}
