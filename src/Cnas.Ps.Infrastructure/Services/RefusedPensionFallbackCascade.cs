using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0942 / TOR §10.1 — concrete implementation of
/// <see cref="IRefusedPensionFallbackCascade"/>. Wires the pension-detection +
/// AlocatieSociala draft-creation sequence against the EF
/// <see cref="ICnasDbContext"/>.
/// </summary>
public sealed class RefusedPensionFallbackCascade : IRefusedPensionFallbackCascade
{
    /// <summary>Stable audit event code emitted on every successful cascade.</summary>
    public const string AuditFallbackInitiated = "DECISION.FALLBACK_INITIATED";

    /// <summary>Reason code returned when the cascade detected a pension refusal and acted.</summary>
    public const string ReasonFallbackInitiated = "FALLBACK_INITIATED";

    /// <summary>Reason code when the refused decision is NOT a pension.</summary>
    public const string ReasonNotPension = "NOT_A_PENSION_REFUSAL";

    /// <summary>Reason code when <c>WorkflowOptions.AutoFallbackToSocialAllowance</c> is false.</summary>
    public const string ReasonFeatureDisabled = "FEATURE_DISABLED";

    /// <summary>Reason code when a follow-up draft already exists for the same refused decision (idempotency).</summary>
    public const string ReasonAlreadyCascaded = "ALREADY_CASCADED";

    /// <summary>Reason code when the configured target passport is not seeded.</summary>
    public const string ReasonTargetMissing = "TARGET_PASSPORT_MISSING";

    /// <summary>
    /// Heuristic substring used to detect a pension passport from its stable Code.
    /// Matches every <c>SP-*-*-PENSION</c> / <c>SP-*-PENSIE-*</c> code seeded in
    /// <c>Cnas.Ps.Infrastructure.Persistence.Seed</c>.
    /// </summary>
    private static readonly string[] PensionCodeTokens = ["PENSION", "PENSIE"];

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IOptionsMonitor<WorkflowOptions> _options;

    /// <summary>Constructs the cascade with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction.</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated caller information for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="options">Workflow options carrying the toggle + target-passport code.</param>
    public RefusedPensionFallbackCascade(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IOptionsMonitor<WorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);

        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<Result<FallbackCascadeOutcomeDto>> EvaluateAsync(
        long refusedDecisionId,
        CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.AutoFallbackToSocialAllowance)
        {
            return Result<FallbackCascadeOutcomeDto>.Success(new FallbackCascadeOutcomeDto(
                WasCascadeTriggered: false,
                ReasonCode: ReasonFeatureDisabled,
                FallbackApplicationSqid: null));
        }

