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
/// R1505 / TOR §3.7-F — concrete implementation of
/// <see cref="IRecoveryDecisionService"/>. Persists recovery decisions as
/// <c>Document</c> rows (Kind=Decision) so the same row participates in the
/// canonical decisions register (R1601), then transitions the lifecycle state
/// via the <c>Document.Verdict</c> column + a JSON envelope on
/// <c>Document.VerdictNote</c> carrying the amount + reason + running
/// recovered total.
/// </summary>
public sealed class RecoveryDecisionService : IRecoveryDecisionService
{
    /// <summary>Audit-code emitted on a successful initiation.</summary>
    public const string AuditInitiated = "DECISION.RECOVERY_INITIATED";

    /// <summary>Audit-code emitted on a state-changing acknowledge.</summary>
    public const string AuditAcknowledged = "DECISION.RECOVERY_ACKNOWLEDGED";

    /// <summary>Audit-code emitted on a state-changing recovery posting.</summary>
    public const string AuditRecovered = "DECISION.RECOVERY_RECOVERED";

    /// <summary>Storage bucket for recovery-decision documents.</summary>
    private const string StorageBucket = "cnas-documents";

    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly ILogger<RecoveryDecisionService> _logger;
    private readonly INotificationTriggerDispatcher? _triggers;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="caller">Authenticated caller information for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="triggers">Optional notification-trigger dispatcher (R0174).</param>
    public RecoveryDecisionService(
        ICnasDbContext db,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService audit,
        ILogger<RecoveryDecisionService> logger,
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
    public async Task<Result<RecoveryDecisionDto>> InitiateAsync(
        string solicitantSqid,
        decimal amount,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0m)
        {
            return Result<RecoveryDecisionDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Recovery amount must be strictly positive.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 3 || reason.Length > 500)
        {
            return Result<RecoveryDecisionDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Recovery reason must be 3-500 characters.");
        }

        var decoded = _sqids.TryDecode(solicitantSqid);
        if (decoded.IsFailure)
        {
            return Result<RecoveryDecisionDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var solicitant = await _db.Solicitants
            .SingleOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (solicitant is null)
        {
            return Result<RecoveryDecisionDto>.Failure(
                ErrorCodes.NotFound,
                $"Solicitant id={decoded.Value} not found.");
        }

        var now = _clock.UtcNow;
        var envelope = new RecoveryEnvelope(
            SolicitantSqid: solicitantSqid,
            SolicitantId: solicitant.Id,
            Amount: amount,
            Reason: reason,
            RecoveredSoFar: 0m,
            InitiatedAtUtc: now,
            AcknowledgedAtUtc: null);

        var doc = new Document
        {
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            DossierId = null,
            Kind = DocumentKind.Decision,
            Title = $"{DecizieRecuperareSumeTemplate.Code} ({solicitant.DisplayName})",
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            SizeBytes = 0,
            StorageObjectKey = $"recovery/{Guid.NewGuid():N}",
            StorageBucket = StorageBucket,
            ContentSha256Hex = string.Empty,
            IsSigned = false,
            IsActive = true,
            Verdict = (int)RecoveryDecisionStatus.Initiated,
            VerdictNote = envelope.ToJson(),
            VerdictAtUtc = now,
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            solicitantId = solicitant.Id,
            amount,
            reason,
            documentId = doc.Id,
        });
        await _audit.RecordAsync(
            AuditInitiated,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Document),
            doc.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        await TryDispatchActionResultAsync(
            solicitant,
            subject: "Decizie de recuperare a sumelor plătite în plus",
            body: string.Format(
                CultureInfo.InvariantCulture,
                "S-a emis o decizie de recuperare în valoare de {0:F2} MDL. Motiv: {1}.",
                amount,
                reason),
            relatedDocumentId: doc.Id,
            cancellationToken).ConfigureAwait(false);

        return Result<RecoveryDecisionDto>.Success(BuildDto(doc, envelope));
    }

