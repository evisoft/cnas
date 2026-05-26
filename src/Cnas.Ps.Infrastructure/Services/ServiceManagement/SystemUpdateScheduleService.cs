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
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.ServiceManagement;

/// <summary>
/// R2503 / TOR PIR 022-023 — production implementation of
/// <see cref="ISystemUpdateScheduleService"/>. CRUD over the
/// <c>SystemUpdateSchedule</c> registry with audit + metric emission for
/// every state transition.
/// </summary>
public sealed class SystemUpdateScheduleService : ISystemUpdateScheduleService
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
    private readonly IValidator<SystemUpdateScheduleCreateInputDto> _createValidator;
    private readonly IValidator<SystemUpdateScheduleModifyInputDto> _modifyValidator;
    private readonly IValidator<SystemUpdateScheduleFilterDto> _filterValidator;

    /// <summary>Constructs the service.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="createValidator">Validator for create input.</param>
    /// <param name="modifyValidator">Validator for modify input.</param>
    /// <param name="filterValidator">Validator for filter input.</param>
    public SystemUpdateScheduleService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<SystemUpdateScheduleCreateInputDto> createValidator,
        IValidator<SystemUpdateScheduleModifyInputDto> modifyValidator,
        IValidator<SystemUpdateScheduleFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _createValidator = createValidator;
        _modifyValidator = modifyValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<SystemUpdateScheduleDto>> CreateAsync(
        SystemUpdateScheduleCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SystemUpdateScheduleDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var duplicate = await _db.SystemUpdateSchedules
            .AnyAsync(s => s.ScheduleCode == input.ScheduleCode, cancellationToken)
            .ConfigureAwait(false);
        if (duplicate)
        {
            return Result<SystemUpdateScheduleDto>.Failure(
                ErrorCodes.UpdateScheduleDuplicateCode,
                $"A system-update schedule with ScheduleCode '{input.ScheduleCode}' already exists.");
        }

        var cadence = Enum.Parse<UpdateCadenceKind>(input.Cadence, ignoreCase: false);
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";

        var schedule = new SystemUpdateSchedule
        {
            ScheduleCode = input.ScheduleCode,
            Title = input.Title,
            Cadence = cadence,
            NoticeLeadTimeDays = input.NoticeLeadTimeDays,
            Description = input.Description,
            RegisteredByUserId = _caller.UserId ?? 0,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.SystemUpdateSchedules.Add(schedule);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            ISystemUpdateScheduleService.AuditCreated,
            AuditSeverity.Sensitive,
            actor,
            schedule.Id,
            new
            {
                scheduleSqid = _sqids.Encode(schedule.Id),
                schedule.ScheduleCode,
                cadence = schedule.Cadence.ToString(),
                schedule.NoticeLeadTimeDays,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<SystemUpdateScheduleDto>.Success(ToDto(schedule));
    }

    /// <inheritdoc />
    public async Task<Result<SystemUpdateScheduleDto>> ModifyAsync(
        string scheduleSqid,
        SystemUpdateScheduleModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _modifyValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SystemUpdateScheduleDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadAsync(scheduleSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SystemUpdateScheduleDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var schedule = loaded.Value;

        if (input.Title is not null) schedule.Title = input.Title;
        if (input.NoticeLeadTimeDays is not null) schedule.NoticeLeadTimeDays = input.NoticeLeadTimeDays.Value;
        if (input.Description is not null) schedule.Description = input.Description;

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        schedule.UpdatedAtUtc = now;
        schedule.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            ISystemUpdateScheduleService.AuditModified,
            AuditSeverity.Sensitive,
            actor,
            schedule.Id,
            new
            {
                scheduleSqid = _sqids.Encode(schedule.Id),
                schedule.ScheduleCode,
                changeReason = input.ChangeReason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<SystemUpdateScheduleDto>.Success(ToDto(schedule));
    }

    /// <inheritdoc />
    public Task<Result<SystemUpdateScheduleDto>> ActivateAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default)
        => TransitionAsync(scheduleSqid, newIsActive: true, label: "Activate", cancellationToken);

    /// <inheritdoc />
    public Task<Result<SystemUpdateScheduleDto>> DeactivateAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default)
        => TransitionAsync(scheduleSqid, newIsActive: false, label: "Deactivate", cancellationToken);

    /// <inheritdoc />
    public async Task<Result<SystemUpdateScheduleDto>> GetByIdAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(scheduleSqid);
        if (decoded.IsFailure)
        {
            return Result<SystemUpdateScheduleDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _read.SystemUpdateSchedules
            .FirstOrDefaultAsync(s => s.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<SystemUpdateScheduleDto>.Failure(ErrorCodes.NotFound, "System-update schedule not found.")
            : Result<SystemUpdateScheduleDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<SystemUpdateScheduleDto>> GetByCodeAsync(
        string scheduleCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scheduleCode))
        {
            return Result<SystemUpdateScheduleDto>.Failure(ErrorCodes.ValidationFailed, "ScheduleCode is required.");
        }
        var row = await _read.SystemUpdateSchedules
            .FirstOrDefaultAsync(s => s.ScheduleCode == scheduleCode, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<SystemUpdateScheduleDto>.Failure(ErrorCodes.NotFound, "System-update schedule not found.")
            : Result<SystemUpdateScheduleDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<SystemUpdateSchedulePageDto>> ListAsync(
        SystemUpdateScheduleFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SystemUpdateSchedulePageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<SystemUpdateSchedule> q = _read.SystemUpdateSchedules;
        if (filter.IsActive is not null)
        {
            var wantActive = filter.IsActive.Value;
            q = q.Where(s => s.IsActive == wantActive);
        }
        if (!string.IsNullOrWhiteSpace(filter.Cadence)
            && Enum.TryParse<UpdateCadenceKind>(filter.Cadence, ignoreCase: false, out var cadence))
        {
            q = q.Where(s => s.Cadence == cadence);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(s => s.CreatedAtUtc)
            .ThenByDescending(s => s.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new SystemUpdateSchedulePageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<SystemUpdateSchedulePageDto>.Success(page);
    }

    /// <summary>Common helper for Activate / Deactivate transitions.</summary>
    /// <param name="scheduleSqid">Sqid of the schedule to flip.</param>
    /// <param name="newIsActive">Desired IsActive flag.</param>
    /// <param name="label">Audit-trail label.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success.</returns>
    private async Task<Result<SystemUpdateScheduleDto>> TransitionAsync(
        string scheduleSqid,
        bool newIsActive,
        string label,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(scheduleSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SystemUpdateScheduleDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var schedule = loaded.Value;
        if (schedule.IsActive == newIsActive)
        {
            return Result<SystemUpdateScheduleDto>.Failure(
                ErrorCodes.Conflict,
                newIsActive ? "Schedule is already Active." : "Schedule is already Inactive.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        schedule.IsActive = newIsActive;
        schedule.UpdatedAtUtc = now;
        schedule.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            ISystemUpdateScheduleService.AuditTransitioned,
            AuditSeverity.Sensitive,
            actor,
            schedule.Id,
            new
            {
                scheduleSqid = _sqids.Encode(schedule.Id),
                schedule.ScheduleCode,
                transition = label,
                isActive = schedule.IsActive,
                atUtc = now.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<SystemUpdateScheduleDto>.Success(ToDto(schedule));
    }

    /// <summary>Loads a tracked schedule entity by Sqid.</summary>
    /// <param name="scheduleSqid">Sqid of the schedule.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tracked entity on success.</returns>
    private async Task<Result<SystemUpdateSchedule>> LoadAsync(string scheduleSqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(scheduleSqid);
        if (decoded.IsFailure)
        {
            return Result<SystemUpdateSchedule>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.SystemUpdateSchedules
            .FirstOrDefaultAsync(s => s.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<SystemUpdateSchedule>.Failure(ErrorCodes.NotFound, "System-update schedule not found.")
            : Result<SystemUpdateSchedule>.Success(row);
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
            nameof(SystemUpdateSchedule),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an entity into its outbound DTO.</summary>
    /// <param name="s">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private SystemUpdateScheduleDto ToDto(SystemUpdateSchedule s) => new(
        Id: _sqids.Encode(s.Id),
        ScheduleCode: s.ScheduleCode,
        Title: s.Title,
        Cadence: s.Cadence.ToString(),
        NoticeLeadTimeDays: s.NoticeLeadTimeDays,
        Description: s.Description,
        IsActive: s.IsActive);
}
