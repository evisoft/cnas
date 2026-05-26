using System;
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
/// R2504 / TOR PIR 024 — production implementation of
/// <see cref="ISystemUpdateEventService"/>. Owns the state-machine
/// Planned → Notified → Deploying → Deployed (or Cancelled from any
/// non-terminal state). Enforces the parent schedule's notice lead-time
/// requirement at create time.
/// </summary>
public sealed class SystemUpdateEventService : ISystemUpdateEventService
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
    private readonly IValidator<SystemUpdateEventCreateInputDto> _createValidator;
    private readonly IValidator<SystemUpdateEventReasonInputDto> _reasonValidator;
    private readonly IValidator<SystemUpdateEventFilterDto> _filterValidator;

    /// <summary>Constructs the service.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="createValidator">Validator for create input.</param>
    /// <param name="reasonValidator">Validator for reason input.</param>
    /// <param name="filterValidator">Validator for filter input.</param>
    public SystemUpdateEventService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<SystemUpdateEventCreateInputDto> createValidator,
        IValidator<SystemUpdateEventReasonInputDto> reasonValidator,
        IValidator<SystemUpdateEventFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _createValidator = createValidator;
        _reasonValidator = reasonValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<SystemUpdateEventDto>> CreateAsync(
        SystemUpdateEventCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SystemUpdateEventDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var schedule = await _db.SystemUpdateSchedules
            .FirstOrDefaultAsync(s => s.ScheduleCode == input.ScheduleCode && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (schedule is null)
        {
            return Result<SystemUpdateEventDto>.Failure(
                ErrorCodes.NotFound,
                $"No active system-update schedule with ScheduleCode '{input.ScheduleCode}'.");
        }

        var now = _clock.UtcNow;
        if (input.PlannedDeploymentUtc <= now)
        {
            return Result<SystemUpdateEventDto>.Failure(
                ErrorCodes.ValidationFailed,
                "PlannedDeploymentUtc must be in the future.");
        }

        var requiredLeadDays = schedule.NoticeLeadTimeDays;
        if (requiredLeadDays > 0)
        {
            var earliestAllowed = now.AddDays(requiredLeadDays);
            if (input.PlannedDeploymentUtc < earliestAllowed)
            {
                return Result<SystemUpdateEventDto>.Failure(
                    ErrorCodes.UpdateEventLeadTimeInsufficient,
                    $"Schedule '{schedule.ScheduleCode}' requires at least {requiredLeadDays} days of advance notice.");
            }
        }

        long? maintWindowId = null;
        if (!string.IsNullOrWhiteSpace(input.MaintenanceWindowSqid))
        {
            var decoded = _sqids.TryDecode(input.MaintenanceWindowSqid);
            if (decoded.IsFailure)
            {
                return Result<SystemUpdateEventDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            }
            var win = await _db.MaintenanceWindows
                .FirstOrDefaultAsync(w => w.Id == decoded.Value, cancellationToken)
                .ConfigureAwait(false);
            if (win is null)
            {
                return Result<SystemUpdateEventDto>.Failure(ErrorCodes.NotFound, "Maintenance window not found.");
            }
            maintWindowId = win.Id;
        }

        var eventNumber = await MintEventNumberAsync(now, cancellationToken).ConfigureAwait(false);
        var actor = _caller.UserSqid ?? "admin";

        var entity = new SystemUpdateEvent
        {
            ScheduleId = schedule.Id,
            EventNumber = eventNumber,
            Title = input.Title,
            Description = input.Description,
            PlannedDeploymentUtc = input.PlannedDeploymentUtc,
            Status = SystemUpdateEventStatus.Planned,
            MaintenanceWindowId = maintWindowId,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.SystemUpdateEvents.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.SystemUpdateEventCreated.Add(1, new System.Diagnostics.TagList { { "cadence", schedule.Cadence.ToString() } });

        await EmitAuditAsync(
            ISystemUpdateEventService.AuditCreated,
            AuditSeverity.Critical,
            actor,
            entity.Id,
            new
            {
                eventSqid = _sqids.Encode(entity.Id),
                entity.EventNumber,
                scheduleCode = schedule.ScheduleCode,
                cadence = schedule.Cadence.ToString(),
                entity.PlannedDeploymentUtc,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<SystemUpdateEventDto>.Success(ToDto(entity, schedule));
    }

    /// <inheritdoc />
    public async Task<Result<SystemUpdateEventDto>> NotifyAsync(
        string eventSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(eventSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SystemUpdateEventDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ev = loaded.Value;
        if (ev.Status != SystemUpdateEventStatus.Planned)
        {
            return Result<SystemUpdateEventDto>.Failure(
                ErrorCodes.UpdateEventInvalidTransition,
                $"Cannot dispatch notice for an event in '{ev.Status}' (must be Planned).");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        ev.Status = SystemUpdateEventStatus.Notified;
        ev.NotifiedAt = now;
        ev.UpdatedAtUtc = now;
        ev.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var schedule = await _db.SystemUpdateSchedules
            .FirstOrDefaultAsync(s => s.Id == ev.ScheduleId, cancellationToken)
            .ConfigureAwait(false);
        CnasMeter.SystemUpdateNotificationDispatched.Add(1, new System.Diagnostics.TagList
        {
            { "cadence", schedule?.Cadence.ToString() ?? "Unknown" },
        });

        await EmitAuditAsync(
            ISystemUpdateEventService.AuditNotified,
            AuditSeverity.Information,
            actor,
            ev.Id,
            new
            {
                eventSqid = _sqids.Encode(ev.Id),
                ev.EventNumber,
                notifiedAt = now.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<SystemUpdateEventDto>.Success(ToDto(ev, schedule));
    }

    /// <inheritdoc />
    public Task<Result<SystemUpdateEventDto>> StartDeploymentAsync(
        string eventSqid,
        CancellationToken cancellationToken = default)
        => SimpleTransitionAsync(
            eventSqid,
            requiredFrom: SystemUpdateEventStatus.Notified,
            target: SystemUpdateEventStatus.Deploying,
            stampStartedAt: true,
            stampCompletedAt: false,
            auditCode: ISystemUpdateEventService.AuditDeploymentStarted,
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<SystemUpdateEventDto>> CompleteDeploymentAsync(
        string eventSqid,
        CancellationToken cancellationToken = default)
        => SimpleTransitionAsync(
            eventSqid,
            requiredFrom: SystemUpdateEventStatus.Deploying,
            target: SystemUpdateEventStatus.Deployed,
            stampStartedAt: false,
            stampCompletedAt: true,
            auditCode: ISystemUpdateEventService.AuditDeploymentCompleted,
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<SystemUpdateEventDto>> CancelAsync(
        string eventSqid,
        SystemUpdateEventReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var rv = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!rv.IsValid)
        {
            return Result<SystemUpdateEventDto>.Failure(ErrorCodes.ValidationFailed, rv.Errors[0].ErrorMessage);
        }
        var loaded = await LoadAsync(eventSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SystemUpdateEventDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ev = loaded.Value;
        if (ev.Status is SystemUpdateEventStatus.Deployed or SystemUpdateEventStatus.Cancelled)
        {
            return Result<SystemUpdateEventDto>.Failure(
                ErrorCodes.UpdateEventInvalidTransition,
                $"Cannot cancel an event in terminal state '{ev.Status}'.");
        }
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        ev.Status = SystemUpdateEventStatus.Cancelled;
        ev.CancelledAt = now;
        ev.CancelReason = input.Reason;
        ev.UpdatedAtUtc = now;
        ev.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var schedule = await _db.SystemUpdateSchedules
            .FirstOrDefaultAsync(s => s.Id == ev.ScheduleId, cancellationToken)
            .ConfigureAwait(false);

        await EmitAuditAsync(
            ISystemUpdateEventService.AuditCancelled,
            AuditSeverity.Critical,
            actor,
            ev.Id,
            new
            {
                eventSqid = _sqids.Encode(ev.Id),
                ev.EventNumber,
                reason = input.Reason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<SystemUpdateEventDto>.Success(ToDto(ev, schedule));
    }

    /// <inheritdoc />
    public async Task<Result<SystemUpdateEventDto>> GetByIdAsync(
        string eventSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(eventSqid);
        if (decoded.IsFailure)
        {
            return Result<SystemUpdateEventDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _read.SystemUpdateEvents
            .FirstOrDefaultAsync(e => e.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<SystemUpdateEventDto>.Failure(ErrorCodes.NotFound, "System-update event not found.");
        }
        var schedule = await _read.SystemUpdateSchedules
            .FirstOrDefaultAsync(s => s.Id == row.ScheduleId, cancellationToken)
            .ConfigureAwait(false);
        return Result<SystemUpdateEventDto>.Success(ToDto(row, schedule));
    }

    /// <inheritdoc />
    public async Task<Result<SystemUpdateEventPageDto>> ListAsync(
        SystemUpdateEventFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SystemUpdateEventPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<SystemUpdateEvent> q = _read.SystemUpdateEvents;
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<SystemUpdateEventStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(e => e.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.ScheduleSqid))
        {
            var decoded = _sqids.TryDecode(filter.ScheduleSqid);
            if (decoded.IsFailure)
            {
                return Result<SystemUpdateEventPageDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            }
            var sid = decoded.Value;
            q = q.Where(e => e.ScheduleId == sid);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(e => e.PlannedDeploymentUtc)
            .ThenByDescending(e => e.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var scheduleIds = rows.Select(r => r.ScheduleId).Distinct().ToList();
        var schedules = await _read.SystemUpdateSchedules
            .Where(s => scheduleIds.Contains(s.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var lookup = schedules.ToDictionary(s => s.Id);

        var page = new SystemUpdateEventPageDto(
            Items: rows.Select(r => ToDto(r, lookup.GetValueOrDefault(r.ScheduleId))).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<SystemUpdateEventPageDto>.Success(page);
    }

    /// <summary>Common helper for simple Status → Status transitions.</summary>
    /// <param name="eventSqid">Sqid of the event to transition.</param>
    /// <param name="requiredFrom">State the row must currently be in.</param>
    /// <param name="target">Target state.</param>
    /// <param name="stampStartedAt">Stamp <c>DeploymentStartedAt</c>.</param>
    /// <param name="stampCompletedAt">Stamp <c>DeploymentCompletedAt</c>.</param>
    /// <param name="auditCode">Stable audit event code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success.</returns>
    private async Task<Result<SystemUpdateEventDto>> SimpleTransitionAsync(
        string eventSqid,
        SystemUpdateEventStatus requiredFrom,
        SystemUpdateEventStatus target,
        bool stampStartedAt,
        bool stampCompletedAt,
        string auditCode,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(eventSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SystemUpdateEventDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ev = loaded.Value;
        if (ev.Status != requiredFrom)
        {
            return Result<SystemUpdateEventDto>.Failure(
                ErrorCodes.UpdateEventInvalidTransition,
                $"Cannot transition from '{ev.Status}' to '{target}'; required '{requiredFrom}'.");
        }
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        ev.Status = target;
        if (stampStartedAt) ev.DeploymentStartedAt = now;
        if (stampCompletedAt) ev.DeploymentCompletedAt = now;
        ev.UpdatedAtUtc = now;
        ev.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var schedule = await _db.SystemUpdateSchedules
            .FirstOrDefaultAsync(s => s.Id == ev.ScheduleId, cancellationToken)
            .ConfigureAwait(false);

        await EmitAuditAsync(
            auditCode,
            AuditSeverity.Sensitive,
            actor,
            ev.Id,
            new
            {
                eventSqid = _sqids.Encode(ev.Id),
                ev.EventNumber,
                transitionTo = target.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<SystemUpdateEventDto>.Success(ToDto(ev, schedule));
    }

    /// <summary>Generates the deterministic <c>UPD-{year}-{seq:000000}</c> event number.</summary>
    /// <param name="now">Current UTC instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The next event number for the current year.</returns>
    private async Task<string> MintEventNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        var year = now.Year;
        var yearPrefix = $"UPD-{year}-";
        var sequence = await _db.SystemUpdateEvents
            .Where(e => e.EventNumber.StartsWith(yearPrefix))
            .CountAsync(cancellationToken).ConfigureAwait(false) + 1;
        return string.Create(CultureInfo.InvariantCulture, $"{yearPrefix}{sequence:D6}");
    }

    /// <summary>Loads a tracked event entity by Sqid.</summary>
    /// <param name="eventSqid">Sqid of the event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tracked entity on success.</returns>
    private async Task<Result<SystemUpdateEvent>> LoadAsync(string eventSqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(eventSqid);
        if (decoded.IsFailure)
        {
            return Result<SystemUpdateEvent>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.SystemUpdateEvents
            .FirstOrDefaultAsync(e => e.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<SystemUpdateEvent>.Failure(ErrorCodes.NotFound, "System-update event not found.")
            : Result<SystemUpdateEvent>.Success(row);
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
            nameof(SystemUpdateEvent),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an entity into its outbound DTO.</summary>
    /// <param name="e">Loaded entity.</param>
    /// <param name="schedule">Resolved parent schedule (or null).</param>
    /// <returns>Populated DTO.</returns>
    private SystemUpdateEventDto ToDto(SystemUpdateEvent e, SystemUpdateSchedule? schedule) => new(
        Id: _sqids.Encode(e.Id),
        ScheduleSqid: schedule is null ? string.Empty : _sqids.Encode(schedule.Id),
        EventNumber: e.EventNumber,
        Title: e.Title,
        Description: e.Description,
        PlannedDeploymentUtc: e.PlannedDeploymentUtc,
        Status: e.Status.ToString(),
        NotifiedAt: e.NotifiedAt,
        DeploymentStartedAt: e.DeploymentStartedAt,
        DeploymentCompletedAt: e.DeploymentCompletedAt,
        CancelledAt: e.CancelledAt,
        CancelReason: e.CancelReason,
        MaintenanceWindowSqid: e.MaintenanceWindowId is null ? null : _sqids.Encode(e.MaintenanceWindowId.Value));
}
