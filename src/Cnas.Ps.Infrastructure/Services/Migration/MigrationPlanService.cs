using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Migration;

/// <summary>
/// R2430 / TOR M4 — production implementation of
/// <see cref="IMigrationPlanService"/>. Hosts the plan-registry CRUD + the
/// lifecycle state machine.
/// </summary>
public sealed class MigrationPlanService : IMigrationPlanService
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
    private readonly IValidator<MigrationPlanCreateInputDto> _createValidator;
    private readonly IValidator<MigrationPlanModifyInputDto> _modifyValidator;
    private readonly IValidator<MigrationPlanReasonInputDto> _reasonValidator;
    private readonly IValidator<MigrationPlanFilterDto> _filterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="createValidator">Validator for plan-create input.</param>
    /// <param name="modifyValidator">Validator for plan-modify input.</param>
    /// <param name="reasonValidator">Validator for transition-reason input.</param>
    /// <param name="filterValidator">Validator for list-filter input.</param>
    public MigrationPlanService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<MigrationPlanCreateInputDto> createValidator,
        IValidator<MigrationPlanModifyInputDto> modifyValidator,
        IValidator<MigrationPlanReasonInputDto> reasonValidator,
        IValidator<MigrationPlanFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _createValidator = createValidator;
        _modifyValidator = modifyValidator;
        _reasonValidator = reasonValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<MigrationPlanDto>> CreateAsync(
        MigrationPlanCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<MigrationPlanDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var duplicate = await _db.MigrationPlans
            .AnyAsync(p => p.PlanCode == input.PlanCode, cancellationToken)
            .ConfigureAwait(false);
        if (duplicate)
        {
            return Result<MigrationPlanDto>.Failure(
                IMigrationPlanService.DuplicatePlanCodeCode,
                $"A migration plan with PlanCode '{input.PlanCode}' already exists.");
        }

        var sourceKind = Enum.Parse<MigrationSourceKind>(input.SourceKind, ignoreCase: false);
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var plan = new MigrationPlan
        {
            PlanCode = input.PlanCode,
            Title = input.Title,
            Description = input.Description,
            SourceKind = sourceKind,
            TargetEntityName = input.TargetEntityName,
            MappingDescriptorJson = input.MappingDescriptorJson,
            BatchSize = input.BatchSize,
            Status = MigrationPlanStatus.Draft,
            RegisteredByUserId = _caller.UserId ?? 0,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.MigrationPlans.Add(plan);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.MigrationPlanCreated.Add(1);

        await EmitAuditAsync(
            IMigrationPlanService.AuditPlanCreated,
            AuditSeverity.Critical,
            actor,
            plan.Id,
            new
            {
                planSqid = _sqids.Encode(plan.Id),
                plan.PlanCode,
                plan.TargetEntityName,
                sourceKind = plan.SourceKind.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<MigrationPlanDto>.Success(ToDto(plan));
    }

    /// <inheritdoc />
    public async Task<Result<MigrationPlanDto>> ModifyAsync(
        string planSqid,
        MigrationPlanModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _modifyValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<MigrationPlanDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var loaded = await LoadAsync(planSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<MigrationPlanDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var plan = loaded.Value;
        if (plan.Status != MigrationPlanStatus.Draft)
        {
            return Result<MigrationPlanDto>.Failure(
                IMigrationPlanService.InvalidTransitionCode,
                "Only Draft plans can be modified.");
        }

        plan.Title = input.Title;
        plan.Description = input.Description;
        plan.MappingDescriptorJson = input.MappingDescriptorJson;
        plan.BatchSize = input.BatchSize;
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        plan.UpdatedAtUtc = now;
        plan.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            IMigrationPlanService.AuditPlanModified,
            AuditSeverity.Sensitive,
            actor,
            plan.Id,
            new { planSqid = _sqids.Encode(plan.Id), plan.PlanCode },
            cancellationToken).ConfigureAwait(false);

        return Result<MigrationPlanDto>.Success(ToDto(plan));
    }

    /// <inheritdoc />
    public Task<Result<MigrationPlanDto>> SubmitForApprovalAsync(
        string planSqid,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            planSqid,
            from: MigrationPlanStatus.Draft,
            to: MigrationPlanStatus.Draft,
            transitionLabel: "SubmitForApproval",
            extraMutation: null,
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    public Task<Result<MigrationPlanDto>> ApproveAsync(
        string planSqid,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            planSqid,
            from: MigrationPlanStatus.Draft,
            to: MigrationPlanStatus.Approved,
            transitionLabel: "Approve",
            extraMutation: (plan, now) =>
            {
                plan.ApprovedByUserId = _caller.UserId ?? 0;
                plan.ApprovedAt = now;
            },
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    public async Task<Result<MigrationPlanDto>> ActivateAsync(
        string planSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(planSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<MigrationPlanDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var plan = loaded.Value;
        if (plan.Status is not (MigrationPlanStatus.Approved or MigrationPlanStatus.Suspended))
        {
            return Result<MigrationPlanDto>.Failure(
                IMigrationPlanService.InvalidTransitionCode,
                "Only Approved or Suspended plans can be activated.");
        }
        return await ApplyTransitionAsync(plan, MigrationPlanStatus.Active, "Activate", extra: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<MigrationPlanDto>> SuspendAsync(
        string planSqid,
        MigrationPlanReasonInputDto reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var rv = await _reasonValidator.ValidateAsync(reason, cancellationToken).ConfigureAwait(false);
        if (!rv.IsValid)
        {
            return Result<MigrationPlanDto>.Failure(ErrorCodes.ValidationFailed, rv.Errors[0].ErrorMessage);
        }

        return await TransitionAsync(
            planSqid,
            from: MigrationPlanStatus.Active,
            to: MigrationPlanStatus.Suspended,
            transitionLabel: "Suspend",
            extraMutation: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<MigrationPlanDto>> ArchiveAsync(
        string planSqid,
        MigrationPlanReasonInputDto reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var rv = await _reasonValidator.ValidateAsync(reason, cancellationToken).ConfigureAwait(false);
        if (!rv.IsValid)
        {
            return Result<MigrationPlanDto>.Failure(ErrorCodes.ValidationFailed, rv.Errors[0].ErrorMessage);
        }

        var loaded = await LoadAsync(planSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<MigrationPlanDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var plan = loaded.Value;
        if (plan.Status == MigrationPlanStatus.Archived)
        {
            return Result<MigrationPlanDto>.Failure(
                IMigrationPlanService.InvalidTransitionCode,
                "Plan is already Archived.");
        }
        return await ApplyTransitionAsync(plan, MigrationPlanStatus.Archived, "Archive", extra: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<MigrationPlanDto>> GetByIdAsync(
        string planSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(planSqid);
        if (decoded.IsFailure)
        {
            return Result<MigrationPlanDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _read.MigrationPlans
            .FirstOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<MigrationPlanDto>.Failure(ErrorCodes.NotFound, "Migration plan not found.")
            : Result<MigrationPlanDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<MigrationPlanDto>> GetByCodeAsync(
        string planCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(planCode))
        {
            return Result<MigrationPlanDto>.Failure(ErrorCodes.ValidationFailed, "PlanCode is required.");
        }
        var row = await _read.MigrationPlans
            .FirstOrDefaultAsync(p => p.PlanCode == planCode && p.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<MigrationPlanDto>.Failure(ErrorCodes.NotFound, "Migration plan not found.")
            : Result<MigrationPlanDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<MigrationPlanPageDto>> ListAsync(
        MigrationPlanFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<MigrationPlanPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<MigrationPlan> q = _read.MigrationPlans.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<MigrationPlanStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(p => p.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.TargetEntityName))
        {
            q = q.Where(p => p.TargetEntityName == filter.TargetEntityName);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(p => p.CreatedAtUtc)
            .ThenByDescending(p => p.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new MigrationPlanPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<MigrationPlanPageDto>.Success(page);
    }

    /// <summary>
    /// Loads a plan by Sqid, returning a friendly failure when the input is
    /// malformed or the row is missing / soft-deleted.
    /// </summary>
    /// <param name="planSqid">Sqid-encoded plan id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded plan on success.</returns>
    private async Task<Result<MigrationPlan>> LoadAsync(string planSqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(planSqid);
        if (decoded.IsFailure)
        {
            return Result<MigrationPlan>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.MigrationPlans
            .FirstOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<MigrationPlan>.Failure(ErrorCodes.NotFound, "Migration plan not found.")
            : Result<MigrationPlan>.Success(row);
    }

    /// <summary>
    /// Helper that loads the plan, asserts the current status equals
    /// <paramref name="from"/>, flips to <paramref name="to"/>, and audits.
    /// </summary>
    /// <param name="planSqid">Sqid-encoded plan id.</param>
    /// <param name="from">Required current status.</param>
    /// <param name="to">Target status.</param>
    /// <param name="transitionLabel">Audit label describing the transition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="extraMutation">Optional mutation applied prior to save.</param>
    /// <returns>The updated DTO on success.</returns>
    private async Task<Result<MigrationPlanDto>> TransitionAsync(
        string planSqid,
        MigrationPlanStatus from,
        MigrationPlanStatus to,
        string transitionLabel,
        Action<MigrationPlan, DateTime>? extraMutation,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(planSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<MigrationPlanDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var plan = loaded.Value;
        if (plan.Status != from)
        {
            return Result<MigrationPlanDto>.Failure(
                IMigrationPlanService.InvalidTransitionCode,
                $"Transition '{transitionLabel}' requires status {from}; current status is {plan.Status}.");
        }
        return await ApplyTransitionAsync(plan, to, transitionLabel, extraMutation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists a status transition + writes a Sensitive-severity audit row.
    /// </summary>
    /// <param name="plan">Loaded plan entity.</param>
    /// <param name="to">Target status.</param>
    /// <param name="transitionLabel">Audit label describing the transition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="extra">Optional mutation applied prior to save.</param>
    /// <returns>The updated DTO on success.</returns>
    private async Task<Result<MigrationPlanDto>> ApplyTransitionAsync(
        MigrationPlan plan,
        MigrationPlanStatus to,
        string transitionLabel,
        Action<MigrationPlan, DateTime>? extra,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var previous = plan.Status;
        plan.Status = to;
        plan.UpdatedAtUtc = now;
        plan.UpdatedBy = actor;
        extra?.Invoke(plan, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            IMigrationPlanService.AuditPlanTransitioned,
            AuditSeverity.Sensitive,
            actor,
            plan.Id,
            new
            {
                planSqid = _sqids.Encode(plan.Id),
                plan.PlanCode,
                transition = transitionLabel,
                fromStatus = previous.ToString(),
                toStatus = to.ToString(),
                atUtc = now.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<MigrationPlanDto>.Success(ToDto(plan));
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
            nameof(MigrationPlan),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an entity into its outbound DTO.</summary>
    /// <param name="p">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private MigrationPlanDto ToDto(MigrationPlan p) => new(
        Id: _sqids.Encode(p.Id),
        PlanCode: p.PlanCode,
        Title: p.Title,
        Description: p.Description,
        SourceKind: p.SourceKind.ToString(),
        TargetEntityName: p.TargetEntityName,
        MappingDescriptorJson: p.MappingDescriptorJson,
        BatchSize: p.BatchSize,
        Status: p.Status.ToString(),
        RegisteredByUserSqid: p.RegisteredByUserId == 0 ? "system" : _sqids.Encode(p.RegisteredByUserId),
        ApprovedByUserSqid: p.ApprovedByUserId is null or 0 ? null : _sqids.Encode(p.ApprovedByUserId.Value),
        ApprovedAt: p.ApprovedAt,
        CreatedAtUtc: p.CreatedAtUtc);
}