    /// <inheritdoc />
    public async Task<Result> MarkAcknowledgedAsync(
        string decisionSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(decisionSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var (doc, envelope) = loaded.Value;

        var currentStatus = (RecoveryDecisionStatus)(doc.Verdict ?? 0);
        if (currentStatus == RecoveryDecisionStatus.FullyRecovered)
        {
            return Result.Failure(
                ErrorCodes.Conflict,
                "Recovery decision is already fully recovered.");
        }

        if (envelope.AcknowledgedAtUtc is not null)
        {
            // Idempotent replay — already acknowledged.
            return Result.Success();
        }

        var now = _clock.UtcNow;
        var updated = envelope with { AcknowledgedAtUtc = now };
        doc.VerdictNote = updated.ToJson();
        doc.VerdictAtUtc = now;
        doc.UpdatedAtUtc = now;
        // Preserve a Recovered state when the caller pre-recorded a partial recovery.
        if (currentStatus is RecoveryDecisionStatus.Initiated)
        {
            doc.Verdict = (int)RecoveryDecisionStatus.Acknowledged;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            documentId = doc.Id,
            solicitantId = envelope.SolicitantId,
        });
        await _audit.RecordAsync(
            AuditAcknowledged,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Document),
            doc.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> MarkRecoveredAsync(
        string decisionSqid,
        decimal recoveredAmount,
        CancellationToken cancellationToken = default)
    {
        if (recoveredAmount <= 0m)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                "Recovered amount must be strictly positive.");
        }

