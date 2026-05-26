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
/// R2502 / TOR PIR 025 — production implementation of
/// <see cref="IMaintenanceWindowService"/>. Enforces per-kind duration
/// ceilings at create time and per-kind notice-lead-time requirements at
/// notice-posting time. Notice lead-times are computed against the
/// referenced <see cref="BusinessHoursPolicy"/> (business days, not calendar
/// days).
/// </summary>
public sealed class MaintenanceWindowService : IMaintenanceWindowService
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
    private readonly IValidator<MaintenanceWindowCreateInputDto> _createValidator;
    private readonly IValidator<MaintenanceWindowReasonInputDto> _reasonValidator;
    private readonly IValidator<MaintenanceWindowFilterDto> _filterValidator;

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
    public MaintenanceWindowService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<MaintenanceWindowCreateInputDto> createValidator,
        IValidator<MaintenanceWindowReasonInputDto> reasonValidator,
        IValidator<MaintenanceWindowFilterDto> filterValidator)
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
    public async Task<Result<MaintenanceWindowDto>> CreateAsync(
        MaintenanceWindowCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<MaintenanceWindowDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var now = _clock.UtcNow;
        if (input.ScheduledStartUtc <= now)
        {
            return Result<MaintenanceWindowDto>.Failure(
                ErrorCodes.ValidationFailed,
                "ScheduledStartUtc must be in the future.");
        }

        var kind = Enum.Parse<MaintenanceWindowKind>(input.WindowKind, ignoreCase: false);
        var maxHours = MaxHoursFor(kind);
        var duration = input.ScheduledEndUtc - input.ScheduledStartUtc;
        if (duration.TotalHours > maxHours + 1e-9)
        {
            return Result<MaintenanceWindowDto>.Failure(
                ErrorCodes.MaintenanceDurationExceeded,
                $"{kind} windows may not exceed {maxHours} hour(s); requested {duration.TotalHours:F2}h.");
        }

        var policy = await _db.BusinessHoursPolicies
            .FirstOrDefaultAsync(p => p.Code == input.BusinessHoursPolicyCode && p.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (policy is null)
        {
            return Result<MaintenanceWindowDto>.Failure(
                ErrorCodes.BusinessHoursPolicyNotFound,
                $"No active business-hours policy with Code '{input.BusinessHoursPolicyCode}'.");
        }

        var windowNumber = await MintWindowNumberAsync(now, cancellationToken).ConfigureAwait(false);
        var actor = _caller.UserSqid ?? "admin";

        var window = new MaintenanceWindow
        {
            WindowNumber = windowNumber,
            BusinessHoursPolicyId = policy.Id,
            WindowKind = kind,
            Title = input.Title,
            Description = input.Description,
            ScheduledStartUtc = input.ScheduledStartUtc,
            ScheduledEndUtc = input.ScheduledEndUtc,
            Status = MaintenanceWindowStatus.Draft,
            RequestedByUserId = _caller.UserId ?? 0,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.MaintenanceWindows.Add(window);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.MaintenanceWindowCreated.Add(1, new System.Diagnostics.TagList { { "kind", kind.ToString() } });

        await EmitAuditAsync(
            IMaintenanceWindowService.AuditCreated,
            AuditSeverity.Critical,
            actor,
            window.Id,
            new
            {
                windowSqid = _sqids.Encode(window.Id),
                window.WindowNumber,
                kind = window.WindowKind.ToString(),
                window.ScheduledStartUtc,
                window.ScheduledEndUtc,
                businessHoursPolicyCode = policy.Code,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<MaintenanceWindowDto>.Success(ToDto(window, policy));
    }

    /// <inheritdoc />
    public async Task<Result<MaintenanceWindowDto>> PostNoticeAsync(
        string windowSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(windowSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<MaintenanceWindowDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var window = loaded.Value;
        if (window.Status != MaintenanceWindowStatus.Draft)
        {
            return Result<MaintenanceWindowDto>.Failure(
                ErrorCodes.MaintenanceInvalidTransition,
                $"Cannot post notice for a window in '{window.Status}' (must be Draft).");
        }

        var policy = await _db.BusinessHoursPolicies
            .FirstOrDefaultAsync(p => p.Id == window.BusinessHoursPolicyId, cancellationToken)
            .ConfigureAwait(false);
        if (policy is null)
        {
            return Result<MaintenanceWindowDto>.Failure(
                ErrorCodes.BusinessHoursPolicyNotFound,
                "The referenced business-hours policy no longer exists.");
        }

        var now = _clock.UtcNow;
        var requiredLeadDays = MinNoticeBusinessDaysFor(window.WindowKind);
        if (requiredLeadDays > 0)
        {
            // Compute "now + requiredLeadDays business days" — if that target is
            // strictly after ScheduledStartUtc, the lead time is insufficient.
            var earliestAllowedStart = BusinessHoursPolicyService.AddBusinessDays(policy, now, requiredLeadDays);
            if (window.ScheduledStartUtc < earliestAllowedStart)
            {
                return Result<MaintenanceWindowDto>.Failure(
                    ErrorCodes.MaintenanceNoticeLeadTimeInsufficient,
                    $"{window.WindowKind} windows require at least {requiredLeadDays} business days of advance notice.");
            }
        }

        var actor = _caller.UserSqid ?? "admin";
        window.Status = MaintenanceWindowStatus.NoticePeriod;
        window.NoticePostedAt = now;
        window.UpdatedAtUtc = now;
        window.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.MaintenanceWindowNoticePosted.Add(1, new System.Diagnostics.TagList { { "kind", window.WindowKind.ToString() } });

        await EmitAuditAsync(
            IMaintenanceWindowService.AuditNoticePosted,
            AuditSeverity.Critical,
            actor,
            window.Id,
            new
            {
                windowSqid = _sqids.Encode(window.Id),
                window.WindowNumber,
                kind = window.WindowKind.ToString(),
                noticePostedAt = now.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<MaintenanceWindowDto>.Success(ToDto(window, policy));
    }

    /// <inheritdoc />
    public Task<Result<MaintenanceWindowDto>> ApproveAsync(
        string windowSqid,
        CancellationToken cancellationToken = default)
        => SimpleTransitionAsync(
            windowSqid,
            requiredFrom: MaintenanceWindowStatus.NoticePeriod,
            target: MaintenanceWindowStatus.Approved,
            stampApprovedAt: true,
            stampStartedAt: false,
            stampCompletedAt: false,
            auditCode: IMaintenanceWindowService.AuditApproved,
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<MaintenanceWindowDto>> StartAsync(
        string windowSqid,
        CancellationToken cancellationToken = default)
        => SimpleTransitionAsync(
            windowSqid,
            requiredFrom: MaintenanceWindowStatus.Approved,
            target: MaintenanceWindowStatus.InProgress,
            stampApprovedAt: false,
            stampStartedAt: true,
            stampCompletedAt: false,
            auditCode: IMaintenanceWindowService.AuditStarted,
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<MaintenanceWindowDto>> CompleteAsync(
        string windowSqid,
        CancellationToken cancellationToken = default)
        => SimpleTransitionAsync(
            windowSqid,
            requiredFrom: MaintenanceWindowStatus.InProgress,
            target: MaintenanceWindowStatus.Completed,
            stampApprovedAt: false,
            stampStartedAt: false,
            stampCompletedAt: true,
            auditCode: IMaintenanceWindowService.AuditCompleted,
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<MaintenanceWindowDto>> CancelAsync(
        string windowSqid,
        MaintenanceWindowReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var rv = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!rv.IsValid)
        {
            return Result<MaintenanceWindowDto>.Failure(ErrorCodes.ValidationFailed, rv.Errors[0].ErrorMessage);
        }

        var loaded = await LoadAsync(windowSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<MaintenanceWindowDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var window = loaded.Value;
        if (window.Status is MaintenanceWindowStatus.Completed or MaintenanceWindowStatus.Cancelled)
        {
            return Result<MaintenanceWindowDto>.Failure(
                ErrorCodes.MaintenanceInvalidTransition,
                $"Cannot cancel a window in terminal state '{window.Status}'.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        window.Status = MaintenanceWindowStatus.Cancelled;
        window.CancelledAt = now;
        window.CancelReason = input.Reason;
        window.UpdatedAtUtc = now;
        window.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var policy = await _db.BusinessHoursPolicies
            .FirstOrDefaultAsync(p => p.Id == window.BusinessHoursPolicyId, cancellationToken)
            .ConfigureAwait(false);

        await EmitAuditAsync(
            IMaintenanceWindowService.AuditCancelled,
            AuditSeverity.Critical,
            actor,
            window.Id,
            new
            {
                windowSqid = _sqids.Encode(window.Id),
                window.WindowNumber,
                kind = window.WindowKind.ToString(),
                reason = input.Reason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<MaintenanceWindowDto>.Success(ToDto(window, policy));
    }

    /// <inheritdoc />
    public async Task<Result<MaintenanceWindowDto>> GetByIdAsync(
        string windowSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(windowSqid);
        if (decoded.IsFailure)
        {
            return Result<MaintenanceWindowDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _read.MaintenanceWindows
            .FirstOrDefaultAsync(p => p.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<MaintenanceWindowDto>.Failure(ErrorCodes.NotFound, "Maintenance window not found.");
        }
        var policy = await _read.BusinessHoursPolicies
            .FirstOrDefaultAsync(p => p.Id == row.BusinessHoursPolicyId, cancellationToken)
            .ConfigureAwait(false);
        return Result<MaintenanceWindowDto>.Success(ToDto(row, policy));
    }

    /// <inheritdoc />
    public async Task<Result<MaintenanceWindowPageDto>> ListAsync(
        MaintenanceWindowFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<MaintenanceWindowPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<MaintenanceWindow> q = _read.MaintenanceWindows;

        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<MaintenanceWindowStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(w => w.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.WindowKind)
            && Enum.TryParse<MaintenanceWindowKind>(filter.WindowKind, ignoreCase: false, out var kind))
        {
            q = q.Where(w => w.WindowKind == kind);
        }
        if (filter.ScheduledStartAfterUtc is not null)
        {
            var after = filter.ScheduledStartAfterUtc.Value;
            q = q.Where(w => w.ScheduledStartUtc >= after);
        }
        if (filter.ScheduledStartBeforeUtc is not null)
        {
            var before = filter.ScheduledStartBeforeUtc.Value;
            q = q.Where(w => w.ScheduledStartUtc <= before);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(w => w.ScheduledStartUtc)
            .ThenByDescending(w => w.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var policyIds = rows.Select(r => r.BusinessHoursPolicyId).Distinct().ToList();
        var policies = await _read.BusinessHoursPolicies
            .Where(p => policyIds.Contains(p.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var lookup = policies.ToDictionary(p => p.Id);

        var page = new MaintenanceWindowPageDto(
            Items: rows.Select(r => ToDto(r, lookup.GetValueOrDefault(r.BusinessHoursPolicyId))).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<MaintenanceWindowPageDto>.Success(page);
    }

    /// <summary>Common helper for simple Status → Status transitions.</summary>
    /// <param name="windowSqid">Sqid of the window to transition.</param>
    /// <param name="requiredFrom">Current state required for the transition to be legal.</param>
    /// <param name="target">Target state.</param>
    /// <param name="stampApprovedAt">When <c>true</c>, stamp <see cref="MaintenanceWindow.ApprovedAt"/>.</param>
    /// <param name="stampStartedAt">When <c>true</c>, stamp <see cref="MaintenanceWindow.StartedAt"/>.</param>
    /// <param name="stampCompletedAt">When <c>true</c>, stamp <see cref="MaintenanceWindow.CompletedAt"/>.</param>
    /// <param name="auditCode">Stable audit event code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success.</returns>
    private async Task<Result<MaintenanceWindowDto>> SimpleTransitionAsync(
        string windowSqid,
        MaintenanceWindowStatus requiredFrom,
        MaintenanceWindowStatus target,
        bool stampApprovedAt,
        bool stampStartedAt,
        bool stampCompletedAt,
        string auditCode,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(windowSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<MaintenanceWindowDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var window = loaded.Value;
        if (window.Status != requiredFrom)
        {
            return Result<MaintenanceWindowDto>.Failure(
                ErrorCodes.MaintenanceInvalidTransition,
                $"Cannot transition from '{window.Status}' to '{target}'; required '{requiredFrom}'.");
        }
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        window.Status = target;
        if (stampApprovedAt)
        {
            window.ApprovedAt = now;
            window.ApprovedByUserId = _caller.UserId;
        }
        if (stampStartedAt)
        {
            window.StartedAt = now;
        }
        if (stampCompletedAt)
        {
            window.CompletedAt = now;
        }
        window.UpdatedAtUtc = now;
        window.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var policy = await _db.BusinessHoursPolicies
            .FirstOrDefaultAsync(p => p.Id == window.BusinessHoursPolicyId, cancellationToken)
            .ConfigureAwait(false);

        await EmitAuditAsync(
            auditCode,
            AuditSeverity.Sensitive,
            actor,
            window.Id,
            new
            {
                windowSqid = _sqids.Encode(window.Id),
                window.WindowNumber,
                kind = window.WindowKind.ToString(),
                transitionTo = target.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<MaintenanceWindowDto>.Success(ToDto(window, policy));
    }

    /// <summary>Per-kind duration ceiling in hours.</summary>
    /// <param name="kind">Window kind.</param>
    /// <returns>Maximum duration in hours.</returns>
    private static int MaxHoursFor(MaintenanceWindowKind kind) => kind switch
    {
        MaintenanceWindowKind.Ordinary => IMaintenanceWindowService.OrdinaryMaxHours,
        MaintenanceWindowKind.Major => IMaintenanceWindowService.MajorMaxHours,
        MaintenanceWindowKind.Urgent => IMaintenanceWindowService.UrgentMaxHours,
        _ => 0,
    };

    /// <summary>Per-kind minimum notice in business days.</summary>
    /// <param name="kind">Window kind.</param>
    /// <returns>Minimum business-days notice (0 for Urgent).</returns>
    private static int MinNoticeBusinessDaysFor(MaintenanceWindowKind kind) => kind switch
    {
        MaintenanceWindowKind.Ordinary => IMaintenanceWindowService.OrdinaryMinNoticeBusinessDays,
        MaintenanceWindowKind.Major => IMaintenanceWindowService.MajorMinNoticeBusinessDays,
        MaintenanceWindowKind.Urgent => 0,
        _ => 0,
    };

    /// <summary>Generates the deterministic <c>MW-{year}-{seq:000000}</c> window number.</summary>
    /// <param name="now">Current UTC instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The next window number for the current year.</returns>
    private async Task<string> MintWindowNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        var year = now.Year;
        var yearPrefix = $"MW-{year}-";
        var sequence = await _db.MaintenanceWindows
            .Where(w => w.WindowNumber.StartsWith(yearPrefix))
            .CountAsync(cancellationToken).ConfigureAwait(false) + 1;
        return string.Create(CultureInfo.InvariantCulture, $"{yearPrefix}{sequence:D6}");
    }

    /// <summary>Loads a tracked window entity by Sqid.</summary>
    /// <param name="windowSqid">Sqid of the window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tracked entity on success.</returns>
    private async Task<Result<MaintenanceWindow>> LoadAsync(string windowSqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(windowSqid);
        if (decoded.IsFailure)
        {
            return Result<MaintenanceWindow>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.MaintenanceWindows
            .FirstOrDefaultAsync(w => w.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<MaintenanceWindow>.Failure(ErrorCodes.NotFound, "Maintenance window not found.")
            : Result<MaintenanceWindow>.Success(row);
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
            nameof(MaintenanceWindow),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an entity into its outbound DTO.</summary>
    /// <param name="w">Loaded entity.</param>
    /// <param name="policy">Resolved business-hours policy (or null when unavailable).</param>
    /// <returns>Populated DTO.</returns>
    private MaintenanceWindowDto ToDto(MaintenanceWindow w, BusinessHoursPolicy? policy) => new(
        Id: _sqids.Encode(w.Id),
        WindowNumber: w.WindowNumber,
        BusinessHoursPolicySqid: policy is null ? string.Empty : _sqids.Encode(policy.Id),
        WindowKind: w.WindowKind.ToString(),
        Title: w.Title,
        Description: w.Description,
        ScheduledStartUtc: w.ScheduledStartUtc,
        ScheduledEndUtc: w.ScheduledEndUtc,
        Status: w.Status.ToString(),
        NoticePostedAt: w.NoticePostedAt,
        ApprovedAt: w.ApprovedAt,
        StartedAt: w.StartedAt,
        CompletedAt: w.CompletedAt,
        CancelledAt: w.CancelledAt,
        CancelReason: w.CancelReason);
}
