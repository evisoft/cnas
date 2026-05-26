using System.Linq;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Application.WorkflowAcl;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="IWorkflowStepAclService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. Every mutation writes a Critical
/// <c>WORKFLOW.STEP_ACL.{CREATED|UPDATED|DELETED}</c> audit row and triggers a
/// synchronous refresh of <see cref="WorkflowAclService"/>'s in-memory snapshot so
/// the change is visible to the next ACL check without waiting for the background
/// refresh tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The controller applies the workflow-management policy;
/// here we only guard against the "service called without an authenticated principal"
/// case via <see cref="ICallerContext.UserId"/>.
/// </para>
/// <para>
/// <b>Idempotent upsert.</b> <see cref="UpsertAsync"/> inserts on first call and
/// updates thereafter; the natural-key UNIQUE on (WorkflowDefinitionId, StepCode)
/// prevents duplicates. The audit row captures CREATED vs UPDATED so investigators
/// can tell which path ran.
/// </para>
/// </remarks>
public sealed class WorkflowStepAclService(
    ICnasDbContext db,
    ICallerContext caller,
    ISqidService sqids,
    ICnasTimeProvider clock,
    IAuditService audit,
    WorkflowAclService resolver,
    IValidator<WorkflowStepAclUpsertInput> upsertValidator)
    : IWorkflowStepAclService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICallerContext _caller = caller;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService _audit = audit;
    private readonly WorkflowAclService _resolver = resolver;
    private readonly IValidator<WorkflowStepAclUpsertInput> _upsertValidator = upsertValidator;

    /// <summary>Stable audit-event prefix.</summary>
    private const string AuditPrefix = "WORKFLOW.STEP_ACL";

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<WorkflowStepAclDto>>> ListAsync(
        string workflowSqid, CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<IReadOnlyList<WorkflowStepAclDto>>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(workflowSqid);
        if (decoded.IsFailure)
        {
            return Result<IReadOnlyList<WorkflowStepAclDto>>.Failure(
                decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var rows = await _db.WorkflowStepAcls
            .Where(s => s.WorkflowDefinitionId == decoded.Value && s.IsActive)
            .OrderBy(s => s.StepCode)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        IReadOnlyList<WorkflowStepAclDto> items = rows.Select(Project).ToList();
        return Result<IReadOnlyList<WorkflowStepAclDto>>.Success(items);
    }

    /// <inheritdoc />
    public async Task<Result<WorkflowStepAclDto>> UpsertAsync(
        string workflowSqid,
        string stepCode,
        WorkflowStepAclUpsertInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result<WorkflowStepAclDto>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        if (string.IsNullOrWhiteSpace(stepCode))
        {
            return Result<WorkflowStepAclDto>.Failure(
                ErrorCodes.ValidationFailed, "StepCode is required.");
        }
        if (stepCode.Length > 64)
        {
            return Result<WorkflowStepAclDto>.Failure(
                ErrorCodes.ValidationFailed, "StepCode exceeds the 64-character cap.");
        }

        var decoded = _sqids.TryDecode(workflowSqid);
        if (decoded.IsFailure)
        {
            return Result<WorkflowStepAclDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var validation = await _upsertValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<WorkflowStepAclDto>.Failure(
                ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        // Verify the workflow definition exists. A typo on the route surfaces as
        // NotFound rather than a silent reference to a missing row.
        var workflowExists = await _db.WorkflowDefinitions
            .AnyAsync(w => w.Id == decoded.Value, ct)
            .ConfigureAwait(false);
        if (!workflowExists)
        {
            return Result<WorkflowStepAclDto>.Failure(
                ErrorCodes.NotFound, "Workflow definition not found.");
        }

        var now = _clock.UtcNow;
        var existing = await _db.WorkflowStepAcls
            .SingleOrDefaultAsync(
                s => s.WorkflowDefinitionId == decoded.Value && s.StepCode == stepCode, ct)
            .ConfigureAwait(false);

        WorkflowStepAcl row;
        bool created;
        if (existing is null)
        {
            row = new WorkflowStepAcl
            {
                WorkflowDefinitionId = decoded.Value,
                StepCode = stepCode,
                RequiredRoles = input.RequiredRoles?.ToList() ?? new List<string>(),
                RequiredGroups = input.RequiredGroups?.ToList() ?? new List<string>(),
                RequiredPermission = string.IsNullOrWhiteSpace(input.RequiredPermission)
                    ? null
                    : input.RequiredPermission,
                Description = input.Description,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.WorkflowStepAcls.Add(row);
            created = true;
        }
        else
        {
            row = existing;
            row.RequiredRoles = input.RequiredRoles?.ToList() ?? new List<string>();
            row.RequiredGroups = input.RequiredGroups?.ToList() ?? new List<string>();
            row.RequiredPermission = string.IsNullOrWhiteSpace(input.RequiredPermission)
                ? null
                : input.RequiredPermission;
            row.Description = input.Description;
            row.IsActive = true;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
            created = false;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var eventName = created ? $"{AuditPrefix}.CREATED" : $"{AuditPrefix}.UPDATED";
        await EmitAuditAsync(eventName, row, ct).ConfigureAwait(false);
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result<WorkflowStepAclDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(string workflowSqid, string stepCode, CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(workflowSqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.WorkflowStepAcls
            .SingleOrDefaultAsync(
                s => s.WorkflowDefinitionId == decoded.Value && s.StepCode == stepCode && s.IsActive,
                ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Workflow step ACL not found.");
        }

        row.IsActive = false;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.DELETED", row, ct).ConfigureAwait(false);
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Projects the entity into its output DTO with Sqid-encoded identifiers.
    /// Centralised so the projection rule is applied identically across every read
    /// path.
    /// </summary>
    /// <param name="row">Loaded entity row.</param>
    /// <returns>The DTO the API surface returns.</returns>
    private WorkflowStepAclDto Project(WorkflowStepAcl row) => new(
        Id: _sqids.Encode(row.Id),
        WorkflowDefinitionId: _sqids.Encode(row.WorkflowDefinitionId),
        StepCode: row.StepCode,
        RequiredRoles: row.RequiredRoles?.ToList() ?? new List<string>(),
        RequiredGroups: row.RequiredGroups?.ToList() ?? new List<string>(),
        RequiredPermission: row.RequiredPermission,
        Description: row.Description);

    /// <summary>
    /// Emits a Critical-severity audit row for a step-ACL mutation. The details JSON
    /// captures the natural key (workflowDefinitionId, stepCode) + the role/group/
    /// permission requirement shape so investigators can reconstruct the change
    /// without echoing arbitrary operator-supplied description text into the audit
    /// stream.
    /// </summary>
    /// <param name="eventCode">Stable audit event code (e.g. <c>WORKFLOW.STEP_ACL.UPDATED</c>).</param>
    /// <param name="row">The persisted (or just-modified) row.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitAuditAsync(string eventCode, WorkflowStepAcl row, CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            workflowDefinitionId = _sqids.Encode(row.WorkflowDefinitionId),
            stepCode = row.StepCode,
            requiredRoleCount = row.RequiredRoles?.Count ?? 0,
            requiredGroupCount = row.RequiredGroups?.Count ?? 0,
            hasRequiredPermission = !string.IsNullOrEmpty(row.RequiredPermission),
        });

        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Critical,
            actorId: actor,
            targetEntity: nameof(WorkflowStepAcl),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
