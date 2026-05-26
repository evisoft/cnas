using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0933 / TOR §10.1 — concrete implementation of
/// <see cref="IPriorDecisionTerminator"/>. Wires the prior-active-decision
/// lookup + terminate + supersession-record sequence against the EF
/// <see cref="ICnasDbContext"/>, the <see cref="IAuditService"/>, and the
/// optional <see cref="INotificationTriggerDispatcher"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lookup contract.</b> "Prior active decision" = a soft-active
/// <see cref="ServiceApplication"/> row in <see cref="ApplicationStatus.Approved"/>
/// for the same <c>(Solicitant, ServicePassport.Code)</c> pair as the new
/// decision, ordered by <see cref="ServiceApplication.SubmittedAtUtc"/> DESC
/// (most recent wins). Rows that share a passport id but differ in
/// <see cref="ServicePassport.Version"/> still belong to the same service code
/// and are therefore comparable.
/// </para>
/// <para>
/// <b>Idempotency.</b> Looks up the (PreviousDecisionId, NewDecisionId)
/// supersession row before inserting. When found, the existing row is
/// projected and returned without re-stamping or re-auditing.
/// </para>
/// <para>
/// <b>Amount extraction.</b> Pulls <c>monthlyAmountMdl</c> from the
/// <see cref="ServiceApplication.FormPayloadJson"/> when present (matches the
/// <c>DocumentGenerationService</c> facts vocabulary). Missing / non-numeric
/// values surface as <c>null</c> on the supersession row — the comparison
/// surface caller is responsible for interpreting that as "amount unknown,
/// do not gate".
/// </para>
/// </remarks>
public sealed class PriorDecisionTerminator(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    ILogger<PriorDecisionTerminator> logger,
    INotificationTriggerDispatcher? triggers = null)
    : IPriorDecisionTerminator
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly ILogger<PriorDecisionTerminator> _logger = logger;
    private readonly INotificationTriggerDispatcher? _triggers = triggers;

    /// <inheritdoc />
    public async Task<Result<DecisionSupersessionDto?>> TerminateOnAcceptanceAsync(
        long newDecisionId,
        CancellationToken cancellationToken = default)
    {
        // Resolve the new decision so we know its (Solicitant, ServiceCode) scope.
        var newDecision = await _db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == newDecisionId && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (newDecision is null)
        {
            return Result<DecisionSupersessionDto?>.Failure(
                ErrorCodes.NotFound,
                $"New decision id={newDecisionId} not found or inactive.");
        }

        // Resolve the new decision's service code via the pinned passport.
        var newPassport = await _db.ServicePassports.AsNoTracking()
            .Where(p => p.Id == newDecision.ServicePassportId)
            .Select(p => new { p.Id, p.Code })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (newPassport is null)
        {
            // Defensive — a dangling FK indicates a corrupted aggregate.
            return Result<DecisionSupersessionDto?>.Failure(
                ErrorCodes.NotFound,
                $"Service passport id={newDecision.ServicePassportId} for new decision id={newDecisionId} not found.");
        }

        // Idempotency guard — if a supersession row already exists for THIS new decision
        // we short-circuit before attempting any state change. The DB-side unique index
        // on (PreviousDecisionId, NewDecisionId) backstops this check. Looking up by
        // NewDecisionId alone is sufficient (and necessary, because the prior decision's
        // status was flipped to Closed on the first call and would no longer match the
        // Active+Approved filter in FindPriorActiveDecisionAsync).
        var existing = await _db.DecisionSupersessions.AsNoTracking()
            .SingleOrDefaultAsync(
                s => s.NewDecisionId == newDecisionId && s.IsActive,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Result<DecisionSupersessionDto?>.Success(Project(existing));
        }

        // Find the prior active decision for the same (Solicitant, ServiceCode) pair.
        // The narrowing happens on the passport's external Code so a version bump on the
        // passport does not break the "same service" relation. Excludes the new decision
        // itself defensively (the new one may already be Approved when this runs).
        var prior = await FindPriorActiveDecisionAsync(
            newDecision.SolicitantId, newPassport.Code, newDecisionId, cancellationToken)
            .ConfigureAwait(false);

        if (prior is null)
        {
            // No prior active decision exists — first-time applicant; nothing to terminate.
            return Result<DecisionSupersessionDto?>.Success(null);
        }

        // Capture the prior + new amount snapshots from the form payload (best-effort).
        var priorAmount = TryReadMonthlyAmount(prior.FormPayloadJson);
        var newAmount = TryReadMonthlyAmount(newDecision.FormPayloadJson);

        var now = _clock.UtcNow;

        // Terminate the prior decision — mutate the tracked entity so we persist
        // through SaveChangesAsync. We re-load the row with tracking enabled (the
        // earlier query was AsNoTracking for read-only lookup).
        var trackedPrior = await _db.Applications
            .SingleAsync(a => a.Id == prior.Id, cancellationToken)
            .ConfigureAwait(false);
        trackedPrior.TransitionStatus(ApplicationStatus.Closed, now);
        trackedPrior.ClosedAtUtc = now;
        trackedPrior.UpdatedAtUtc = now;

        var row = new DecisionSupersession
        {
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            PreviousDecisionId = prior.Id,
            NewDecisionId = newDecisionId,
            SupersededAtUtc = now,
            SupersededByUserId = _caller.UserId,
            Reason = BuildReason(priorAmount, newAmount),
            PriorAmount = priorAmount,
            NewAmount = newAmount,
            IsActive = true,
        };
        _db.DecisionSupersessions.Add(row);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Audit the supersession with both raw ids embedded for forensic queries.
        var detailsJson = JsonSerializer.Serialize(new
        {
            previousDecisionId = prior.Id,
            newDecisionId,
            serviceCode = newPassport.Code,
            priorAmount,
            newAmount,
        });
        await _audit.RecordAsync(
            "DECISION.SUPERSEDED",
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(ServiceApplication),
            prior.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        // Best-effort outbound: notify the beneficiary their prior decision was terminated.
        await TryDispatchActionResultAsync(prior, newDecision, cancellationToken).ConfigureAwait(false);

        return Result<DecisionSupersessionDto?>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<DecisionComparisonDto>> CompareAsync(
        long newDecisionId,
        CancellationToken cancellationToken = default)
    {
        var newDecision = await _db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == newDecisionId && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (newDecision is null)
        {
            return Result<DecisionComparisonDto>.Failure(
                ErrorCodes.NotFound,
                $"New decision id={newDecisionId} not found or inactive.");
        }

        var newPassport = await _db.ServicePassports.AsNoTracking()
            .Where(p => p.Id == newDecision.ServicePassportId)
            .Select(p => new { p.Id, p.Code })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (newPassport is null)
        {
            return Result<DecisionComparisonDto>.Failure(
                ErrorCodes.NotFound,
                $"Service passport id={newDecision.ServicePassportId} for new decision id={newDecisionId} not found.");
        }

        var prior = await FindPriorActiveDecisionAsync(
            newDecision.SolicitantId, newPassport.Code, newDecisionId, cancellationToken)
            .ConfigureAwait(false);

        var newAmount = TryReadMonthlyAmount(newDecision.FormPayloadJson);

        if (prior is null)
        {
            return Result<DecisionComparisonDto>.Success(new DecisionComparisonDto(
                HasPrior: false,
                PreviousDecisionSqid: null,
                PriorAmount: null,
                NewAmount: newAmount,
                Difference: null,
                LowerSumWarning: false));
        }

        var priorAmount = TryReadMonthlyAmount(prior.FormPayloadJson);
        var difference = priorAmount is not null && newAmount is not null
            ? newAmount - priorAmount
            : (decimal?)null;
        var lowerSumWarning = priorAmount is not null && newAmount is not null && newAmount < priorAmount;

        return Result<DecisionComparisonDto>.Success(new DecisionComparisonDto(
            HasPrior: true,
            PreviousDecisionSqid: _sqids.Encode(prior.Id),
            PriorAmount: priorAmount,
            NewAmount: newAmount,
            Difference: difference,
            LowerSumWarning: lowerSumWarning));
    }

    /// <summary>
    /// Locates the most recently submitted ACTIVE
    /// <see cref="ServiceApplication"/> (status =
    /// <see cref="ApplicationStatus.Approved"/>) for the supplied solicitant +
    /// service code, excluding the new decision id from the candidate set.
    /// </summary>
    /// <param name="solicitantId">Raw solicitant id.</param>
    /// <param name="serviceCode">External passport code (R0142 contract).</param>
    /// <param name="excludeId">New decision id to exclude (self-protection).</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The prior decision row or <c>null</c> when none exists.</returns>
    private async Task<ServiceApplication?> FindPriorActiveDecisionAsync(
        long solicitantId,
        string serviceCode,
        long excludeId,
        CancellationToken cancellationToken)
    {
        // Join to passport via FK so we can narrow by code across passport versions.
        var query =
            from app in _db.Applications.AsNoTracking()
            join pass in _db.ServicePassports.AsNoTracking()
                on app.ServicePassportId equals pass.Id
            where app.SolicitantId == solicitantId
                && pass.Code == serviceCode
                && app.IsActive
                && app.Status == ApplicationStatus.Approved
                && app.Id != excludeId
            orderby app.SubmittedAtUtc descending, app.Id descending
            select app;
        return await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
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
                // Fallback: also try the unqualified "amount" key (R0140 informal payloads).
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
    /// Composes the <c>Reason</c> column value. Carries the lower-sum-warning
    /// flag verbatim so a reviewer auditing the supersession row can see
    /// whether the decider acknowledged a downgrade before approving.
    /// </summary>
    private static string BuildReason(decimal? priorAmount, decimal? newAmount)
    {
        if (priorAmount is null || newAmount is null)
        {
            return "Prior decision terminated on new acceptance (amount unknown).";
        }
        if (newAmount < priorAmount)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Prior decision terminated on new acceptance (lower-sum-warning: new={0} < prior={1}).",
                newAmount.Value,
                priorAmount.Value);
        }
        return string.Format(
            CultureInfo.InvariantCulture,
            "Prior decision terminated on new acceptance (new={0}, prior={1}).",
            newAmount.Value,
            priorAmount.Value);
    }

    /// <summary>
    /// Projects a domain row into the wire DTO with Sqid encoding applied to
    /// every id field.
    /// </summary>
    private DecisionSupersessionDto Project(DecisionSupersession row) => new(
        Id: _sqids.Encode(row.Id),
        PreviousDecisionSqid: _sqids.Encode(row.PreviousDecisionId),
        NewDecisionSqid: _sqids.Encode(row.NewDecisionId),
        SupersededAtUtc: row.SupersededAtUtc,
        SupersededByUserSqid: row.SupersededByUserId is { } u ? _sqids.Encode(u) : null,
        Reason: row.Reason,
        PriorAmount: row.PriorAmount,
        NewAmount: row.NewAmount);

    /// <summary>
    /// Fires the R0174 / CF 22.03 ActionResult trigger so the beneficiary's
    /// inbox carries a "prior decision X was terminated" note. Best-effort —
    /// any failure is logged at Warning and swallowed; the supersession's
    /// primary mutation has already committed.
    /// </summary>
    private async Task TryDispatchActionResultAsync(
        ServiceApplication prior,
        ServiceApplication newDecision,
        CancellationToken cancellationToken)
    {
        if (_triggers is null)
        {
            return;
        }

        try
        {
            var subject = "Decizia anterioară a fost încetată";
            var priorRef = prior.ReferenceNumber ?? _sqids.Encode(prior.Id);
            var newRef = newDecision.ReferenceNumber ?? _sqids.Encode(newDecision.Id);
            var body = $"Decizia anterioară (ref {priorRef}) a fost încetată automat în urma aprobării deciziei noi (ref {newRef}).";

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
#pragma warning disable CA1031 // Best-effort notification — MUST NOT break the supersession path.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ActionResult trigger dispatch failed for prior decision {PriorSqid}; supersession unaffected.",
                _sqids.Encode(prior.Id));
        }
#pragma warning restore CA1031
    }
}
