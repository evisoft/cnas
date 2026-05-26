using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// EF Core + Quartz <c>ISchedulerFactory</c>-backed implementation of
/// <see cref="IFailedJobStore"/>. Owns the persistence and the replay-scheduling
/// surface for the dead-letter queue (CLAUDE.md §6.2).
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: registered as <c>Scoped</c> because the EF Core <see cref="ICnasDbContext"/>
/// is scoped and tracks per-request changes. The <see cref="ISchedulerFactory"/> is
/// resolved from DI on each replay call so the store does not own scheduler lifecycle.
/// </para>
/// <para>
/// Logging discipline: the store NEVER logs the full <see cref="FailedJob.StackTrace"/>
/// at <c>Information</c> level — stack traces captured at the moment of failure may
/// contain PII inside parameter names, exception messages, or local-variable dumps. Use
/// the <see cref="QueryAsync"/> surface (which already gates on admin authorization at
/// the controller layer) when an operator legitimately needs to inspect a trace.
/// </para>
/// </remarks>
public sealed class FailedJobStore(
    ICnasDbContext db,
    ISchedulerFactory schedulerFactory,
    ICnasTimeProvider clock,
    ISqidService sqids,
    ILogger<FailedJobStore> logger) : IFailedJobStore
{
    private readonly ICnasDbContext _db = db;
    private readonly ISchedulerFactory _schedulerFactory = schedulerFactory;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly ILogger<FailedJobStore> _logger = logger;

    /// <inheritdoc />
    public async Task<Result> RecordFailureAsync(FailedJob entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // The listener stamps FailedAtUtc and CreatedAtUtc; we don't re-stamp here so the
        // listener can capture the exact moment Quartz raised the exception (which may
        // differ from the time we make it to SaveChanges by a few milliseconds).
        _db.FailedJobs.Add(entry);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // INFO level — no exception message or stack trace included, just the metadata
        // an operator needs to find the row. PII safety: JobName is a constant Quartz key
        // (e.g. "mpay-dispatcher"), never user data.
        _logger.LogInformation(
            "FailedJob recorded for JobName={JobName} at {FailedAtUtc:o} (RefireCount={RefireCount}).",
            entry.JobName, entry.FailedAtUtc, entry.RefireCount);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<FailedJobOutput>>> QueryAsync(
        string? jobName,
        DateTime? since,
        PageRequest page,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var pageSize = Math.Clamp(page.PageSize, 1, 200);
        var skip = Math.Max(0, page.Page - 1) * pageSize;

        // Start with the soft-delete-honouring query (IsActive == true is implicit on the
        // entity), then layer on the optional filters. Materialising the ORDER BY first so
        // both COUNT() and the page projection see the same predicate set.
        IQueryable<FailedJob> query = _db.FailedJobs.Where(f => f.IsActive);
        if (!string.IsNullOrWhiteSpace(jobName))
        {
            query = query.Where(f => f.JobName == jobName);
        }
        if (since is DateTime sinceValue)
        {
            query = query.Where(f => f.FailedAtUtc >= sinceValue);
        }

        var ordered = query.OrderByDescending(f => f.FailedAtUtc);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);

        // Project to entities first, then encode Sqids on the materialised list — Sqid
        // encoding is not translatable to SQL and pushing it into the IQueryable would
        // throw at runtime.
        var rows = await ordered
            .Skip(skip).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        var dtos = rows
            .Select(f => new FailedJobOutput(
                _sqids.Encode(f.Id),
                f.JobName,
                f.JobGroup,
                f.FailedAtUtc,
                f.ExceptionType,
                f.ExceptionMessage,
                f.StackTrace,
                f.RefireCount,
                f.ReplayState,
                f.LastReplayAtUtc))
            .ToList();

        return Result<PagedResult<FailedJobOutput>>.Success(
            new PagedResult<FailedJobOutput>(dtos, page.Page, pageSize, total));
    }

    /// <inheritdoc />
    public async Task<Result> ReplayAsync(string sqid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqid);

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var id = decoded.Value;

        var entry = await _db.FailedJobs.SingleOrDefaultAsync(f => f.Id == id, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "FailedJob entry not found.");
        }

        // Re-hydrate the JobDataMap from JSON. The listener already stripped PII before
        // serialising, so what we read back is the safe-to-replay shape. We tolerate a
        // null or empty payload — the job will receive an empty data map.
        var dataMap = new JobDataMap();
        if (!string.IsNullOrWhiteSpace(entry.JobDataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(entry.JobDataJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        // JobDataMap is a string-keyed dictionary of objects; we forward the
                        // raw text of each value so primitive types round-trip without
                        // implying type information we never preserved on the inbound side.
                        dataMap.Put(prop.Name, prop.Value.ToString());
                    }
                }
            }
            catch (JsonException)
            {
                // Corrupted payload — proceed with an empty map so the replay still fires.
                // The DLQ entry records the original data; an operator can correct it.
                _logger.LogWarning(
                    "FailedJobStore: JobDataJson on FailedJobId={Id} is unparseable; replaying with empty data map.",
                    entry.Id);
            }
        }

        // One-shot trigger fired immediately. The trigger group lives in a dedicated
        // namespace so an operator can quickly identify replay triggers in the Quartz
        // tables and so they cannot collide with the regular schedule trigger names.
        var jobKey = new JobKey(entry.JobName, entry.JobGroup);
        var now = _clock.UtcNow;
        var triggerKey = new TriggerKey($"replay-{entry.Id}-{now:yyyyMMddHHmmssfff}", "dlq-replay");
        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .UsingJobData(dataMap)
            .StartNow()
            .Build();

        var scheduler = await _schedulerFactory.GetScheduler(ct).ConfigureAwait(false);
        if (!await scheduler.CheckExists(jobKey, ct).ConfigureAwait(false))
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"Quartz job '{entry.JobName}' (group='{entry.JobGroup}') is not registered with the scheduler.");
        }
        await scheduler.ScheduleJob(trigger, ct).ConfigureAwait(false);

        entry.ReplayState = "scheduled";
        entry.LastReplayAtUtc = now;
        entry.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "FailedJob {Id} replayed for JobName={JobName} (TriggerKey={TriggerKey}).",
            entry.Id, entry.JobName, triggerKey);

        return Result.Success();
    }
}
