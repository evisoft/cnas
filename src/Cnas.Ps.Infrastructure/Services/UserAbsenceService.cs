using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Application.WorkflowTasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0127 / CF 16.11 — concrete <see cref="IUserAbsenceService"/>. Persists user-absence
/// rows, orchestrates the activation / completion sweep against
/// <see cref="ITaskInboxService"/>, and emits audit records at each lifecycle
/// transition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Activation strategy.</b> Activation routes each of the absent user's open tasks
/// through <see cref="ITaskInboxService.ReassignAsync"/> which carries the audit
/// emission, notification dispatch, and original-assignee anchoring already wired
/// there. The service itself only flips the row's lifecycle status, stamps
/// <c>ActivatedAtUtc</c>, and increments the routed-task counter — the per-task
/// machinery stays in one place.
/// </para>
/// <para>
/// <b>Completion strategy.</b> Completion reverts every still-open task whose
/// <c>DelegatedFromAbsenceId</c> matches this row back to its original assignee via
/// <see cref="ITaskInboxService.RevertReassignmentAsync"/>. Tasks the delegate already
/// touched (different assignee, completed, or cancelled) are skipped — the absence
/// does not "steal" them back.
/// </para>
/// </remarks>
public sealed class UserAbsenceService(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ISqidService sqids,
    ICallerContext caller,
    ITaskInboxService tasks,
    IAuditService? audit = null) : IUserAbsenceService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly ICallerContext _caller = caller;
    private readonly ITaskInboxService _tasks = tasks;
    private readonly IAuditService? _audit = audit;

    /// <inheritdoc />
    public async Task<Result<UserAbsenceOutputDto>> PlanAsync(
        UserAbsenceCreateDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // ─── Body validation (FluentValidation). ───
        var validator = new UserAbsenceCreateDtoValidator(_clock);
        var validation = await validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<UserAbsenceOutputDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        // ─── Decode the user + delegate Sqids. ───
        var userDecoded = _sqids.TryDecode(input.UserSqid);
        if (userDecoded.IsFailure)
        {
            return Result<UserAbsenceOutputDto>.Failure(userDecoded.ErrorCode!, userDecoded.ErrorMessage!);
        }
        var delegateDecoded = _sqids.TryDecode(input.DelegateSqid);
        if (delegateDecoded.IsFailure)
        {
            return Result<UserAbsenceOutputDto>.Failure(delegateDecoded.ErrorCode!, delegateDecoded.ErrorMessage!);
        }
        var userId = userDecoded.Value;
        var delegateId = delegateDecoded.Value;

        // ─── Verify both users exist and are active. ───
        var bothExist = await _db.UserProfiles
            .CountAsync(
                u => u.IsActive && (u.Id == userId || u.Id == delegateId),
                cancellationToken).ConfigureAwait(false);
        if (bothExist < 2)
        {
            return Result<UserAbsenceOutputDto>.Failure(
                ErrorCodes.NotFound,
                "User or delegate not found.");
        }

        // ─── Overlap check: no Planned or Active row for the same user. ───
        // Two intervals overlap iff (a.Start ≤ b.End) AND (b.Start ≤ a.End).
        var overlap = await _db.UserAbsences
            .Where(a => a.IsActive
                && a.UserUserId == userId
                && (a.Status == UserAbsenceStatus.Planned || a.Status == UserAbsenceStatus.Active)
                && a.StartDateUtc <= input.EndDateUtc
                && input.StartDateUtc <= a.EndDateUtc)
            .AnyAsync(cancellationToken).ConfigureAwait(false);
        if (overlap)
        {
            return Result<UserAbsenceOutputDto>.Failure(
                ErrorCodes.ValidationFailed,
                "An overlapping Planned or Active absence already exists for this user.");
        }

        // ─── Persist. ───
        var now = _clock.UtcNow;
        var row = new UserAbsence
        {
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
            UserUserId = userId,
            DelegateUserId = delegateId,
            StartDateUtc = input.StartDateUtc,
            EndDateUtc = input.EndDateUtc,
            Reason = input.Reason,
            Status = UserAbsenceStatus.Planned,
            RoutedTaskCount = 0,
        };
        _db.UserAbsences.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // ─── Audit (Notice). ───
        await AuditAsync(
            "USER_ABSENCE.PLANNED",
            row.Id,
            new
            {
                userSqid = input.UserSqid,
                delegateSqid = input.DelegateSqid,
                startDateUtc = row.StartDateUtc,
                endDateUtc = row.EndDateUtc,
                reason = row.Reason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<UserAbsenceOutputDto>.Success(ToOutputDto(row));
    }

    /// <inheritdoc />
    public async Task<Result> ActivateAsync(long absenceId, CancellationToken cancellationToken = default)
    {
        var row = await _db.UserAbsences
            .SingleOrDefaultAsync(a => a.Id == absenceId && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Absence not found.");
        }
        if (row.Status == UserAbsenceStatus.Active)
        {
            // Idempotent — re-activating an already-active row is a no-op success.
            return Result.Success();
        }
        if (row.Status != UserAbsenceStatus.Planned)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                $"Absence is in {row.Status} status and cannot be activated.");
        }

        // ─── Collect the absent user's open tasks. ───
        var openTasks = await _db.WorkflowTasks
            .Where(t => t.IsActive
                && t.AssignedUserId == row.UserUserId
                && (t.Status == WorkflowTaskStatus.Pending
                    || t.Status == WorkflowTaskStatus.InProgress
                    || t.Status == WorkflowTaskStatus.Overdue))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // ─── Route each task to the delegate. ───
        // ReassignAsync handles audit + notification + counter increment per task.
        var reasonForRoute = $"Absence delegation: {row.Reason}";
        var routed = 0;
        foreach (var taskId in openTasks)
        {
            var outcome = await _tasks.ReassignAsync(
                taskId,
                row.DelegateUserId,
                reasonForRoute,
                absenceId: row.Id,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (outcome.IsSuccess)
            {
                routed += 1;
            }
        }

        // ─── Flip the row lifecycle. ───
        row.Status = UserAbsenceStatus.Active;
        row.ActivatedAtUtc = _clock.UtcNow;
        row.RoutedTaskCount = routed;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditAsync(
            "USER_ABSENCE.ACTIVATED",
            row.Id,
            new { routedTaskCount = routed },
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> CompleteAsync(long absenceId, CancellationToken cancellationToken = default)
    {
        var row = await _db.UserAbsences
            .SingleOrDefaultAsync(a => a.Id == absenceId && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Absence not found.");
        }
        if (row.Status != UserAbsenceStatus.Active)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                $"Absence is in {row.Status} status and cannot be completed.");
        }

        // ─── Revert every still-open task that was routed via this absence. ───
        var taskIds = await _db.WorkflowTasks
            .Where(t => t.IsActive
                && t.DelegatedFromAbsenceId == row.Id
                && (t.Status == WorkflowTaskStatus.Pending
                    || t.Status == WorkflowTaskStatus.InProgress
                    || t.Status == WorkflowTaskStatus.Overdue))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var reverted = 0;
        foreach (var taskId in taskIds)
        {
            var outcome = await _tasks.RevertReassignmentAsync(taskId, cancellationToken)
                .ConfigureAwait(false);
            if (outcome.IsSuccess)
            {
                reverted += 1;
            }
        }

        // ─── Flip the row lifecycle. ───
        row.Status = UserAbsenceStatus.Completed;
        row.CompletedAtUtc = _clock.UtcNow;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditAsync(
            "USER_ABSENCE.COMPLETED",
            row.Id,
            new { revertedTaskCount = reverted },
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> CancelAsync(long absenceId, CancellationToken cancellationToken = default)
    {
        var row = await _db.UserAbsences
            .SingleOrDefaultAsync(a => a.Id == absenceId && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Absence not found.");
        }
        if (row.Status != UserAbsenceStatus.Planned)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                $"Absence is in {row.Status} status and cannot be cancelled. Active rows must be completed instead.");
        }

        row.Status = UserAbsenceStatus.Cancelled;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditAsync(
            "USER_ABSENCE.CANCELLED",
            row.Id,
            payload: null,
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<UserAbsenceOutputDto?> GetAsync(long absenceId, CancellationToken cancellationToken = default)
    {
        var row = await _db.UserAbsences
            .SingleOrDefaultAsync(a => a.Id == absenceId && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToOutputDto(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserAbsenceOutputDto>> ListForUserAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.UserAbsences
            .Where(a => a.IsActive && a.UserUserId == userId)
            .OrderByDescending(a => a.StartDateUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToOutputDto).ToList();
    }

    /// <summary>
    /// Maps a <see cref="UserAbsence"/> entity to its external DTO shape. Encodes every
    /// id-shaped column through <see cref="ISqidService.Encode"/> per CLAUDE.md RULE 3.
    /// </summary>
    /// <param name="row">Entity to project.</param>
    /// <returns>The DTO snapshot.</returns>
    internal UserAbsenceOutputDto ToOutputDto(UserAbsence row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new UserAbsenceOutputDto(
            Id: _sqids.Encode(row.Id),
            UserSqid: _sqids.Encode(row.UserUserId),
            DelegateSqid: _sqids.Encode(row.DelegateUserId),
            StartDateUtc: row.StartDateUtc,
            EndDateUtc: row.EndDateUtc,
            Status: row.Status.ToString(),
            ActivatedAtUtc: row.ActivatedAtUtc,
            CompletedAtUtc: row.CompletedAtUtc,
            RoutedTaskCount: row.RoutedTaskCount,
            Reason: row.Reason);
    }

    /// <summary>
    /// Writes a Notice-severity audit row when an <see cref="IAuditService"/> was
    /// injected; no-op when running in a unit-test harness that elected to skip audit
    /// wiring.
    /// </summary>
    /// <param name="eventCode">Stable event code per <c>AuditEventCodes</c> conventions.</param>
    /// <param name="absenceId">Target row id.</param>
    /// <param name="payload">Optional structured JSON-shaped detail payload.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task AuditAsync(string eventCode, long absenceId, object? payload, CancellationToken ct)
    {
        if (_audit is null)
        {
            return;
        }
        var json = JsonSerializer.Serialize(payload ?? new { });
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Notice,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(UserAbsence),
            targetEntityId: absenceId,
            detailsJson: json,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
