using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.ServiceManagement;

/// <summary>
/// R2505 / TOR PIR 030-033 — production implementation of
/// <see cref="IChangeRequestService"/>. Owns the change-management lifecycle
/// and enforces four-eyes++ separation between requester / tester / signer /
/// approver.
/// </summary>
public sealed class ChangeRequestService : IChangeRequestService
{
    /// <summary>Cached JSON serializer options shared across audit payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _read;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IValidator<ChangeRequestCreateInputDto> _createValidator;
    private readonly IValidator<ChangeRequestTestValidationInputDto> _testValidator;
    private readonly IValidator<ChangeRequestSignCodeInputDto> _signValidator;
    private readonly IValidator<ChangeRequestRollbackInputDto> _rollbackValidator;
    private readonly IValidator<ChangeRequestReasonInputDto> _reasonValidator;
    private readonly IValidator<ChangeRequestFilterDto> _filterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="createValidator">Create-payload validator.</param>
    /// <param name="testValidator">Test-validation payload validator.</param>
    /// <param name="signValidator">Code-signing payload validator.</param>
    /// <param name="rollbackValidator">Rollback payload validator.</param>
    /// <param name="reasonValidator">Reason (cancel) payload validator.</param>
    /// <param name="filterValidator">List-filter validator.</param>
    public ChangeRequestService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<ChangeRequestCreateInputDto> createValidator,
        IValidator<ChangeRequestTestValidationInputDto> testValidator,
        IValidator<ChangeRequestSignCodeInputDto> signValidator,
        IValidator<ChangeRequestRollbackInputDto> rollbackValidator,
        IValidator<ChangeRequestReasonInputDto> reasonValidator,
        IValidator<ChangeRequestFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(testValidator);
        ArgumentNullException.ThrowIfNull(signValidator);
        ArgumentNullException.ThrowIfNull(rollbackValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _createValidator = createValidator;
        _testValidator = testValidator;
        _signValidator = signValidator;
        _rollbackValidator = rollbackValidator;
        _reasonValidator = reasonValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<ChangeRequestDto>> CreateAsync(
        ChangeRequestCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ChangeRequestDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        if (_caller.UserId is null)
        {
            return Result<ChangeRequestDto>.Failure(ErrorCodes.Unauthorized, "Anonymous callers cannot create a change request.");
        }

        var kind = Enum.Parse<ChangeRequestKind>(input.Kind, ignoreCase: false);
        var risk = Enum.Parse<ChangeRequestRisk>(input.Risk, ignoreCase: false);

        long? relatedWindowId = null;
        if (!string.IsNullOrWhiteSpace(input.RelatedMaintenanceWindowSqid))
        {
            var decoded = _sqids.TryDecode(input.RelatedMaintenanceWindowSqid);
            if (decoded.IsFailure)
            {
                return Result<ChangeRequestDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            }
            var win = await _db.MaintenanceWindows
                .FirstOrDefaultAsync(w => w.Id == decoded.Value, cancellationToken)
                .ConfigureAwait(false);
            if (win is null)
            {
                return Result<ChangeRequestDto>.Failure(ErrorCodes.NotFound, "Maintenance window not found.");
            }
            relatedWindowId = win.Id;
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var changeNumber = await MintChangeNumberAsync(now, cancellationToken).ConfigureAwait(false);

        var entity = new ChangeRequest
        {
            ChangeNumber = changeNumber,
            Title = input.Title,
            Description = input.Description,
            Kind = kind,
            Status = ChangeRequestStatus.Draft,
            Risk = risk,
            ImpactedSystems = input.ImpactedSystems,
            RollbackPlan = input.RollbackPlan,
            RequestedByUserId = (int)_caller.UserId.Value,
            RelatedMaintenanceWindowId = relatedWindowId,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.ChangeRequests.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            IChangeRequestService.AuditCreated,
            AuditSeverity.Sensitive,
            actor,
            entity.Id,
            new
            {
                changeSqid = _sqids.Encode(entity.Id),
                entity.ChangeNumber,
                kind = kind.ToString(),
                risk = risk.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<ChangeRequestDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public Task<Result<ChangeRequestDto>> SubmitAsync(
        string changeSqid,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            changeSqid,
            from: ChangeRequestStatus.Draft,
            to: ChangeRequestStatus.Submitted,
            auditCode: IChangeRequestService.AuditSubmitted,
            auditSeverity: AuditSeverity.Sensitive,
            preTransition: (entity, _) =>
            {
                // Belt-and-braces — validator already enforces 50 chars.
                if (string.IsNullOrWhiteSpace(entity.RollbackPlan)
                    || entity.RollbackPlan.Length < 50)
                {
                    return Result.Failure(
                        ErrorCodes.ValidationFailed,
                        "RollbackPlan must be at least 50 characters before Submit.");
                }
                return Result.Success();
            },
            onTransition: (_, _) =>
            {
                CnasMeter.ChangeRequestSubmitted.Add(1);
                return Result.Success();
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<ChangeRequestDto>> StartReviewAsync(
        string changeSqid,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            changeSqid,
            from: ChangeRequestStatus.Submitted,
            to: ChangeRequestStatus.InReview,
            auditCode: IChangeRequestService.AuditReviewStarted,
            auditSeverity: AuditSeverity.Notice,
            preTransition: null,
            onTransition: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<ChangeRequestDto>> ValidateTestEnvAsync(
        string changeSqid,
        ChangeRequestTestValidationInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _testValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ChangeRequestDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        if (_caller.UserId is null)
        {
            return Result<ChangeRequestDto>.Failure(ErrorCodes.Unauthorized, "Anonymous callers cannot validate test-env.");
        }

        var loaded = await LoadAsync(changeSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<ChangeRequestDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status != ChangeRequestStatus.InReview)
        {
            return Result<ChangeRequestDto>.Failure(
                ErrorCodes.ChangeRequestInvalidTransition,
                $"Cannot validate test-env; current status is '{entity.Status}' (required: InReview).");
        }

        var callerUserId = (int)_caller.UserId.Value;
        if (entity.RequestedByUserId == callerUserId)
        {
            return Result<ChangeRequestDto>.Failure(
                ErrorCodes.ChangeRequestSameOperator,
                "Test-env validator must differ from the requester.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var from = entity.Status;
        entity.Status = ChangeRequestStatus.TestEnvValidated;
        entity.TestEnvironmentValidationNote = input.ValidationNote;
        entity.TestValidatedByUserId = callerUserId;
        entity.TestValidatedAt = now;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        EmitStateMetric(entity.Kind, from, entity.Status);
        await EmitAuditAsync(
            IChangeRequestService.AuditTestEnvValidated,
            AuditSeverity.Critical,
            actor,
            entity.Id,
            new
            {
                changeSqid = _sqids.Encode(entity.Id),
                entity.ChangeNumber,
                from = from.ToString(),
                to = entity.Status.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<ChangeRequestDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<ChangeRequestDto>> SignCodeAsync(
        string changeSqid,
        ChangeRequestSignCodeInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _signValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ChangeRequestDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        if (_caller.UserId is null)
        {
            return Result<ChangeRequestDto>.Failure(ErrorCodes.Unauthorized, "Anonymous callers cannot sign code.");
        }

        var loaded = await LoadAsync(changeSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<ChangeRequestDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status != ChangeRequestStatus.TestEnvValidated)
        {
            return Result<ChangeRequestDto>.Failure(
                ErrorCodes.ChangeRequestInvalidTransition,
                $"Cannot sign code; current status is '{entity.Status}' (required: TestEnvValidated).");
        }

        var callerUserId = (int)_caller.UserId.Value;
        if (entity.RequestedByUserId == callerUserId
            || entity.TestValidatedByUserId == callerUserId)
        {
            return Result<ChangeRequestDto>.Failure(
                ErrorCodes.ChangeRequestSameOperator,
                "Code signer must differ from requester AND test-env validator.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var from = entity.Status;
        entity.Status = ChangeRequestStatus.CodeSigned;
        entity.CodeSignatureReference = input.CodeSignatureReference;
        entity.CodeSignedByUserId = callerUserId;
        entity.CodeSignedAt = now;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        EmitStateMetric(entity.Kind, from, entity.Status);
        await EmitAuditAsync(
            IChangeRequestService.AuditCodeSigned,
            AuditSeverity.Critical,
            actor,
            entity.Id,
            new
            {
                changeSqid = _sqids.Encode(entity.Id),
                entity.ChangeNumber,
                from = from.ToString(),
                to = entity.Status.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<ChangeRequestDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<ChangeRequestDto>> ApproveAsync(
        string changeSqid,
        CancellationToken cancellationToken = default)
    {
        if (_caller.UserId is null)
        {
            return Result<ChangeRequestDto>.Failure(ErrorCodes.Unauthorized, "Anonymous callers cannot approve.");
        }
        var loaded = await LoadAsync(changeSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<ChangeRequestDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status != ChangeRequestStatus.CodeSigned)
        {
            return Result<ChangeRequestDto>.Failure(
                ErrorCodes.ChangeRequestInvalidTransition,
                $"Cannot approve; current status is '{entity.Status}' (required: CodeSigned).");
        }

        var callerUserId = (int)_caller.UserId.Value;
        if (entity.RequestedByUserId == callerUserId
            || entity.TestValidatedByUserId == callerUserId
            || entity.CodeSignedByUserId == callerUserId)
        {
            return Result<ChangeRequestDto>.Failure(
                ErrorCodes.ChangeRequestSameOperator,
                "Approver must differ from requester, tester, and signer.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var from = entity.Status;
        entity.Status = ChangeRequestStatus.ApprovedForProd;
        entity.ApprovedByUserId = callerUserId;
        entity.ApprovedAt = now;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        EmitStateMetric(entity.Kind, from, entity.Status);
        await EmitAuditAsync(
            IChangeRequestService.AuditApprovedForProd,
            AuditSeverity.Critical,
            actor,
            entity.Id,
            new
            {
                changeSqid = _sqids.Encode(entity.Id),
                entity.ChangeNumber,
                from = from.ToString(),
                to = entity.Status.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<ChangeRequestDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public Task<Result<ChangeRequestDto>> StartDeploymentAsync(
        string changeSqid,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            changeSqid,
            from: ChangeRequestStatus.ApprovedForProd,
            to: ChangeRequestStatus.Deploying,
            auditCode: IChangeRequestService.AuditDeploymentStarted,
            auditSeverity: AuditSeverity.Sensitive,
            preTransition: null,
            onTransition: null,
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<ChangeRequestDto>> CompleteDeploymentAsync(
        string changeSqid,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            changeSqid,
            from: ChangeRequestStatus.Deploying,
            to: ChangeRequestStatus.Deployed,
            auditCode: IChangeRequestService.AuditDeploymentCompleted,
            auditSeverity: AuditSeverity.Critical,
            preTransition: null,
            onTransition: (entity, now) =>
            {
                entity.DeployedAt = now;
                return Result.Success();
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<ChangeRequestDto>> RollBackAsync(
        string changeSqid,
        ChangeRequestRollbackInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _rollbackValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ChangeRequestDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadAsync(changeSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<ChangeRequestDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status is not (ChangeRequestStatus.Deploying or ChangeRequestStatus.Deployed))
        {
            return Result<ChangeRequestDto>.Failure(
                ErrorCodes.ChangeRequestInvalidTransition,
                $"Cannot roll back from '{entity.Status}' (must be Deploying or Deployed).");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var from = entity.Status;
        entity.Status = ChangeRequestStatus.RolledBack;
        entity.RollbackReason = input.Reason;
        entity.RolledBackAt = now;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.ChangeRequestRollback.Add(
            1,
            new KeyValuePair<string, object?>("kind", entity.Kind.ToString()));
        EmitStateMetric(entity.Kind, from, entity.Status);
        await EmitAuditAsync(
            IChangeRequestService.AuditRolledBack,
            AuditSeverity.Critical,
            actor,
            entity.Id,
            new
            {
                changeSqid = _sqids.Encode(entity.Id),
                entity.ChangeNumber,
                from = from.ToString(),
                to = entity.Status.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<ChangeRequestDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<ChangeRequestDto>> CancelAsync(
        string changeSqid,
        ChangeRequestReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ChangeRequestDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadAsync(changeSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<ChangeRequestDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status is ChangeRequestStatus.Deployed
            or ChangeRequestStatus.RolledBack
            or ChangeRequestStatus.Cancelled)
        {
            return Result<ChangeRequestDto>.Failure(
                ErrorCodes.ChangeRequestInvalidTransition,
                $"Cannot cancel a change in terminal state '{entity.Status}'.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var from = entity.Status;
        entity.Status = ChangeRequestStatus.Cancelled;
        entity.CancelReason = input.Reason;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        EmitStateMetric(entity.Kind, from, entity.Status);
        await EmitAuditAsync(
            IChangeRequestService.AuditCancelled,
            AuditSeverity.Critical,
            actor,
            entity.Id,
            new
            {
                changeSqid = _sqids.Encode(entity.Id),
                entity.ChangeNumber,
                from = from.ToString(),
                to = entity.Status.ToString(),
                reason = input.Reason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<ChangeRequestDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<ChangeRequestDto>> GetByIdAsync(
        string changeSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(changeSqid);
        if (decoded.IsFailure)
        {
            return Result<ChangeRequestDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _read.ChangeRequests
            .FirstOrDefaultAsync(e => e.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<ChangeRequestDto>.Failure(ErrorCodes.NotFound, "Change request not found.")
            : Result<ChangeRequestDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<ChangeRequestPageDto>> ListAsync(
        ChangeRequestFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ChangeRequestPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<ChangeRequest> q = _read.ChangeRequests;
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<ChangeRequestStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(e => e.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.Kind)
            && Enum.TryParse<ChangeRequestKind>(filter.Kind, ignoreCase: false, out var kind))
        {
            q = q.Where(e => e.Kind == kind);
        }
        if (!string.IsNullOrWhiteSpace(filter.Risk)
            && Enum.TryParse<ChangeRequestRisk>(filter.Risk, ignoreCase: false, out var risk))
        {
            q = q.Where(e => e.Risk == risk);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new ChangeRequestPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<ChangeRequestPageDto>.Success(page);
    }

    /// <summary>Common helper for simple state-to-state transitions.</summary>
    /// <param name="changeSqid">Sqid of the change.</param>
    /// <param name="from">Required current state.</param>
    /// <param name="to">Target state.</param>
    /// <param name="auditCode">Stable audit event code.</param>
    /// <param name="auditSeverity">Audit severity.</param>
    /// <param name="preTransition">Optional pre-transition guard returning a Result.</param>
    /// <param name="onTransition">Optional callback after the entity's status is flipped.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    private async Task<Result<ChangeRequestDto>> TransitionAsync(
        string changeSqid,
        ChangeRequestStatus from,
        ChangeRequestStatus to,
        string auditCode,
        AuditSeverity auditSeverity,
        Func<ChangeRequest, DateTime, Result>? preTransition,
        Func<ChangeRequest, DateTime, Result>? onTransition,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(changeSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<ChangeRequestDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status != from)
        {
            return Result<ChangeRequestDto>.Failure(
                ErrorCodes.ChangeRequestInvalidTransition,
                $"Cannot transition from '{entity.Status}' to '{to}'; required '{from}'.");
        }

        var now = _clock.UtcNow;
        if (preTransition is not null)
        {
            var guard = preTransition(entity, now);
            if (guard.IsFailure)
            {
                return Result<ChangeRequestDto>.Failure(guard.ErrorCode!, guard.ErrorMessage!);
            }
        }

        var actor = _caller.UserSqid ?? "admin";
        var fromStatus = entity.Status;
        entity.Status = to;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        if (onTransition is not null)
        {
            var post = onTransition(entity, now);
            if (post.IsFailure)
            {
                return Result<ChangeRequestDto>.Failure(post.ErrorCode!, post.ErrorMessage!);
            }
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        EmitStateMetric(entity.Kind, fromStatus, to);
        await EmitAuditAsync(
            auditCode,
            auditSeverity,
            actor,
            entity.Id,
            new
            {
                changeSqid = _sqids.Encode(entity.Id),
                entity.ChangeNumber,
                from = fromStatus.ToString(),
                to = to.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<ChangeRequestDto>.Success(ToDto(entity));
    }

    /// <summary>Generates the deterministic <c>CHG-{year}-{seq:000000}</c> change number.</summary>
    /// <param name="now">Current UTC instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The next change number for the current year.</returns>
    private async Task<string> MintChangeNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        var year = now.Year;
        var yearPrefix = $"CHG-{year}-";
        var sequence = await _db.ChangeRequests
            .Where(e => e.ChangeNumber.StartsWith(yearPrefix))
            .CountAsync(cancellationToken).ConfigureAwait(false) + 1;
        return string.Create(CultureInfo.InvariantCulture, $"{yearPrefix}{sequence:D6}");
    }

    /// <summary>Loads a tracked change-request entity by Sqid.</summary>
    /// <param name="changeSqid">Sqid of the change.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tracked entity on success.</returns>
    private async Task<Result<ChangeRequest>> LoadAsync(string changeSqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(changeSqid);
        if (decoded.IsFailure)
        {
            return Result<ChangeRequest>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.ChangeRequests
            .FirstOrDefaultAsync(e => e.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<ChangeRequest>.Failure(ErrorCodes.NotFound, "Change request not found.")
            : Result<ChangeRequest>.Success(row);
    }

    /// <summary>Emits the state-changed counter with kind + from/to tags.</summary>
    /// <param name="kind">Change kind.</param>
    /// <param name="from">Previous state.</param>
    /// <param name="to">New state.</param>
    private static void EmitStateMetric(ChangeRequestKind kind, ChangeRequestStatus from, ChangeRequestStatus to)
    {
        CnasMeter.ChangeRequestStateChanged.Add(
            1,
            new TagList
            {
                { "kind", kind.ToString() },
                { "from_status", from.ToString() },
                { "to_status", to.ToString() },
            });
    }

    /// <summary>Writes a single audit row with a serialised details payload.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row.</param>
    /// <param name="details">Arbitrary anonymous object serialised to JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when the audit row is enqueued.</returns>
    private async Task EmitAuditAsync(
        string eventCode,
        AuditSeverity severity,
        string actor,
        long targetEntityId,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode,
            severity,
            actor,
            nameof(ChangeRequest),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an entity into its outbound DTO.</summary>
    /// <param name="e">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private ChangeRequestDto ToDto(ChangeRequest e) => new(
        Id: _sqids.Encode(e.Id),
        ChangeNumber: e.ChangeNumber,
        Title: e.Title,
        Description: e.Description,
        Kind: e.Kind.ToString(),
        Status: e.Status.ToString(),
        Risk: e.Risk.ToString(),
        ImpactedSystems: e.ImpactedSystems,
        RollbackPlan: e.RollbackPlan,
        TestEnvironmentValidationNote: e.TestEnvironmentValidationNote,
        TestValidatedBySqid: e.TestValidatedByUserId is null ? null : _sqids.Encode(e.TestValidatedByUserId.Value),
        TestValidatedAt: e.TestValidatedAt,
        CodeSignatureReference: e.CodeSignatureReference,
        CodeSignedBySqid: e.CodeSignedByUserId is null ? null : _sqids.Encode(e.CodeSignedByUserId.Value),
        CodeSignedAt: e.CodeSignedAt,
        RequestedBySqid: _sqids.Encode(e.RequestedByUserId),
        ApprovedBySqid: e.ApprovedByUserId is null ? null : _sqids.Encode(e.ApprovedByUserId.Value),
        ApprovedAt: e.ApprovedAt,
        DeployedAt: e.DeployedAt,
        RolledBackAt: e.RolledBackAt,
        RollbackReason: e.RollbackReason,
        CancelReason: e.CancelReason,
        RelatedMaintenanceWindowSqid: e.RelatedMaintenanceWindowId is null ? null : _sqids.Encode(e.RelatedMaintenanceWindowId.Value));
}