        var refused = await _db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == refusedDecisionId && a.IsActive, ct)
            .ConfigureAwait(false);
        if (refused is null)
        {
            return Result<FallbackCascadeOutcomeDto>.Failure(
                ErrorCodes.NotFound,
                $"Refused decision id={refusedDecisionId} not found.");
        }

        // Defensive: cascade only acts on Rejected rows. Non-Rejected statuses
        // produce a no-op so this method can be invoked unconditionally by the
        // workflow service.
        if (refused.Status != ApplicationStatus.Rejected)
        {
            return Result<FallbackCascadeOutcomeDto>.Success(new FallbackCascadeOutcomeDto(
                WasCascadeTriggered: false,
                ReasonCode: ReasonNotPension,
                FallbackApplicationSqid: null));
        }

        var refusedPassport = await _db.ServicePassports.AsNoTracking()
            .Where(p => p.Id == refused.ServicePassportId)
            .Select(p => new { p.Id, p.Code })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (refusedPassport is null || !IsPensionCode(refusedPassport.Code))
        {
            return Result<FallbackCascadeOutcomeDto>.Success(new FallbackCascadeOutcomeDto(
                WasCascadeTriggered: false,
                ReasonCode: ReasonNotPension,
                FallbackApplicationSqid: null));
        }

        // iter-149 — idempotency guard now includes soft-deleted (IsActive=false)
        // cascade drafts. Dropping the IsActive filter ensures that a re-cascade
        // attempt against a refused decision whose prior cascade-draft was soft-
        // deleted (admin tidy-up, citizen withdrawal) is still blocked. The marker
        // string embedded in FormPayloadJson is the only signal we need; the
        // active/inactive flag is orthogonal to "we already cascaded once".
        var marker = BuildIdempotencyMarker(refusedDecisionId);
        var existing = await _db.Applications.AsNoTracking()
            .Where(a => a.SolicitantId == refused.SolicitantId
                && a.FormPayloadJson.Contains(marker))
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (existing != 0)
        {
            return Result<FallbackCascadeOutcomeDto>.Success(new FallbackCascadeOutcomeDto(
                WasCascadeTriggered: false,
                ReasonCode: ReasonAlreadyCascaded,
                FallbackApplicationSqid: _sqids.Encode(existing)));
        }

        // Locate the target passport. Returns the most recently created active
        // row matching the configured code so passport-version churn does not
        // break the cascade.
        var target = await _db.ServicePassports.AsNoTracking()
            .Where(p => p.Code == opts.SocialAllowancePassportCode && p.IsActive)
            .OrderByDescending(p => p.Version)
            .ThenByDescending(p => p.Id)
            .Select(p => new { p.Id, p.Version, p.WorkflowCode })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (target is null)
        {
            return Result<FallbackCascadeOutcomeDto>.Success(new FallbackCascadeOutcomeDto(
                WasCascadeTriggered: false,
                ReasonCode: ReasonTargetMissing,
                FallbackApplicationSqid: null));
        }

        var now = _clock.UtcNow;
        var payload = BuildPrefillPayload(refusedDecisionId, refused.FormPayloadJson);
        var draft = new ServiceApplication
        {
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            SolicitantId = refused.SolicitantId,
            ServicePassportId = target.Id,
            PinnedServicePassportVersion = target.Version <= 0 ? 1 : target.Version,
            Status = ApplicationStatus.Draft,
            FormPayloadJson = payload,
            SnapshotJson = refused.SnapshotJson,
            IsActive = true,
        };
        _db.Applications.Add(draft);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            refusedDecisionId,
            fallbackApplicationId = draft.Id,
            sourcePassportCode = refusedPassport.Code,
            targetPassportCode = opts.SocialAllowancePassportCode,
        });
        await _audit.RecordAsync(
            AuditFallbackInitiated,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(ServiceApplication),
            draft.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<FallbackCascadeOutcomeDto>.Success(new FallbackCascadeOutcomeDto(
            WasCascadeTriggered: true,
            ReasonCode: ReasonFallbackInitiated,
            FallbackApplicationSqid: _sqids.Encode(draft.Id)));
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied passport code contains one of the
    /// pension family substrings (<c>"PENSION"</c> or <c>"PENSIE"</c>).
    /// Case-sensitive — codes in the seed table are uppercase by convention.
    /// </summary>
    private static bool IsPensionCode(string code)
    {
        foreach (var token in PensionCodeTokens)
        {
            if (code.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Stable string written into the cascade-issued draft's
    /// <c>FormPayloadJson</c> so the idempotency guard can find it on
    /// subsequent runs.
    /// </summary>
    private static string BuildIdempotencyMarker(long refusedDecisionId) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "\"cascadeFromDecisionId\":{0}",
            refusedDecisionId);

    /// <summary>
    /// Builds the prefilled JSON payload for the new draft. Merges the
    /// idempotency marker with the (best-effort) source payload so the citizen
    /// sees the values they already filled in for the refused pension.
    /// </summary>
    private static string BuildPrefillPayload(long refusedDecisionId, string? sourcePayloadJson)
    {
        var marker = BuildIdempotencyMarker(refusedDecisionId);
        // Strip the source root braces (best-effort) and concatenate; on a
        // malformed source we still produce a valid JSON object with the
        // marker key.
        if (!string.IsNullOrWhiteSpace(sourcePayloadJson)
            && sourcePayloadJson.StartsWith('{')
            && sourcePayloadJson.TrimEnd().EndsWith('}'))
        {
            var trimmed = sourcePayloadJson.Trim().Trim('{', '}').Trim();
            if (trimmed.Length == 0)
            {
                return "{" + marker + "}";
            }
            return "{" + marker + "," + trimmed + "}";
        }
        return "{" + marker + "}";
    }
}
