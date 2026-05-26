using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Scheduling;

/// <summary>
/// R0200 / TOR CF 20.01-03, MR 012 — admin-facing CRUD over the Quartz cron-override
/// registry. Lists every embedded Quartz job (with the effective cron — operator
/// override if present, baked-in default otherwise), upserts a new cron expression,
/// and pauses / resumes individual jobs. Every mutation emits a Critical-severity
/// audit row because changing a job's cadence is a SEC 042-class configuration event.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity model.</b> Each job is addressed by its stable Quartz <c>JobKey.Name</c>
/// (e.g. <c>mpay-dispatcher</c>, <c>mconnect-sync</c>) — NOT by a Sqid. Job codes are
/// stable public names and operators reference them in runbooks; Sqid-encoding would
/// obscure the very identifier the admin surface uses. The override row carries a
/// Sqid id as well for parity with the rest of the admin REST surface (CLAUDE.md
/// RULE 3 applies to row ids that cross the boundary, not to natural-key job codes).
/// </para>
/// <para>
/// <b>List semantics.</b> <see cref="ListAsync"/> returns one row per registered Quartz
/// job — including jobs that have NO override row yet. For those rows
/// <see cref="JobScheduleOverrideDto.Id"/> is <c>null</c> and
/// <see cref="JobScheduleOverrideDto.IsOverridden"/> is <c>false</c>; the
/// <see cref="JobScheduleOverrideDto.CronExpression"/> mirrors the baked-in default so
/// operators can see exactly what the scheduler will run with no further input.
/// </para>
/// <para>
/// <b>Validation.</b> Every cron expression is parsed by Quartz before persistence.
/// Malformed expressions surface as
/// <see cref="ErrorCodes.ValidationFailed"/> with a stable detail message; the
/// validator NEVER attempts to "repair" a bad expression — a wrong cron is rejected
/// loudly rather than silently rounded.
/// </para>
/// </remarks>
public interface ICronAdminService
{
    /// <summary>Stable failure code: the supplied cron expression is not valid Quartz syntax.</summary>
    public const string InvalidCronCode = "CRON.INVALID_EXPRESSION";

    /// <summary>Stable failure code: the supplied job code is not registered in the Quartz scheduler.</summary>
    public const string UnknownJobCode = "CRON.UNKNOWN_JOB_CODE";

    /// <summary>Stable audit event code emitted when a cron override is created or updated.</summary>
    public const string AuditCronUpserted = "CRON.SCHEDULE.UPSERTED";

    /// <summary>Stable audit event code emitted when a job is paused.</summary>
    public const string AuditCronPaused = "CRON.SCHEDULE.PAUSED";

    /// <summary>Stable audit event code emitted when a job is resumed.</summary>
    public const string AuditCronResumed = "CRON.SCHEDULE.RESUMED";

    /// <summary>
    /// Lists every registered Quartz job with its current effective cron expression.
    /// Rows that have no operator override carry <see cref="JobScheduleOverrideDto.Id"/>
    /// = <c>null</c> and <see cref="JobScheduleOverrideDto.IsOverridden"/> = <c>false</c>;
    /// rows that have one carry the override id + the operator-edited cron expression.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result wrapping the (possibly empty) list of job-schedule rows.</returns>
    Task<Result<IReadOnlyList<JobScheduleOverrideDto>>> ListAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the cron expression for the named Quartz job. Creates an override row on
    /// first call; updates the existing row on subsequent calls. The new cron value is
    /// validated through <c>Quartz.CronExpression.IsValidExpression</c> before persistence.
    /// Emits a Critical-severity <see cref="AuditCronUpserted"/> audit row on success.
    /// </summary>
    /// <param name="jobCode">Stable Quartz job code (NOT a Sqid).</param>
    /// <param name="input">Payload carrying the new cron expression.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<JobScheduleOverrideDto>> UpsertAsync(
        string jobCode,
        CronExpressionInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses the named Quartz job. The change is persisted to the override row (so the
    /// pause survives restarts) and applied to the running scheduler. Emits a Critical
    /// <see cref="AuditCronPaused"/> audit row on success.
    /// </summary>
    /// <param name="jobCode">Stable Quartz job code (NOT a Sqid).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<JobScheduleOverrideDto>> PauseAsync(
        string jobCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a previously paused Quartz job. The change is persisted to the override
    /// row (so the resume survives restarts) and applied to the running scheduler. Emits
    /// a Critical <see cref="AuditCronResumed"/> audit row on success.
    /// </summary>
    /// <param name="jobCode">Stable Quartz job code (NOT a Sqid).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<JobScheduleOverrideDto>> ResumeAsync(
        string jobCode,
        CancellationToken cancellationToken = default);
}
