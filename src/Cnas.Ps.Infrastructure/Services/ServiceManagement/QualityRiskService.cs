using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// R2506 / TOR PIR 037-040 — production implementation of
/// <see cref="IQualityRiskService"/>. Manages the QA-risk registry,
/// preventive-action lifecycle, and the overdue-review query consumed by the
/// annual-review sweep job.
/// </summary>
public sealed class QualityRiskService : IQualityRiskService
{
    /// <summary>Stable cnas-admin role code consulted by the review-permission check.</summary>
    private const string AdminRole = "cnas-admin";

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
    private readonly IValidator<QualityRiskCreateInputDto> _createValidator;
    private readonly IValidator<QualityRiskModifyInputDto> _modifyValidator;
    private readonly IValidator<QualityRiskReviewInputDto> _reviewValidator;
    private readonly IValidator<QualityRiskReasonInputDto> _reasonValidator;
    private readonly IValidator<QualityRiskFilterDto> _filterValidator;
    private readonly IValidator<QualityRiskActionCreateInputDto> _actionCreateValidator;
    private readonly IValidator<QualityRiskActionModifyInputDto> _actionModifyValidator;
    private readonly IValidator<QualityRiskActionImplementInputDto> _actionImplementValidator;
    private readonly IValidator<QualityRiskActionReasonInputDto> _actionReasonValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="createValidator">Validator for risk-create input.</param>
    /// <param name="modifyValidator">Validator for risk-modify input.</param>
    /// <param name="reviewValidator">Validator for review input.</param>
    /// <param name="reasonValidator">Validator for risk-level reason inputs (close / accept).</param>
    /// <param name="filterValidator">Validator for list filter.</param>
    /// <param name="actionCreateValidator">Validator for action-create input.</param>
    /// <param name="actionModifyValidator">Validator for action-modify input.</param>
    /// <param name="actionImplementValidator">Validator for action-implement input.</param>
    /// <param name="actionReasonValidator">Validator for action-cancel input.</param>
    public QualityRiskService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<QualityRiskCreateInputDto> createValidator,
        IValidator<QualityRiskModifyInputDto> modifyValidator,
        IValidator<QualityRiskReviewInputDto> reviewValidator,
        IValidator<QualityRiskReasonInputDto> reasonValidator,
        IValidator<QualityRiskFilterDto> filterValidator,
        IValidator<QualityRiskActionCreateInputDto> actionCreateValidator,
        IValidator<QualityRiskActionModifyInputDto> actionModifyValidator,
        IValidator<QualityRiskActionImplementInputDto> actionImplementValidator,
        IValidator<QualityRiskActionReasonInputDto> actionReasonValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(reviewValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        ArgumentNullException.ThrowIfNull(actionCreateValidator);
        ArgumentNullException.ThrowIfNull(actionModifyValidator);
        ArgumentNullException.ThrowIfNull(actionImplementValidator);
        ArgumentNullException.ThrowIfNull(actionReasonValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _createValidator = createValidator;
        _modifyValidator = modifyValidator;
        _reviewValidator = reviewValidator;
        _reasonValidator = reasonValidator;
        _filterValidator = filterValidator;
        _actionCreateValidator = actionCreateValidator;
        _actionModifyValidator = actionModifyValidator;
        _actionImplementValidator = actionImplementValidator;
        _actionReasonValidator = actionReasonValidator;
    }

    /// <inheritdoc />
    public async Task<Result<QualityRiskDto>> CreateRiskAsync(
        QualityRiskCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<QualityRiskDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var category = Enum.Parse<QualityRiskCategory>(input.Category, ignoreCase: false);
        var likelihood = Enum.Parse<QualityRiskLikelihood>(input.Likelihood, ignoreCase: false);
        var impact = Enum.Parse<QualityRiskImpact>(input.Impact, ignoreCase: false);

        var ownerDecoded = _sqids.TryDecode(input.OwnerSqid);
        if (ownerDecoded.IsFailure)
        {
            return Result<QualityRiskDto>.Failure(ownerDecoded.ErrorCode!, ownerDecoded.ErrorMessage!);
        }

        // Defensive — reject duplicate code with a stable code.
        var dup = await _db.QualityRisks
            .AnyAsync(r => r.RiskCode == input.RiskCode, cancellationToken)
            .ConfigureAwait(false);
        if (dup)
        {
            return Result<QualityRiskDto>.Failure(
                ErrorCodes.QualityRiskDuplicateCode,
                $"A risk with code '{input.RiskCode}' already exists.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var entity = new QualityRisk
        {
            RiskCode = input.RiskCode,
            Title = input.Title,
            Description = input.Description,
            Category = category,
            Likelihood = likelihood,
            Impact = impact,
            Status = QualityRiskStatus.Open,
            OwnerUserId = (int)ownerDecoded.Value,
            IdentifiedAt = now,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.QualityRisks.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.QualityRiskCreated.Add(
            1,
            new KeyValuePair<string, object?>("category", category.ToString()));

        await EmitRiskAuditAsync(
            IQualityRiskService.AuditRiskCreated,
            AuditSeverity.Notice,
            actor,
            entity.Id,
            new
            {
                riskSqid = _sqids.Encode(entity.Id),
                entity.RiskCode,
                category = category.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<QualityRiskDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<QualityRiskDto>> ModifyRiskAsync(
        string riskSqid,
        QualityRiskModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _modifyValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<QualityRiskDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadRiskAsync(riskSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<QualityRiskDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status is QualityRiskStatus.Closed)
        {
            return Result<QualityRiskDto>.Failure(
                ErrorCodes.QualityRiskInvalidTransition,
                "Cannot modify a Closed risk.");
        }

        if (input.Title is not null) entity.Title = input.Title;
        if (input.Description is not null) entity.Description = input.Description;
        if (input.Category is not null) entity.Category = Enum.Parse<QualityRiskCategory>(input.Category, ignoreCase: false);
        if (input.Likelihood is not null) entity.Likelihood = Enum.Parse<QualityRiskLikelihood>(input.Likelihood, ignoreCase: false);
        if (input.Impact is not null) entity.Impact = Enum.Parse<QualityRiskImpact>(input.Impact, ignoreCase: false);
        if (input.OwnerSqid is not null)
        {
            var decoded = _sqids.TryDecode(input.OwnerSqid);
            if (decoded.IsFailure)
            {
                return Result<QualityRiskDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            }
            entity.OwnerUserId = (int)decoded.Value;
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitRiskAuditAsync(
            IQualityRiskService.AuditRiskModified,
            AuditSeverity.Notice,
            actor,
            entity.Id,
            new
            {
                riskSqid = _sqids.Encode(entity.Id),
                entity.RiskCode,
                changeReason = input.ChangeReason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<QualityRiskDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<QualityRiskDto>> CloseRiskAsync(
        string riskSqid,
        QualityRiskReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<QualityRiskDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadRiskAsync(riskSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<QualityRiskDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status is QualityRiskStatus.Closed)
        {
            return Result<QualityRiskDto>.Failure(
                ErrorCodes.QualityRiskInvalidTransition,
                "Risk is already Closed.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        entity.Status = QualityRiskStatus.Closed;
        entity.ClosedAt = now;
        entity.ClosureReason = input.Reason;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitRiskAuditAsync(
            IQualityRiskService.AuditRiskClosed,
            AuditSeverity.Sensitive,
            actor,
            entity.Id,
            new
            {
                riskSqid = _sqids.Encode(entity.Id),
                entity.RiskCode,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<QualityRiskDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<QualityRiskDto>> MarkMitigatingAsync(
        string riskSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadRiskAsync(riskSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<QualityRiskDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status != QualityRiskStatus.Open)
        {
            return Result<QualityRiskDto>.Failure(
                ErrorCodes.QualityRiskInvalidTransition,
                $"Cannot mark Mitigating from '{entity.Status}' (must be Open).");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        entity.Status = QualityRiskStatus.Mitigating;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitRiskAuditAsync(
            IQualityRiskService.AuditRiskMitigating,
            AuditSeverity.Notice,
            actor,
            entity.Id,
            new
            {
                riskSqid = _sqids.Encode(entity.Id),
                entity.RiskCode,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<QualityRiskDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<QualityRiskDto>> AcceptRiskAsync(
        string riskSqid,
        QualityRiskReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<QualityRiskDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadRiskAsync(riskSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<QualityRiskDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status is QualityRiskStatus.Closed or QualityRiskStatus.Accepted)
        {
            return Result<QualityRiskDto>.Failure(
                ErrorCodes.QualityRiskInvalidTransition,
                $"Cannot accept from terminal state '{entity.Status}'.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        entity.Status = QualityRiskStatus.Accepted;
        entity.ClosureReason = input.Reason;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitRiskAuditAsync(
            IQualityRiskService.AuditRiskAccepted,
            AuditSeverity.Sensitive,
            actor,
            entity.Id,
            new
            {
                riskSqid = _sqids.Encode(entity.Id),
                entity.RiskCode,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<QualityRiskDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<QualityRiskDto>> RecordReviewAsync(
        string riskSqid,
        QualityRiskReviewInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _reviewValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<QualityRiskDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        if (_caller.UserId is null)
        {
            return Result<QualityRiskDto>.Failure(ErrorCodes.Unauthorized, "Anonymous callers cannot record a review.");
        }
        var loaded = await LoadRiskAsync(riskSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<QualityRiskDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;

        var callerUserId = (int)_caller.UserId.Value;
        var isOwner = entity.OwnerUserId == callerUserId;
        var isAdmin = _caller.Roles.Contains(AdminRole, StringComparer.Ordinal);
        if (!isOwner && !isAdmin)
        {
            return Result<QualityRiskDto>.Failure(
                ErrorCodes.QualityRiskNotOwner,
                "Only the risk owner or a cnas-admin can record a review.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        entity.LastReviewedAt = now;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitRiskAuditAsync(
            IQualityRiskService.AuditRiskReviewed,
            AuditSeverity.Information,
            actor,
            entity.Id,
            new
            {
                riskSqid = _sqids.Encode(entity.Id),
                entity.RiskCode,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<QualityRiskDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<QualityRiskActionDto>> AddPreventiveActionAsync(
        string riskSqid,
        QualityRiskActionCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _actionCreateValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<QualityRiskActionDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadRiskAsync(riskSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<QualityRiskActionDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var risk = loaded.Value;

        var assigneeDecoded = _sqids.TryDecode(input.AssignedToSqid);
        if (assigneeDecoded.IsFailure)
        {
            return Result<QualityRiskActionDto>.Failure(assigneeDecoded.ErrorCode!, assigneeDecoded.ErrorMessage!);
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var entity = new QualityRiskPreventiveAction
        {
            RiskId = risk.Id,
            Description = input.Description,
            Status = QualityRiskActionStatus.Planned,
            DueDate = input.DueDate,
            AssignedToUserId = (int)assigneeDecoded.Value,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.QualityRiskPreventiveActions.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        EmitActionStateMetric(from: null, to: QualityRiskActionStatus.Planned);
        await EmitActionAuditAsync(
            IQualityRiskService.AuditActionAdded,
            AuditSeverity.Notice,
            actor,
            entity.Id,
            new
            {
                actionSqid = _sqids.Encode(entity.Id),
                riskSqid = _sqids.Encode(risk.Id),
                entity.DueDate,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<QualityRiskActionDto>.Success(ToActionDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<QualityRiskActionDto>> ModifyPreventiveActionAsync(
        string actionSqid,
        QualityRiskActionModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _actionModifyValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<QualityRiskActionDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadActionAsync(actionSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<QualityRiskActionDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status is QualityRiskActionStatus.Implemented
            or QualityRiskActionStatus.Cancelled)
        {
            return Result<QualityRiskActionDto>.Failure(
                ErrorCodes.QualityRiskActionInvalidTransition,
                $"Cannot modify an action in terminal state '{entity.Status}'.");
        }

        if (input.Description is not null) entity.Description = input.Description;
        if (input.DueDate is not null) entity.DueDate = input.DueDate.Value;
        if (input.AssignedToSqid is not null)
        {
            var decoded = _sqids.TryDecode(input.AssignedToSqid);
            if (decoded.IsFailure)
            {
                return Result<QualityRiskActionDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            }
            entity.AssignedToUserId = (int)decoded.Value;
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitActionAuditAsync(
            IQualityRiskService.AuditActionModified,
            AuditSeverity.Notice,
            actor,
            entity.Id,
            new
            {
                actionSqid = _sqids.Encode(entity.Id),
                changeReason = input.ChangeReason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<QualityRiskActionDto>.Success(ToActionDto(entity));
    }

    /// <inheritdoc />
    public Task<Result<QualityRiskActionDto>> MarkActionInProgressAsync(
        string actionSqid,
        CancellationToken cancellationToken = default)
        => ActionTransitionAsync(
            actionSqid,
            from: QualityRiskActionStatus.Planned,
            to: QualityRiskActionStatus.InProgress,
            auditCode: IQualityRiskService.AuditActionInProgress,
            preTransition: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<QualityRiskActionDto>> MarkActionImplementedAsync(
        string actionSqid,
        QualityRiskActionImplementInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _actionImplementValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<QualityRiskActionDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        return await ActionTransitionAsync(
            actionSqid,
            from: QualityRiskActionStatus.InProgress,
            to: QualityRiskActionStatus.Implemented,
            auditCode: IQualityRiskService.AuditActionImplemented,
            preTransition: (entity, now) =>
            {
                entity.CompletedAt = now;
                entity.CompletionNote = input.CompletionNote;
                return Result.Success();
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<QualityRiskActionDto>> CancelActionAsync(
        string actionSqid,
        QualityRiskActionReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _actionReasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<QualityRiskActionDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadActionAsync(actionSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<QualityRiskActionDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status is QualityRiskActionStatus.Implemented
            or QualityRiskActionStatus.Cancelled)
        {
            return Result<QualityRiskActionDto>.Failure(
                ErrorCodes.QualityRiskActionInvalidTransition,
                $"Cannot cancel an action in terminal state '{entity.Status}'.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var from = entity.Status;
        entity.Status = QualityRiskActionStatus.Cancelled;
        entity.CompletionNote = input.Reason;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        EmitActionStateMetric(from, entity.Status);
        await EmitActionAuditAsync(
            IQualityRiskService.AuditActionCancelled,
            AuditSeverity.Notice,
            actor,
            entity.Id,
            new
            {
                actionSqid = _sqids.Encode(entity.Id),
                from = from.ToString(),
                to = entity.Status.ToString(),
                reason = input.Reason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<QualityRiskActionDto>.Success(ToActionDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<QualityRiskDto>> GetRiskByIdAsync(
        string riskSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(riskSqid);
        if (decoded.IsFailure)
        {
            return Result<QualityRiskDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _read.QualityRisks
            .FirstOrDefaultAsync(r => r.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<QualityRiskDto>.Failure(ErrorCodes.NotFound, "Risk not found.")
            : Result<QualityRiskDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<QualityRiskPageDto>> ListAsync(
        QualityRiskFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<QualityRiskPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<QualityRisk> q = _read.QualityRisks;
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<QualityRiskStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(r => r.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.Category)
            && Enum.TryParse<QualityRiskCategory>(filter.Category, ignoreCase: false, out var category))
        {
            q = q.Where(r => r.Category == category);
        }
        if (!string.IsNullOrWhiteSpace(filter.Likelihood)
            && Enum.TryParse<QualityRiskLikelihood>(filter.Likelihood, ignoreCase: false, out var likelihood))
        {
            q = q.Where(r => r.Likelihood == likelihood);
        }
        if (!string.IsNullOrWhiteSpace(filter.Impact)
            && Enum.TryParse<QualityRiskImpact>(filter.Impact, ignoreCase: false, out var impact))
        {
            q = q.Where(r => r.Impact == impact);
        }
        if (!string.IsNullOrWhiteSpace(filter.OwnerSqid))
        {
            var decoded = _sqids.TryDecode(filter.OwnerSqid);
            if (decoded.IsFailure)
            {
                return Result<QualityRiskPageDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            }
            var ownerId = (int)decoded.Value;
            q = q.Where(r => r.OwnerUserId == ownerId);
        }
        if (filter.OverdueForReview is true)
        {
            var cutoff = _clock.UtcNow.AddDays(-IQualityRiskService.DefaultReviewWindowDays);
            q = q.Where(r => r.LastReviewedAt == null || r.LastReviewedAt < cutoff);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(r => r.IdentifiedAt)
            .ThenByDescending(r => r.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new QualityRiskPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<QualityRiskPageDto>.Success(page);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<QualityRiskDto>>> ListOverdueForReviewAsync(
        int sinceDays = IQualityRiskService.DefaultReviewWindowDays,
        CancellationToken cancellationToken = default)
    {
        if (sinceDays <= 0)
        {
            return Result<IReadOnlyList<QualityRiskDto>>.Failure(
                ErrorCodes.ValidationFailed,
                "sinceDays must be > 0.");
        }
        var cutoff = _clock.UtcNow.AddDays(-sinceDays);
        var rows = await _read.QualityRisks
            .Where(r => r.IsActive
                && r.Status != QualityRiskStatus.Closed
                && (r.LastReviewedAt == null || r.LastReviewedAt < cutoff))
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<QualityRiskDto> projection = rows.Select(ToDto).ToList();
        return Result<IReadOnlyList<QualityRiskDto>>.Success(projection);
    }

    /// <summary>Common helper for simple preventive-action state transitions.</summary>
    /// <param name="actionSqid">Sqid of the action.</param>
    /// <param name="from">Required current state.</param>
    /// <param name="to">Target state.</param>
    /// <param name="auditCode">Stable audit event code.</param>
    /// <param name="preTransition">Optional callback after the entity's status is flipped.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated action DTO on success.</returns>
    private async Task<Result<QualityRiskActionDto>> ActionTransitionAsync(
        string actionSqid,
        QualityRiskActionStatus from,
        QualityRiskActionStatus to,
        string auditCode,
        Func<QualityRiskPreventiveAction, DateTime, Result>? preTransition,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadActionAsync(actionSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<QualityRiskActionDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var entity = loaded.Value;
        if (entity.Status != from)
        {
            return Result<QualityRiskActionDto>.Failure(
                ErrorCodes.QualityRiskActionInvalidTransition,
                $"Cannot transition action from '{entity.Status}' to '{to}'; required '{from}'.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var fromStatus = entity.Status;
        entity.Status = to;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = actor;
        if (preTransition is not null)
        {
            var g = preTransition(entity, now);
            if (g.IsFailure)
            {
                return Result<QualityRiskActionDto>.Failure(g.ErrorCode!, g.ErrorMessage!);
            }
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        EmitActionStateMetric(fromStatus, to);
        await EmitActionAuditAsync(
            auditCode,
            AuditSeverity.Notice,
            actor,
            entity.Id,
            new
            {
                actionSqid = _sqids.Encode(entity.Id),
                from = fromStatus.ToString(),
                to = to.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<QualityRiskActionDto>.Success(ToActionDto(entity));
    }

    /// <summary>Emits the action-state-changed counter with from/to tags.</summary>
    /// <param name="from">Previous state (or null when newly created).</param>
    /// <param name="to">New state.</param>
    private static void EmitActionStateMetric(QualityRiskActionStatus? from, QualityRiskActionStatus to)
    {
        CnasMeter.QualityRiskActionStateChanged.Add(
            1,
            new TagList
            {
                { "from_status", from?.ToString() ?? "(new)" },
                { "to_status", to.ToString() },
            });
    }

    /// <summary>Loads a tracked risk by Sqid.</summary>
    /// <param name="riskSqid">Sqid of the risk.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tracked entity on success.</returns>
    private async Task<Result<QualityRisk>> LoadRiskAsync(string riskSqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(riskSqid);
        if (decoded.IsFailure)
        {
            return Result<QualityRisk>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.QualityRisks
            .FirstOrDefaultAsync(r => r.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<QualityRisk>.Failure(ErrorCodes.NotFound, "Risk not found.")
            : Result<QualityRisk>.Success(row);
    }

    /// <summary>Loads a tracked action by Sqid.</summary>
    /// <param name="actionSqid">Sqid of the action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tracked entity on success.</returns>
    private async Task<Result<QualityRiskPreventiveAction>> LoadActionAsync(string actionSqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(actionSqid);
        if (decoded.IsFailure)
        {
            return Result<QualityRiskPreventiveAction>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.QualityRiskPreventiveActions
            .FirstOrDefaultAsync(a => a.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<QualityRiskPreventiveAction>.Failure(ErrorCodes.NotFound, "Preventive action not found.")
            : Result<QualityRiskPreventiveAction>.Success(row);
    }

    /// <summary>Writes a single audit row scoped to a <see cref="QualityRisk"/>.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row.</param>
    /// <param name="details">Arbitrary anonymous object serialised to JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when the audit row is enqueued.</returns>
    private async Task EmitRiskAuditAsync(
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
            nameof(QualityRisk),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a single audit row scoped to a <see cref="QualityRiskPreventiveAction"/>.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row.</param>
    /// <param name="details">Arbitrary anonymous object serialised to JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when the audit row is enqueued.</returns>
    private async Task EmitActionAuditAsync(
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
            nameof(QualityRiskPreventiveAction),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects a risk entity into its outbound DTO.</summary>
    /// <param name="r">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private QualityRiskDto ToDto(QualityRisk r) => new(
        Id: _sqids.Encode(r.Id),
        RiskCode: r.RiskCode,
        Title: r.Title,
        Description: r.Description,
        Category: r.Category.ToString(),
        Likelihood: r.Likelihood.ToString(),
        Impact: r.Impact.ToString(),
        Status: r.Status.ToString(),
        OwnerSqid: _sqids.Encode(r.OwnerUserId),
        IdentifiedAt: r.IdentifiedAt,
        LastReviewedAt: r.LastReviewedAt,
        ClosedAt: r.ClosedAt,
        ClosureReason: r.ClosureReason);

    /// <summary>Projects an action entity into its outbound DTO.</summary>
    /// <param name="a">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private QualityRiskActionDto ToActionDto(QualityRiskPreventiveAction a) => new(
        Id: _sqids.Encode(a.Id),
        RiskSqid: _sqids.Encode(a.RiskId),
        Description: a.Description,
        Status: a.Status.ToString(),
        DueDate: a.DueDate,
        AssignedToSqid: _sqids.Encode(a.AssignedToUserId),
        CompletedAt: a.CompletedAt,
        CompletionNote: a.CompletionNote);
}