        var loaded = await LoadAsync(decisionSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var (doc, envelope) = loaded.Value;

        var currentStatus = (RecoveryDecisionStatus)(doc.Verdict ?? 0);
        if (currentStatus == RecoveryDecisionStatus.FullyRecovered)
        {
            return Result.Failure(
                ErrorCodes.Conflict,
                "Recovery decision is already fully recovered.");
        }

        // iter-149 — over-recovery guard. Without this check the envelope could
        // record a RecoveredSoFar > Amount (e.g. an operator double-keys a
        // partial recovery, or the integration with the bank feed misfires).
        // The downstream FullyRecovered status would still flip but the audit
        // trail would carry an internally inconsistent figure. We refuse with
        // ValidationFailed so the operator sees the cap and can correct the
        // round before persisting.
        var newTotal = envelope.RecoveredSoFar + recoveredAmount;
        if (newTotal > envelope.Amount)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                $"Cannot recover {newTotal} MDL — exceeds decision amount {envelope.Amount} MDL.");
        }

        var newStatus = newTotal >= envelope.Amount
            ? RecoveryDecisionStatus.FullyRecovered
            : RecoveryDecisionStatus.PartiallyRecovered;

        var now = _clock.UtcNow;
        var updated = envelope with { RecoveredSoFar = newTotal };
        doc.VerdictNote = updated.ToJson();
        doc.Verdict = (int)newStatus;
        doc.VerdictAtUtc = now;
        doc.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            documentId = doc.Id,
            solicitantId = envelope.SolicitantId,
            recoveredThisRound = recoveredAmount,
            recoveredTotal = newTotal,
            status = newStatus.ToString(),
        });
        await _audit.RecordAsync(
            AuditRecovered,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Document),
            doc.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Loads + decodes a recovery-decision document and its envelope, or returns
    /// the stable failure code (<see cref="ErrorCodes.NotFound"/> /
    /// <see cref="ErrorCodes.InvalidSqid"/>).
    /// </summary>
    private async Task<Result<(Document Doc, RecoveryEnvelope Envelope)>> LoadAsync(
        string decisionSqid,
        CancellationToken ct)
    {
        var decoded = _sqids.TryDecode(decisionSqid);
        if (decoded.IsFailure)
        {
            return Result<(Document, RecoveryEnvelope)>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var doc = await _db.Documents
            .SingleOrDefaultAsync(d => d.Id == decoded.Value && d.IsActive, ct)
            .ConfigureAwait(false);
        if (doc is null)
        {
            return Result<(Document, RecoveryEnvelope)>.Failure(
                ErrorCodes.NotFound,
                $"Recovery decision id={decoded.Value} not found.");
        }

        var envelope = RecoveryEnvelope.FromJson(doc.VerdictNote);
        if (envelope is null)
        {
            return Result<(Document, RecoveryEnvelope)>.Failure(
                ErrorCodes.NotFound,
                $"Document id={decoded.Value} is not a recovery decision.");
        }

        return Result<(Document, RecoveryEnvelope)>.Success((doc, envelope));
    }

    /// <summary>Projects the persisted state into the canonical wire DTO.</summary>
    private RecoveryDecisionDto BuildDto(Document doc, RecoveryEnvelope envelope)
    {
        var status = (RecoveryDecisionStatus)(doc.Verdict ?? 0);
        return new RecoveryDecisionDto(
            Sqid: _sqids.Encode(doc.Id),
            SolicitantSqid: envelope.SolicitantSqid,
            Status: status,
            AmountMdl: envelope.Amount,
            RecoveredAmountMdl: envelope.RecoveredSoFar,
            Reason: envelope.Reason,
            InitiatedAtUtc: envelope.InitiatedAtUtc,
            AcknowledgedAtUtc: envelope.AcknowledgedAtUtc);
    }

    /// <summary>
    /// Best-effort R0174 trigger for the citizen inbox. Any failure is logged at
    /// Warning and swallowed — a missed notification MUST NOT roll back the
    /// recovery lifecycle write.
    /// </summary>
    private async Task TryDispatchActionResultAsync(
        Solicitant solicitant,
        string subject,
        string body,
        long relatedDocumentId,
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
                    RecipientUserId: solicitant.Id,
                    Subject: subject,
                    Body: body,
                    CorrelationId: _caller.CorrelationId,
                    RelatedEntityType: NotificationRelatedEntityTypes.Application,
                    RelatedEntityId: relatedDocumentId),
                ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort notification — MUST NOT break the recovery pipeline.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ActionResult trigger dispatch failed for recovery decision {DocId}; lifecycle unaffected.",
                relatedDocumentId);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Serialisable envelope persisted on <see cref="Document.VerdictNote"/>.
    /// Carries the original amount + reason + running recovered total + the
    /// timestamp pair the lifecycle reader needs to project the
    /// <see cref="RecoveryDecisionDto"/>.
    /// </summary>
    /// <param name="SolicitantSqid">Sqid of the targeted beneficiary.</param>
    /// <param name="SolicitantId">Raw internal id (used for fast filtering).</param>
    /// <param name="Amount">Original recovery amount in MDL.</param>
    /// <param name="Reason">Free-text justification recorded at initiation.</param>
    /// <param name="RecoveredSoFar">Total recovered against this decision so far.</param>
    /// <param name="InitiatedAtUtc">UTC instant of initiation.</param>
    /// <param name="AcknowledgedAtUtc">UTC instant of solicitant acknowledgement.</param>
    internal sealed record RecoveryEnvelope(
        string SolicitantSqid,
        long SolicitantId,
        decimal Amount,
        string Reason,
        decimal RecoveredSoFar,
        DateTime InitiatedAtUtc,
        DateTime? AcknowledgedAtUtc)
    {
        /// <summary>Marker key used to recognise a recovery-envelope payload.</summary>
        public const string MarkerKey = "recoveryDecision";

        /// <summary>Serialises the envelope to a JSON string for VerdictNote storage.</summary>
        public string ToJson() => JsonSerializer.Serialize(new
        {
            kind = MarkerKey,
            solicitantSqid = SolicitantSqid,
            solicitantId = SolicitantId,
            amount = Amount,
            reason = Reason,
            recoveredSoFar = RecoveredSoFar,
            initiatedAtUtc = InitiatedAtUtc,
            acknowledgedAtUtc = AcknowledgedAtUtc,
        });

        /// <summary>Best-effort deserializer. Returns null on malformed / foreign payloads.</summary>
        public static RecoveryEnvelope? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                if (!doc.RootElement.TryGetProperty("kind", out var kind)
                    || kind.GetString() != MarkerKey)
                {
                    return null;
                }
                return new RecoveryEnvelope(
                    SolicitantSqid: doc.RootElement.GetProperty("solicitantSqid").GetString() ?? string.Empty,
                    SolicitantId: doc.RootElement.GetProperty("solicitantId").GetInt64(),
                    Amount: doc.RootElement.GetProperty("amount").GetDecimal(),
                    Reason: doc.RootElement.GetProperty("reason").GetString() ?? string.Empty,
                    RecoveredSoFar: doc.RootElement.GetProperty("recoveredSoFar").GetDecimal(),
                    InitiatedAtUtc: doc.RootElement.GetProperty("initiatedAtUtc").GetDateTime(),
                    AcknowledgedAtUtc: doc.RootElement.TryGetProperty("acknowledgedAtUtc", out var ack)
                        && ack.ValueKind != JsonValueKind.Null
                        ? ack.GetDateTime()
                        : null);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }
    }
}
