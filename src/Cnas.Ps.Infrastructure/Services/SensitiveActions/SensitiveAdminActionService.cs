using System.Diagnostics.Metrics;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.SensitiveActions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.SensitiveActions;

/// <summary>
/// R2273 / TOR SEC 027 — production implementation of
/// <see cref="ISensitiveAdminActionService"/>. Carries the generic 4-eyes workflow
/// (request → approve / reject / cancel → execute / expire) shared across every
/// registered <see cref="ISensitiveActionPolicy"/> / <see cref="ISensitiveActionHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same-operator invariant.</b> Approval and rejection paths verify the inbound
/// approver id is not equal to the original requester id. Violations return
/// <see cref="ErrorCodes.FourEyesSameOperator"/>; the service layer is the canonical
/// guard — the controller is a thin pass-through.
/// </para>
/// <para>
/// <b>Critical audit attribution.</b> Every successful state transition (request,
/// approve, reject, cancel, execute, execution-failed, sweep) writes a
/// <see cref="AuditSeverity.Critical"/> audit row. Failures (validation, not-found,
/// conflict) do not — those surface to the caller via the
/// <see cref="Result{T}"/> envelope.
/// </para>
/// <para>
/// <b>Sanitised failure capture.</b> A failing handler returns a sanitised
/// <see cref="Result{T}"/> failure (no stack trace, no PII). If the handler THROWS the
/// substrate catches the exception and stores
/// <c>ExecutionFailureReason = $"HANDLER_ERROR:{exception.GetType().Name}"</c>.
/// </para>
/// </remarks>
public sealed class SensitiveAdminActionService : ISensitiveAdminActionService
{
    /// <summary>Default expiration window applied when no policy override is supplied.</summary>
    public static readonly TimeSpan DefaultExpirationWindow = TimeSpan.FromHours(72);

