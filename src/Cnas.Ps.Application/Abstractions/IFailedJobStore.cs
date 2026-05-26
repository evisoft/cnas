using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// Persistence + replay façade over the <see cref="FailedJob"/> dead-letter queue
/// (CLAUDE.md §6.2). Implemented in Infrastructure (<c>FailedJobStore</c>) and consumed
/// by both the Quartz <c>FailedJobListener</c> (writes) and the admin replay endpoint
/// (reads + replays).
/// </summary>
/// <remarks>
/// Splitting "the listener" from "the store" keeps the listener tightly scoped (capture
/// what failed) and lets the store own the EF Core, scheduler, and clock plumbing in
/// one place. The store is the only component that re-schedules Quartz jobs from a DLQ
/// row — no other surface area touches <c>ISchedulerFactory</c> for this purpose.
/// </remarks>
public interface IFailedJobStore
{
    /// <summary>
    /// Persists a DLQ entry. The caller (typically <c>FailedJobListener</c>) is
    /// responsible for PII-scrubbing the <see cref="FailedJob.JobDataJson"/>
    /// payload BEFORE invoking this method — the store does not redact.
    /// </summary>
    /// <param name="entry">Fully-populated entry. <c>FailedAtUtc</c>, <c>JobName</c>, and the exception fields are required.</param>
    /// <param name="ct">Cancellation token plumbed through to <c>SaveChangesAsync</c>.</param>
    /// <returns>A successful result on persist; only exceptional failures (DB outage) propagate.</returns>
    Task<Result> RecordFailureAsync(FailedJob entry, CancellationToken ct = default);

    /// <summary>
    /// Paged query over DLQ entries. Default ordering is newest-first by
    /// <see cref="FailedJob.FailedAtUtc"/>, which matches what an operator wants to see
    /// when triaging a recent incident.
    /// </summary>
    /// <param name="jobName">When non-null, restricts the result set to entries matching this Quartz job name.</param>
    /// <param name="since">When non-null, restricts the result set to entries with <c>FailedAtUtc &gt;= since</c>.</param>
    /// <param name="page">Pagination request — page size is clamped to [1, 200].</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Page of output DTOs with Sqid-encoded ids (RULE 3).</returns>
    Task<Result<PagedResult<FailedJobOutput>>> QueryAsync(
        string? jobName,
        DateTime? since,
        PageRequest page,
        CancellationToken ct = default);

    /// <summary>
    /// Replays a DLQ entry by scheduling a one-shot Quartz fire of the original job key.
    /// The original <c>JobDataMap</c> (already PII-scrubbed) is parsed back from
    /// <see cref="FailedJob.JobDataJson"/> and forwarded as the new fire's data map.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the DLQ entry as it appears on the API surface.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>Result.Success</c> when the replay was scheduled and the DLQ row was stamped;
    /// failure with <see cref="ErrorCodes.NotFound"/> when no entry matches
    /// <paramref name="sqid"/>; failure with <see cref="ErrorCodes.InvalidSqid"/> when
    /// the id is malformed.
    /// </returns>
    Task<Result> ReplayAsync(string sqid, CancellationToken ct = default);
}
