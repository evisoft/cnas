using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Quartz.Impl.Matchers;

namespace Cnas.Ps.Infrastructure.Services.Scheduling;

/// <summary>
/// R0200 / TOR CF 20.01-03, MR 012 — concrete <see cref="ICronAdminService"/>. CRUD
/// over the <see cref="JobScheduleOverride"/> registry combined with the running
/// Quartz <see cref="ISchedulerFactory"/> so every change is both persisted (for
/// restart survival) AND applied to the live scheduler (for immediate effect). Every
/// mutation emits a Critical-severity audit row + bumps the
/// <see cref="CnasMeter.CronScheduleMutated"/> counter.
/// </summary>
public sealed class CronAdminService : ICronAdminService
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
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IValidator<CronExpressionInputDto> _cronValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="schedulerFactory">Quartz scheduler factory.</param>
    /// <param name="cronValidator">Cron-expression validator.</param>
    public CronAdminService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        ISchedulerFactory schedulerFactory,
        IValidator<CronExpressionInputDto> cronValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(schedulerFactory);
        ArgumentNullException.ThrowIfNull(cronValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _schedulerFactory = schedulerFactory;
        _cronValidator = cronValidator;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<JobScheduleOverrideDto>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        // 1. Enumerate registered Quartz jobs + their effective default crons.
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var jobKeys = await scheduler
            .GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), cancellationToken)
            .ConfigureAwait(false);

        var defaultCronByJob = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in jobKeys)
        {
            var triggers = await scheduler.GetTriggersOfJob(key, cancellationToken).ConfigureAwait(false);
            // Pick the first cron trigger as the baked-in default; fall back to "" when
            // the job has no cron trigger (e.g. interval-only triggers).
            var cronTrigger = triggers.OfType<ICronTrigger>().FirstOrDefault();
            defaultCronByJob[key.Name] = cronTrigger?.CronExpressionString ?? string.Empty;
        }

        // 2. Pull every override row keyed by job code so we can join in-memory.
        var overrides = await _read.JobScheduleOverrides
            .Where(o => o.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var overrideByJob = overrides.ToDictionary(o => o.JobCode, StringComparer.Ordinal);

        // 3. Emit one DTO per registered job, alphabetically by JobCode for
        //    deterministic UI ordering.
        var rows = new List<JobScheduleOverrideDto>(defaultCronByJob.Count);
        foreach (var jobCode in defaultCronByJob.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var defaultCron = defaultCronByJob[jobCode];
            if (overrideByJob.TryGetValue(jobCode, out var ov))
            {
                rows.Add(ToDto(ov, defaultCron));
            }
            else
            {
                rows.Add(new JobScheduleOverrideDto(
                    Id: null,
                    JobCode: jobCode,
                    CronExpression: defaultCron,
                    DefaultCronExpression: defaultCron,
                    IsPaused: false,
                    IsOverridden: false,
                    UpdatedAtUtc: null,
                    UpdatedByUserSqid: null));
            }
        }
        return Result<IReadOnlyList<JobScheduleOverrideDto>>.Success(rows);
    }

    /// <inheritdoc />
    public async Task<Result<JobScheduleOverrideDto>> UpsertAsync(
        string jobCode,
        CronExpressionInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var v = await _cronValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<JobScheduleOverrideDto>.Failure(
                ICronAdminService.InvalidCronCode,
                v.Errors[0].ErrorMessage);
        }

        var jobLookup = await ResolveJobAsync(jobCode, cancellationToken).ConfigureAwait(false);
        if (jobLookup.IsFailure)
        {
            return Result<JobScheduleOverrideDto>.Failure(jobLookup.ErrorCode!, jobLookup.ErrorMessage!);
        }
        var (scheduler, defaultCron) = jobLookup.Value;

        // Load-or-create the override row.
        var existing = await _db.JobScheduleOverrides
            .FirstOrDefaultAsync(o => o.JobCode == jobCode, cancellationToken)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";

        if (existing is null)
        {
            existing = new JobScheduleOverride
            {
                JobCode = jobCode,
                CronExpression = input.CronExpression,
                IsPaused = false,
                UpdatedByUserId = _caller.UserId,
                CreatedAtUtc = now,
                CreatedBy = actor,
                IsActive = true,
            };
            _db.JobScheduleOverrides.Add(existing);
        }
        else
        {
            existing.CronExpression = input.CronExpression;
            existing.UpdatedAtUtc = now;
            existing.UpdatedBy = actor;
            existing.UpdatedByUserId = _caller.UserId;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Apply to the running scheduler — reschedule the job's existing cron trigger.
        await ApplyCronToSchedulerAsync(scheduler, jobCode, input.CronExpression, cancellationToken).ConfigureAwait(false);

        CnasMeter.CronScheduleMutated.Add(1,
            new KeyValuePair<string, object?>("change_kind", "upserted"),
            new KeyValuePair<string, object?>("job_code", jobCode));

        await EmitAuditAsync(
            ICronAdminService.AuditCronUpserted,
            actor,
            existing.Id,
            new
            {
                jobCode,
                cronExpression = input.CronExpression,
                overrideSqid = _sqids.Encode(existing.Id),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<JobScheduleOverrideDto>.Success(ToDto(existing, defaultCron));
    }

    /// <inheritdoc />
    public Task<Result<JobScheduleOverrideDto>> PauseAsync(
        string jobCode,
        CancellationToken cancellationToken = default)
        => SetPausedAsync(jobCode, paused: true, cancellationToken);

    /// <inheritdoc />
    public Task<Result<JobScheduleOverrideDto>> ResumeAsync(
        string jobCode,
        CancellationToken cancellationToken = default)
        => SetPausedAsync(jobCode, paused: false, cancellationToken);

    /// <summary>
    /// Shared implementation for <see cref="PauseAsync"/> / <see cref="ResumeAsync"/>.
    /// Persists the flag, applies it to the live scheduler, and emits the matching
    /// audit row.
    /// </summary>
    /// <param name="jobCode">Stable Quartz job code.</param>
    /// <param name="paused">Target paused flag.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success.</returns>
    private async Task<Result<JobScheduleOverrideDto>> SetPausedAsync(
        string jobCode,
        bool paused,
        CancellationToken cancellationToken)
    {
        var jobLookup = await ResolveJobAsync(jobCode, cancellationToken).ConfigureAwait(false);
        if (jobLookup.IsFailure)
        {
            return Result<JobScheduleOverrideDto>.Failure(jobLookup.ErrorCode!, jobLookup.ErrorMessage!);
        }
        var (scheduler, defaultCron) = jobLookup.Value;

        // Load-or-create the override row. Pausing a job that has no cron override yet
        // creates a row that carries the default cron + IsPaused=true so the pause
        // survives a restart even if the operator never customised cadence.
        var existing = await _db.JobScheduleOverrides
            .FirstOrDefaultAsync(o => o.JobCode == jobCode, cancellationToken)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";

        if (existing is null)
        {
            existing = new JobScheduleOverride
            {
                JobCode = jobCode,
                CronExpression = defaultCron,
                IsPaused = paused,
                UpdatedByUserId = _caller.UserId,
                CreatedAtUtc = now,
                CreatedBy = actor,
                IsActive = true,
            };
            _db.JobScheduleOverrides.Add(existing);
        }
        else
        {
            existing.IsPaused = paused;
            existing.UpdatedAtUtc = now;
            existing.UpdatedBy = actor;
            existing.UpdatedByUserId = _caller.UserId;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Apply to the live scheduler.
        var jobKey = new JobKey(jobCode);
        if (paused)
        {
            await scheduler.PauseJob(jobKey, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await scheduler.ResumeJob(jobKey, cancellationToken).ConfigureAwait(false);
        }

        var kind = paused ? "paused" : "resumed";
        CnasMeter.CronScheduleMutated.Add(1,
            new KeyValuePair<string, object?>("change_kind", kind),
            new KeyValuePair<string, object?>("job_code", jobCode));

        var eventCode = paused
            ? ICronAdminService.AuditCronPaused
            : ICronAdminService.AuditCronResumed;
        await EmitAuditAsync(
            eventCode,
            actor,
            existing.Id,
            new { jobCode, overrideSqid = _sqids.Encode(existing.Id), isPaused = paused },
            cancellationToken).ConfigureAwait(false);

        return Result<JobScheduleOverrideDto>.Success(ToDto(existing, defaultCron));
    }

    /// <summary>
    /// Looks up a Quartz job by its stable job code, returning a friendly
    /// <see cref="ICronAdminService.UnknownJobCode"/> failure when the job is not
    /// registered. On success returns the running scheduler + the job's baked-in
    /// default cron expression (empty string when the job has no cron trigger).
    /// </summary>
    /// <param name="jobCode">Candidate job code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved scheduler + default cron, or a stable failure code.</returns>
    private async Task<Result<(IScheduler scheduler, string defaultCron)>> ResolveJobAsync(
        string jobCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobCode))
        {
            return Result<(IScheduler, string)>.Failure(
                ICronAdminService.UnknownJobCode,
                "JobCode is required.");
        }
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var key = new JobKey(jobCode);
        var exists = await scheduler.CheckExists(key, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            return Result<(IScheduler, string)>.Failure(
                ICronAdminService.UnknownJobCode,
                $"Quartz job '{jobCode}' is not registered with the scheduler.");
        }
        var triggers = await scheduler.GetTriggersOfJob(key, cancellationToken).ConfigureAwait(false);
        var cronTrigger = triggers.OfType<ICronTrigger>().FirstOrDefault();
        var defaultCron = cronTrigger?.CronExpressionString ?? string.Empty;
        return Result<(IScheduler, string)>.Success((scheduler, defaultCron));
    }

    /// <summary>
    /// Re-schedules every existing cron trigger attached to the named job to fire on
    /// the supplied expression. Triggers that are not <see cref="ICronTrigger"/>
    /// (e.g. simple interval triggers) are skipped — the admin surface only manages
    /// cron-driven cadence.
    /// </summary>
    /// <param name="scheduler">Running Quartz scheduler.</param>
    /// <param name="jobCode">Stable job code.</param>
    /// <param name="newCron">New cron expression (already validated).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when every cron trigger has been re-scheduled.</returns>
    internal static async Task ApplyCronToSchedulerAsync(
        IScheduler scheduler,
        string jobCode,
        string newCron,
        CancellationToken cancellationToken)
    {
        var key = new JobKey(jobCode);
        var triggers = await scheduler.GetTriggersOfJob(key, cancellationToken).ConfigureAwait(false);
        foreach (var trigger in triggers)
        {
            if (trigger is not ICronTrigger oldCron) continue;
            var rebuilt = (ICronTrigger)oldCron.GetTriggerBuilder()
                .WithCronSchedule(newCron)
                .Build();
            await scheduler.RescheduleJob(oldCron.Key, rebuilt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes a single audit row with a serialised details payload.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected override row.</param>
    /// <param name="details">Anonymous object serialised to JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when the audit row is enqueued.</returns>
    private async Task EmitAuditAsync(
        string eventCode,
        string actor,
        long targetEntityId,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode,
            AuditSeverity.Critical,
            actor,
            nameof(JobScheduleOverride),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an override row into its outbound DTO.</summary>
    /// <param name="o">Loaded override row.</param>
    /// <param name="defaultCron">Baked-in default cron for the same job code.</param>
    /// <returns>Populated DTO.</returns>
    private JobScheduleOverrideDto ToDto(JobScheduleOverride o, string defaultCron) => new(
        Id: _sqids.Encode(o.Id),
        JobCode: o.JobCode,
        CronExpression: o.CronExpression,
        DefaultCronExpression: defaultCron,
        IsPaused: o.IsPaused,
        IsOverridden: true,
        UpdatedAtUtc: o.UpdatedAtUtc ?? o.CreatedAtUtc,
        UpdatedByUserSqid: o.UpdatedByUserId is long uid ? _sqids.Encode(uid) : o.UpdatedBy);
}
