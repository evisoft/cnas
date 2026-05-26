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
/// R1502 / TOR §3.7-C — concrete implementation of
/// <see cref="IDecisionRecomputeService"/>. Computes the signed delta between
/// the prior decision's stored amount and the recomputed amount, persists a
/// new <see cref="Document"/> row of the appropriate template kind, audits
/// the recompute, and dispatches the citizen ActionResult notification.
/// </summary>
public sealed class DecisionRecomputeService : IDecisionRecomputeService
{
    /// <summary>Stable audit event code emitted on every state-changing recompute.</summary>
    public const string AuditRecomputed = "DECISION.RECOMPUTED";

    /// <summary>Stable document-kind code returned when the delta is positive.</summary>
    public const string DocCodeAdjustment = "DECIZIE_AJUSTARE_SUME";

    /// <summary>Stable document-kind code returned when the delta is negative.</summary>
    public const string DocCodeRecuperare = "DECIZIE_RECUPERARE_SUME";

    /// <summary>Stable document-kind code returned when the delta is zero (no new doc emitted).</summary>
    public const string DocCodeNoChange = "NO_CHANGE";

    /// <summary>Synthetic storage bucket name for recompute documents (placeholder pending MinIO wiring).</summary>
    private const string StorageBucket = "cnas-documents";

    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly ILogger<DecisionRecomputeService> _logger;
    private readonly INotificationTriggerDispatcher? _triggers;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="caller">Authenticated caller information for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="triggers">Optional notification-trigger dispatcher (R0174).</param>
    public DecisionRecomputeService(
        ICnasDbContext db,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService audit,
        ILogger<DecisionRecomputeService> logger,
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
    public async Task<Result<DecisionRecomputeOutcomeDto>> RecomputeAsync(
        string priorDecisionSqid,
        DecisionRecomputeReason reason,
        decimal newMonthlyAmountMdl,
        CancellationToken cancellationToken = default)
    {
        if (newMonthlyAmountMdl < 0)
        {
            return Result<DecisionRecomputeOutcomeDto>.Failure(
                ErrorCodes.ValidationFailed,
                "newMonthlyAmountMdl must be non-negative.");
        }

        var decoded = _sqids.TryDecode(priorDecisionSqid);
        if (decoded.IsFailure)
        {
            return Result<DecisionRecomputeOutcomeDto>.Failure(
                decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var prior = await _db.Applications
            .SingleOrDefaultAsync(a => a.Id == decoded.Value && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (prior is null)
        {
            return Result<DecisionRecomputeOutcomeDto>.Failure(
                ErrorCodes.NotFound,
                $"Prior decision id={decoded.Value} not found.");
        }

        var priorAmount = TryReadMonthlyAmount(prior.FormPayloadJson);
        var delta = priorAmount is { } pa
            ? newMonthlyAmountMdl - pa
            : newMonthlyAmountMdl;

        // Zero-delta short-circuit: no new document, no audit, no notification.
        if (priorAmount is not null && delta == 0)
        {
            return Result<DecisionRecomputeOutcomeDto>.Success(new DecisionRecomputeOutcomeDto(
                PriorAmount: priorAmount,
                NewAmount: newMonthlyAmountMdl,
                Delta: 0m,
                NewDocumentSqid: null,
                DocumentKindCode: DocCodeNoChange));
        }

        var now = _clock.UtcNow;
        var (templateCode, docKindCode) = delta >= 0
            ? (DecizieAjustareSumeTemplate.Code, DocCodeAdjustment)
            : (DecizieRecuperareSumeTemplate.Code, DocCodeRecuperare);

        var doc = new Document
        {
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            DossierId = prior.DossierId,
            Kind = DocumentKind.Decision,
            Title = $"{templateCode} (recompute {reason})",
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            SizeBytes = 0,
            StorageObjectKey = $"recompute/{Guid.NewGuid():N}",
            StorageBucket = StorageBucket,
            ContentSha256Hex = string.Empty,
            IsSigned = false,
            IsActive = true,
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            priorDecisionId = decoded.Value,
            priorAmount,
            newAmount = newMonthlyAmountMdl,
            delta,
            reason = reason.ToString(),
            newDocumentId = doc.Id,
            templateCode,
        });
        await _audit.RecordAsync(
            AuditRecomputed,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(ServiceApplication),
            decoded.Value,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        // Fire the citizen-inbox notification (best-effort).
        await TryDispatchActionResultAsync(prior, delta, newMonthlyAmountMdl, cancellationToken)
            .ConfigureAwait(false);

        return Result<DecisionRecomputeOutcomeDto>.Success(new DecisionRecomputeOutcomeDto(
            PriorAmount: priorAmount,
            NewAmount: newMonthlyAmountMdl,
            Delta: delta,
            NewDocumentSqid: _sqids.Encode(doc.Id),
            DocumentKindCode: docKindCode));
    }

    /// <summary>
    /// Best-effort parser for the <c>monthlyAmountMdl</c> key on a JSON form
    /// payload. Returns <c>null</c> when the payload is missing the key, the
    /// JSON is malformed, or the value is not numeric.
    /// </summary>
    private static decimal? TryReadMonthlyAmount(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (!doc.RootElement.TryGetProperty("monthlyAmountMdl", out var prop))
            {
                if (!doc.RootElement.TryGetProperty("amount", out prop))
                {
                    return null;
                }
            }
            return prop.ValueKind switch
            {
                JsonValueKind.Number => prop.GetDecimal(),
                JsonValueKind.String when decimal.TryParse(
                    prop.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) => s,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fires the R0174 / CF 22.03 ActionResult trigger so the beneficiary's
    /// inbox row reflects the recompute outcome. Best-effort: any thrown
    /// exception is logged at Warning and swallowed.
    /// </summary>
    private async Task TryDispatchActionResultAsync(
        ServiceApplication prior,
        decimal delta,
        decimal newAmount,
        CancellationToken cancellationToken)
    {
        if (_triggers is null)
        {
            return;
        }
        try
        {
            var subject = delta >= 0
                ? "Prestația Dvs. a fost ajustată"
                : "Decizie de recuperare a sumelor plătite în plus";
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "Suma lunară a fost recalculată: nou {0:F2} MDL, diferență {1:F2} MDL.",
                newAmount,
                delta);

            await _triggers.DispatchAsync(
                NotificationTriggerKind.ActionResult,
                new NotificationTriggerPayload(
                    RecipientUserId: prior.SolicitantId,
                    Subject: subject,
                    Body: body,
                    CorrelationId: _caller.CorrelationId,
                    RelatedEntityType: NotificationRelatedEntityTypes.Application,
                    RelatedEntityId: prior.Id),
                cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort notification — MUST NOT break the recompute pipeline.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ActionResult trigger dispatch failed for recomputed decision {AppSqid}; recompute unaffected.",
                _sqids.Encode(prior.Id));
        }
#pragma warning restore CA1031
    }
}