    /// <summary>Stable placeholder reason captured when no handler is registered for the action code.</summary>
    public const string NoHandlerRegistered = "NO_HANDLER_REGISTERED";

    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly ISensitiveActionRegistry _registry;
    private readonly IReadOnlyDictionary<string, ISensitiveActionHandler> _handlers;
    private readonly IValidator<SensitiveAdminActionRequestInputDto> _requestValidator;
    private readonly IValidator<SensitiveAdminActionApprovalInputDto> _approvalValidator;
    private readonly IValidator<SensitiveAdminActionReasonInputDto> _reasonValidator;
    private readonly IValidator<SensitiveAdminActionFilterDto> _filterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer DB context.</param>
    /// <param name="sqids">Sqid encoder/decoder for boundary id translation.</param>
    /// <param name="clock">UTC clock abstraction (CLAUDE.md RULE 4).</param>
    /// <param name="caller">Caller context (requester / approver id source).</param>
    /// <param name="audit">Audit service — emits the Critical lifecycle rows.</param>
    /// <param name="registry">Read-only policy registry consulted at request time.</param>
    /// <param name="policies">Every registered <see cref="ISensitiveActionPolicy"/>.</param>
    /// <param name="handlers">Every registered <see cref="ISensitiveActionHandler"/>.</param>
    /// <param name="requestValidator">Validator for the request envelope.</param>
    /// <param name="approvalValidator">Validator for the approval envelope.</param>
    /// <param name="reasonValidator">Validator for the reason envelope.</param>
    /// <param name="filterValidator">Validator for the filter envelope.</param>
    public SensitiveAdminActionService(
        ICnasDbContext db,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService audit,
        ISensitiveActionRegistry registry,
        IEnumerable<ISensitiveActionPolicy> policies,
        IEnumerable<ISensitiveActionHandler> handlers,
        IValidator<SensitiveAdminActionRequestInputDto> requestValidator,
        IValidator<SensitiveAdminActionApprovalInputDto> approvalValidator,
        IValidator<SensitiveAdminActionReasonInputDto> reasonValidator,
        IValidator<SensitiveAdminActionFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(requestValidator);
        ArgumentNullException.ThrowIfNull(approvalValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _audit = audit;
        _registry = registry;
        // Materialise policies into a lookup so RequestAsync can find the per-action
        // expiration override + invoke ValidatePayloadAsync without enumerating on each
        // call. Last registration wins on collision (matches SensitiveActionRegistry).
        var policyByCode = new Dictionary<string, ISensitiveActionPolicy>(StringComparer.Ordinal);
        foreach (var p in policies)
        {
            policyByCode[p.ActionCode] = p;
        }
        _policies = policyByCode;
        var handlerByCode = new Dictionary<string, ISensitiveActionHandler>(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
            handlerByCode[h.ActionCode] = h;
        }
        _handlers = handlerByCode;
        _requestValidator = requestValidator;
        _approvalValidator = approvalValidator;
        _reasonValidator = reasonValidator;
        _filterValidator = filterValidator;
    }

    /// <summary>Materialised policy lookup used by the request path.</summary>
    private readonly IReadOnlyDictionary<string, ISensitiveActionPolicy> _policies;

    /// <inheritdoc />
    public async Task<Result<SensitiveAdminActionDto>> RequestAsync(
        SensitiveAdminActionRequestInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _requestValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<SensitiveAdminActionDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        if (_caller.UserId is not long requesterId)
        {
            return Result<SensitiveAdminActionDto>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // Fail-fast when the action code has no registered policy — operators get
        // immediate feedback instead of a row queued against a handler that will never
        // fire. The registry encodes the well-known set; unknown codes are policy
        // violations.
        if (!_registry.IsKnown(input.ActionCode))
        {
            return Result<SensitiveAdminActionDto>.Failure(
                ErrorCodes.FourEyesUnknownAction,
                $"No sensitive-action policy registered for code '{input.ActionCode}'.");
        }

        // Delegate to the policy for payload-shape validation. Defensive: the policy is
        // free to inject its own dependencies for cross-payload checks (e.g. confirming
        // a referenced user exists). A failure short-circuits the request.
        if (_policies.TryGetValue(input.ActionCode, out var policy))
        {
            var payloadResult = await policy.ValidatePayloadAsync(input.RequestPayloadJson, ct)
                .ConfigureAwait(false);
            if (payloadResult.IsFailure)
            {
                return Result<SensitiveAdminActionDto>.Failure(
                    payloadResult.ErrorCode!,
                    payloadResult.ErrorMessage!);
            }
        }

        var now = _clock.UtcNow;
        var expirationWindow = policy?.ExpirationOverride ?? DefaultExpirationWindow;
        var row = new SensitiveAdminAction
        {
            ActionCode = input.ActionCode,
            Status = SensitiveAdminActionStatus.PendingApproval,
            RequestedByUserId = requesterId,
            RequestedAt = now,
            RequestReason = input.RequestReason,
            RequestPayloadJson = input.RequestPayloadJson,
            ExpiresAt = now + expirationWindow,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.SensitiveAdminActions.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Counter increments AFTER persistence so SaveChanges throws don't inflate
        // success rates. Tag with action_code so dashboards can chart volume per kind.
        CnasMeter.SensitiveAdminActionRequested.Add(1,
            new KeyValuePair<string, object?>("action_code", input.ActionCode));

        await EmitAuditAsync("SENS_ADMIN.REQUESTED", row, extraJson: null, ct).ConfigureAwait(false);

        return Result<SensitiveAdminActionDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<SensitiveAdminActionDto>> ApproveAsync(
        string sqid,
        SensitiveAdminActionApprovalInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _approvalValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<SensitiveAdminActionDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var loaded = await LoadPendingAsync(sqid, ct).ConfigureAwait(false);
        if (loaded.Failure is { } failure) return Result<SensitiveAdminActionDto>.From(failure);
        var row = loaded.Row!;

        if (_caller.UserId is not long approverId)
        {
            return Result<SensitiveAdminActionDto>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        if (approverId == row.RequestedByUserId)
        {
            // The very point of the 4-eyes ceremony — same operator cannot self-approve.
            return Result<SensitiveAdminActionDto>.Failure(
                ErrorCodes.FourEyesSameOperator,
                "Approver must be distinct from the original requester.");
        }

        var now = _clock.UtcNow;
        row.Status = SensitiveAdminActionStatus.Approved;
        row.ApprovedByUserId = approverId;
        row.ApprovedAt = now;
        row.ApprovalNote = input.Note;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        CnasMeter.SensitiveAdminActionOutcome.Add(1,
            new KeyValuePair<string, object?>("action_code", row.ActionCode),
            new KeyValuePair<string, object?>("outcome", "approved"));
        await EmitAuditAsync("SENS_ADMIN.APPROVED", row, extraJson: null, ct).ConfigureAwait(false);

        // Invoke the registered handler. Missing handler → ExecutionFailed/NO_HANDLER
        // (still a successful approval transition — operator surface remains consistent).
        await ExecuteHandlerAsync(row, ct).ConfigureAwait(false);

        return Result<SensitiveAdminActionDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<SensitiveAdminActionDto>> RejectAsync(
        string sqid,
        SensitiveAdminActionReasonInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<SensitiveAdminActionDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var loaded = await LoadPendingAsync(sqid, ct).ConfigureAwait(false);
        if (loaded.Failure is { } failure) return Result<SensitiveAdminActionDto>.From(failure);
        var row = loaded.Row!;

        if (_caller.UserId is not long rejecterId)
        {
            return Result<SensitiveAdminActionDto>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        if (rejecterId == row.RequestedByUserId)
        {
            return Result<SensitiveAdminActionDto>.Failure(
                ErrorCodes.FourEyesSameOperator,
                "Rejecter must be distinct from the original requester.");
        }

        var now = _clock.UtcNow;
        row.Status = SensitiveAdminActionStatus.Rejected;
        row.RejectedByUserId = rejecterId;
        row.RejectedAt = now;
        row.RejectionReason = input.Reason;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        CnasMeter.SensitiveAdminActionOutcome.Add(1,
            new KeyValuePair<string, object?>("action_code", row.ActionCode),
            new KeyValuePair<string, object?>("outcome", "rejected"));
        await EmitAuditAsync("SENS_ADMIN.REJECTED", row, extraJson: null, ct).ConfigureAwait(false);

        return Result<SensitiveAdminActionDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<SensitiveAdminActionDto>> CancelAsync(
        string sqid,
        SensitiveAdminActionReasonInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<SensitiveAdminActionDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var loaded = await LoadPendingAsync(sqid, ct).ConfigureAwait(false);
        if (loaded.Failure is { } failure) return Result<SensitiveAdminActionDto>.From(failure);
        var row = loaded.Row!;

        var now = _clock.UtcNow;
        row.Status = SensitiveAdminActionStatus.Cancelled;
        row.CancelledAt = now;
        row.CancelReason = input.Reason;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        CnasMeter.SensitiveAdminActionOutcome.Add(1,
            new KeyValuePair<string, object?>("action_code", row.ActionCode),
            new KeyValuePair<string, object?>("outcome", "cancelled"));
        await EmitAuditAsync("SENS_ADMIN.CANCELLED", row, extraJson: null, ct).ConfigureAwait(false);

        return Result<SensitiveAdminActionDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<SensitiveAdminActionDto>> GetByIdAsync(string sqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<SensitiveAdminActionDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.SensitiveAdminActions
            .SingleOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<SensitiveAdminActionDto>.Failure(ErrorCodes.NotFound, "Sensitive admin action not found.");
        }

        return Result<SensitiveAdminActionDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<SensitiveAdminActionPageDto>> ListAsync(
        SensitiveAdminActionFilterDto filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var validation = await _filterValidator.ValidateAsync(filter, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<SensitiveAdminActionPageDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        IQueryable<SensitiveAdminAction> q = _db.SensitiveAdminActions
            .Where(r => r.IsActive);

        if (!string.IsNullOrEmpty(filter.Status)
            && Enum.TryParse<SensitiveAdminActionStatus>(filter.Status, ignoreCase: false, out var statusEnum))
        {
            q = q.Where(r => r.Status == statusEnum);
        }
        if (!string.IsNullOrEmpty(filter.ActionCode))
        {
            q = q.Where(r => r.ActionCode == filter.ActionCode);
        }
        if (filter.RequestedAfter is { } after)
        {
            q = q.Where(r => r.RequestedAt >= after);
        }
        if (filter.RequestedBefore is { } before)
        {
            q = q.Where(r => r.RequestedAt <= before);
        }

        var total = await q.CountAsync(ct).ConfigureAwait(false);
        var rows = await q.OrderByDescending(r => r.RequestedAt)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        // Sqid encoding happens in-memory (ISqidService is not SQL-translatable).
        var items = rows.Select(Project).ToList();

        return Result<SensitiveAdminActionPageDto>.Success(new SensitiveAdminActionPageDto(
            Items: items,
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take));
    }

    /// <inheritdoc />
    public async Task<Result<int>> SweepExpiredAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        // Materialise the candidate rows so we can stamp UpdatedAtUtc + record audit.
        // The index on (Status, ExpiresAt) keeps the scan cheap.
        var stale = await _db.SensitiveAdminActions
            .Where(r => r.IsActive
                        && r.Status == SensitiveAdminActionStatus.PendingApproval
                        && r.ExpiresAt < now)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (stale.Count == 0)
        {
            return Result<int>.Success(0);
        }
        foreach (var row in stale)
        {
            row.Status = SensitiveAdminActionStatus.Expired;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = "system";
            CnasMeter.SensitiveAdminActionOutcome.Add(1,
                new KeyValuePair<string, object?>("action_code", row.ActionCode),
                new KeyValuePair<string, object?>("outcome", "expired"));
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        CnasMeter.SensitiveAdminActionExpired.Add(stale.Count);

        // Audit once per sweep — Critical severity, count-only payload (no individual
        // row payloads to avoid log-pumping when the sweep flips many rows at once).
        var details = JsonSerializer.Serialize(new
        {
            sweptCount = stale.Count,
            sweptAtUtc = now,
        });
        await _audit.RecordAsync(
            "SENS_ADMIN.EXPIRED_SWEEP",
            AuditSeverity.Critical,
            actorId: "system",
            targetEntity: nameof(SensitiveAdminAction),
            targetEntityId: null,
            detailsJson: details,
            sourceIp: null,
            correlationId: null,
            ct).ConfigureAwait(false);

        return Result<int>.Success(stale.Count);
    }

    /// <summary>
    /// Resolves <paramref name="sqid"/> to a PendingApproval row, returning a structured
    /// failure on Sqid-decode error / not-found / already-decided.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id from the caller.</param>
    /// <param name="ct">Cancellation propagation.</param>
    /// <returns>Tuple of the loaded row + an optional failure to propagate.</returns>
    private async Task<(SensitiveAdminAction? Row, Result? Failure)> LoadPendingAsync(
        string sqid,
        CancellationToken ct)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return (null, Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!));
        }

        var row = await _db.SensitiveAdminActions
            .SingleOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return (null, Result.Failure(ErrorCodes.NotFound, "Sensitive admin action not found."));
        }
        if (row.Status != SensitiveAdminActionStatus.PendingApproval)
        {
            return (null, Result.Failure(
                ErrorCodes.FourEyesAlreadyDecided,
                $"Sensitive admin action already in status {row.Status}."));
        }
        return (row, null);
    }

    /// <summary>
    /// Dispatches the approved <paramref name="row"/> to its registered handler and
    /// records the outcome. Missing handler → ExecutionFailed/NO_HANDLER_REGISTERED.
    /// </summary>
    /// <param name="row">The approved action row (Status already flipped + persisted).</param>
    /// <param name="ct">Cancellation propagation.</param>
    private async Task ExecuteHandlerAsync(SensitiveAdminAction row, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (!_handlers.TryGetValue(row.ActionCode, out var handler))
        {
            row.Status = SensitiveAdminActionStatus.ExecutionFailed;
            row.ExecutedAt = now;
            row.ExecutionFailureReason = NoHandlerRegistered;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            CnasMeter.SensitiveAdminActionExecutionResult.Add(1,
                new KeyValuePair<string, object?>("action_code", row.ActionCode),
                new KeyValuePair<string, object?>("result", "no_handler"));
            await EmitAuditAsync("SENS_ADMIN.EXECUTION_FAILED", row,
                extraJson: $"{{\"reason\":\"{NoHandlerRegistered}\"}}", ct).ConfigureAwait(false);
            return;
        }

        Result<string?> outcome;
        try
        {
            outcome = await handler.ExecuteAsync(row, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defensive — the contract is "handlers return Result-failure on error" but
            // a misbehaving handler that throws still produces a deterministic terminal
            // state instead of leaving the row in Approved-but-not-Executed limbo.
            row.Status = SensitiveAdminActionStatus.ExecutionFailed;
            row.ExecutedAt = now;
            row.ExecutionFailureReason = Sanitise($"HANDLER_ERROR:{ex.GetType().Name}");
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            CnasMeter.SensitiveAdminActionExecutionResult.Add(1,
                new KeyValuePair<string, object?>("action_code", row.ActionCode),
                new KeyValuePair<string, object?>("result", "failed"));
            await EmitAuditAsync("SENS_ADMIN.EXECUTION_FAILED", row,
                extraJson: $"{{\"reason\":\"{Sanitise(row.ExecutionFailureReason!)}\"}}", ct).ConfigureAwait(false);
            return;
        }

        if (outcome.IsSuccess)
        {
            row.Status = SensitiveAdminActionStatus.Executed;
            row.ExecutedAt = now;
            row.ExecutionResultJson = outcome.Value;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            CnasMeter.SensitiveAdminActionExecutionResult.Add(1,
                new KeyValuePair<string, object?>("action_code", row.ActionCode),
                new KeyValuePair<string, object?>("result", "succeeded"));
            await EmitAuditAsync("SENS_ADMIN.EXECUTED", row, extraJson: null, ct).ConfigureAwait(false);
        }
        else
        {
            row.Status = SensitiveAdminActionStatus.ExecutionFailed;
            row.ExecutedAt = now;
            row.ExecutionFailureReason = Sanitise(outcome.ErrorCode!);
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            CnasMeter.SensitiveAdminActionExecutionResult.Add(1,
                new KeyValuePair<string, object?>("action_code", row.ActionCode),
                new KeyValuePair<string, object?>("result", "failed"));
            await EmitAuditAsync("SENS_ADMIN.EXECUTION_FAILED", row,
                extraJson: $"{{\"reason\":\"{Sanitise(row.ExecutionFailureReason!)}\"}}", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Emits a stable Critical audit row for a sensitive-admin-action lifecycle event.
    /// </summary>
    /// <param name="eventCode">Stable event code (e.g. <c>SENS_ADMIN.APPROVED</c>).</param>
    /// <param name="row">The row whose state transitioned.</param>
    /// <param name="extraJson">Optional extra JSON to merge into the details payload.</param>
    /// <param name="ct">Cancellation propagation.</param>
    private async Task EmitAuditAsync(
        string eventCode,
        SensitiveAdminAction row,
        string? extraJson,
        CancellationToken ct)
    {
        var actor = _caller.UserSqid ?? "system";
        var details = JsonSerializer.Serialize(new
        {
            actionCode = row.ActionCode,
            status = row.Status.ToString(),
            requestedBy = _sqids.Encode(row.RequestedByUserId),
            // Payload deliberately omitted — Confidential by classification.
            extra = extraJson,
        });
        await _audit.RecordAsync(
            eventCode,
            AuditSeverity.Critical,
            actor,
            nameof(SensitiveAdminAction),
            row.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Strips control chars + truncates to the 1000-char column cap so a misbehaving
    /// handler cannot blow up the row OR leak a stack trace via the failure reason.
    /// </summary>
    /// <param name="value">Candidate failure reason.</param>
    /// <returns>The sanitised reason, capped at 1000 chars.</returns>
    private static string Sanitise(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Filter out control chars to keep the persisted value PII-free + grep-able.
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray());
        // Cap mirrors SensitiveAdminActionValidatorShared.ReasonMaxLength (internal).
        const int MaxSanitisedLength = 1000;
        return clean.Length <= MaxSanitisedLength
            ? clean
            : clean.Substring(0, MaxSanitisedLength);
    }

    /// <summary>Projects a domain row to its boundary DTO with Sqid-encoded user ids.</summary>
    /// <param name="row">The domain row.</param>
    /// <returns>The projection DTO.</returns>
    private SensitiveAdminActionDto Project(SensitiveAdminAction row)
        => new(
            Id: _sqids.Encode(row.Id),
            ActionCode: row.ActionCode,
            Status: row.Status.ToString(),
            RequestedByUserSqid: _sqids.Encode(row.RequestedByUserId),
            RequestedAt: row.RequestedAt,
            RequestReason: row.RequestReason,
            RequestPayloadJson: row.RequestPayloadJson,
            ApprovedByUserSqid: row.ApprovedByUserId is { } a ? _sqids.Encode(a) : null,
            ApprovedAt: row.ApprovedAt,
            ApprovalNote: row.ApprovalNote,
            RejectedByUserSqid: row.RejectedByUserId is { } rj ? _sqids.Encode(rj) : null,
            RejectedAt: row.RejectedAt,
            RejectionReason: row.RejectionReason,
            CancelledAt: row.CancelledAt,
            CancelReason: row.CancelReason,
            ExpiresAt: row.ExpiresAt,
            ExecutedAt: row.ExecutedAt,
            ExecutionResultJson: row.ExecutionResultJson,
            ExecutionFailureReason: row.ExecutionFailureReason);
}
