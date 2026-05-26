using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — production implementation of
/// <see cref="IBackupPolicyService"/>. CRUD over the
/// <c>BackupPolicy</c> registry, with audit + metric emission for every
/// state transition.
/// </summary>
public sealed class BackupPolicyService : IBackupPolicyService
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
    private readonly IValidator<BackupPolicyCreateInputDto> _createValidator;
    private readonly IValidator<BackupPolicyModifyInputDto> _modifyValidator;
    private readonly IValidator<BackupPolicyReasonInputDto> _reasonValidator;
    private readonly IValidator<BackupPolicyFilterDto> _filterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="createValidator">Validator for create input.</param>
    /// <param name="modifyValidator">Validator for modify input.</param>
    /// <param name="reasonValidator">Validator for reason input.</param>
    /// <param name="filterValidator">Validator for list-filter input.</param>
    public BackupPolicyService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<BackupPolicyCreateInputDto> createValidator,
        IValidator<BackupPolicyModifyInputDto> modifyValidator,
        IValidator<BackupPolicyReasonInputDto> reasonValidator,
        IValidator<BackupPolicyFilterDto> filterValidator)
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
    public async Task<Result<BackupPolicyDto>> CreateAsync(
        BackupPolicyCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<BackupPolicyDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var duplicate = await _db.BackupPolicies
            .AnyAsync(p => p.PolicyCode == input.PolicyCode, cancellationToken)
            .ConfigureAwait(false);
        if (duplicate)
        {
            return Result<BackupPolicyDto>.Failure(
                IBackupPolicyService.DuplicatePolicyCodeCode,
                $"A backup policy with PolicyCode '{input.PolicyCode}' already exists.");
        }

        var scope = Enum.Parse<BackupScope>(input.Scope, ignoreCase: false);
        var strategy = Enum.Parse<BackupStrategy>(input.Strategy, ignoreCase: false);
        var targetKind = Enum.Parse<BackupTargetKind>(input.TargetKind, ignoreCase: false);
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";

        var policy = new BackupPolicy
        {
            PolicyCode = input.PolicyCode,
            DisplayName = input.DisplayName,
            Description = input.Description,
            Scope = scope,
            Strategy = strategy,
            CronSchedule = input.CronSchedule,
            RetentionDays = input.RetentionDays,
            TargetKind = targetKind,
            TargetReference = input.TargetReference,
            RegisteredByUserId = _caller.UserId ?? 0,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.BackupPolicies.Add(policy);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.BackupPolicyCreated.Add(1);

        await EmitAuditAsync(
            IBackupPolicyService.AuditPolicyCreated,
            AuditSeverity.Critical,
            actor,
            policy.Id,
            new
            {
                policySqid = _sqids.Encode(policy.Id),
                policy.PolicyCode,
                scope = policy.Scope.ToString(),
                strategy = policy.Strategy.ToString(),
                targetKind = policy.TargetKind.ToString(),
                policy.RetentionDays,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<BackupPolicyDto>.Success(ToDto(policy));
    }

    /// <inheritdoc />
    public async Task<Result<BackupPolicyDto>> ModifyAsync(
        string policySqid,
        BackupPolicyModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _modifyValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<BackupPolicyDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var loaded = await LoadAsync(policySqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<BackupPolicyDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var policy = loaded.Value;
        if (policy.IsArchived)
        {
            return Result<BackupPolicyDto>.Failure(
                IBackupPolicyService.InvalidTransitionCode,
                "Archived policies cannot be modified.");
        }

        if (input.DisplayName is not null) policy.DisplayName = input.DisplayName;
        if (input.Description is not null) policy.Description = input.Description;
        if (input.CronSchedule is not null) policy.CronSchedule = input.CronSchedule;
        if (input.RetentionDays is not null) policy.RetentionDays = input.RetentionDays.Value;
        if (input.TargetReference is not null) policy.TargetReference = input.TargetReference;

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        policy.UpdatedAtUtc = now;
        policy.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            IBackupPolicyService.AuditPolicyModified,
            AuditSeverity.Sensitive,
            actor,
            policy.Id,
            new
            {
                policySqid = _sqids.Encode(policy.Id),
                policy.PolicyCode,
                changeReason = input.ChangeReason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<BackupPolicyDto>.Success(ToDto(policy));
    }

    /// <inheritdoc />
    public async Task<Result<BackupPolicyDto>> ActivateAsync(
        string policySqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(policySqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<BackupPolicyDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var policy = loaded.Value;
        if (policy.IsArchived)
        {
            return Result<BackupPolicyDto>.Failure(
                IBackupPolicyService.InvalidTransitionCode,
                "Archived policies cannot be activated.");
        }
        if (policy.IsActive)
        {
            return Result<BackupPolicyDto>.Failure(
                IBackupPolicyService.InvalidTransitionCode,
                "Policy is already Active.");
        }
        return await ApplyTransitionAsync(policy, newIsActive: true, transitionLabel: "Activate", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<BackupPolicyDto>> DeactivateAsync(
        string policySqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(policySqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<BackupPolicyDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var policy = loaded.Value;
        if (policy.IsArchived)
        {
            return Result<BackupPolicyDto>.Failure(
                IBackupPolicyService.InvalidTransitionCode,
                "Archived policies cannot be deactivated (they are already inactive).");
        }
        if (!policy.IsActive)
        {
            return Result<BackupPolicyDto>.Failure(
                IBackupPolicyService.InvalidTransitionCode,
                "Policy is already Inactive.");
        }
        return await ApplyTransitionAsync(policy, newIsActive: false, transitionLabel: "Deactivate", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<BackupPolicyDto>> ArchiveAsync(
        string policySqid,
        BackupPolicyReasonInputDto reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var rv = await _reasonValidator.ValidateAsync(reason, cancellationToken).ConfigureAwait(false);
        if (!rv.IsValid)
        {
            return Result<BackupPolicyDto>.Failure(ErrorCodes.ValidationFailed, rv.Errors[0].ErrorMessage);
        }

        var loaded = await LoadAsync(policySqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<BackupPolicyDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var policy = loaded.Value;
        if (policy.IsArchived)
        {
            return Result<BackupPolicyDto>.Failure(
                IBackupPolicyService.InvalidTransitionCode,
                "Policy is already Archived.");
        }
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        policy.IsArchived = true;
        policy.IsActive = false;
        policy.UpdatedAtUtc = now;
        policy.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            IBackupPolicyService.AuditPolicyTransitioned,
            AuditSeverity.Critical,
            actor,
            policy.Id,
            new
            {
                policySqid = _sqids.Encode(policy.Id),
                policy.PolicyCode,
                transition = "Archive",
                reason = reason.Reason,
                atUtc = now.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<BackupPolicyDto>.Success(ToDto(policy));
    }

    /// <inheritdoc />
    public async Task<Result<BackupPolicyDto>> GetByIdAsync(
        string policySqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(policySqid);
        if (decoded.IsFailure)
        {
            return Result<BackupPolicyDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _read.BackupPolicies
            .FirstOrDefaultAsync(p => p.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<BackupPolicyDto>.Failure(ErrorCodes.NotFound, "Backup policy not found.")
            : Result<BackupPolicyDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<BackupPolicyDto>> GetByCodeAsync(
        string policyCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(policyCode))
        {
            return Result<BackupPolicyDto>.Failure(ErrorCodes.ValidationFailed, "PolicyCode is required.");
        }
        var row = await _read.BackupPolicies
            .FirstOrDefaultAsync(p => p.PolicyCode == policyCode, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<BackupPolicyDto>.Failure(ErrorCodes.NotFound, "Backup policy not found.")
            : Result<BackupPolicyDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<BackupPolicyPageDto>> ListAsync(
        BackupPolicyFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<BackupPolicyPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<BackupPolicy> q = _read.BackupPolicies;
        if (filter.IsActive is not null)
        {
            var wantActive = filter.IsActive.Value;
            q = q.Where(p => p.IsActive == wantActive);
        }
        if (!string.IsNullOrWhiteSpace(filter.Scope)
            && Enum.TryParse<BackupScope>(filter.Scope, ignoreCase: false, out var scope))
        {
            q = q.Where(p => p.Scope == scope);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(p => p.CreatedAtUtc)
            .ThenByDescending(p => p.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new BackupPolicyPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<BackupPolicyPageDto>.Success(page);
    }

    /// <summary>
    /// Loads a policy by Sqid, returning a friendly failure when the input
    /// is malformed or the row is missing.
    /// </summary>
    /// <param name="policySqid">Sqid-encoded policy id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded entity on success.</returns>
    private async Task<Result<BackupPolicy>> LoadAsync(string policySqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(policySqid);
        if (decoded.IsFailure)
        {
            return Result<BackupPolicy>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.BackupPolicies
            .FirstOrDefaultAsync(p => p.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<BackupPolicy>.Failure(ErrorCodes.NotFound, "Backup policy not found.")
            : Result<BackupPolicy>.Success(row);
    }

    /// <summary>
    /// Helper that flips the policy's <c>IsActive</c> flag + emits the
    /// transition audit row.
    /// </summary>
    /// <param name="policy">Loaded policy.</param>
    /// <param name="newIsActive">Target active flag.</param>
    /// <param name="transitionLabel">Audit label describing the transition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success.</returns>
    private async Task<Result<BackupPolicyDto>> ApplyTransitionAsync(
        BackupPolicy policy,
        bool newIsActive,
        string transitionLabel,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        policy.IsActive = newIsActive;
        policy.UpdatedAtUtc = now;
        policy.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            IBackupPolicyService.AuditPolicyTransitioned,
            AuditSeverity.Sensitive,
            actor,
            policy.Id,
            new
            {
                policySqid = _sqids.Encode(policy.Id),
                policy.PolicyCode,
                transition = transitionLabel,
                isActive = policy.IsActive,
                atUtc = now.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<BackupPolicyDto>.Success(ToDto(policy));
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
            nameof(BackupPolicy),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an entity into its outbound DTO.</summary>
    /// <param name="p">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private BackupPolicyDto ToDto(BackupPolicy p) => new(
        Id: _sqids.Encode(p.Id),
        PolicyCode: p.PolicyCode,
        DisplayName: p.DisplayName,
        Description: p.Description,
        Scope: p.Scope.ToString(),
        Strategy: p.Strategy.ToString(),
        CronSchedule: p.CronSchedule,
        RetentionDays: p.RetentionDays,
        TargetKind: p.TargetKind.ToString(),
        TargetReference: p.TargetReference,
        IsActive: p.IsActive,
        LastSuccessfulRunAt: p.LastSuccessfulRunAt,
        LastFailedRunAt: p.LastFailedRunAt);
}
