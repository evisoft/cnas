using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R1000..R1034 / TOR §3.2-Z — operator-facing recurrent-payment scheduler
/// driving the monthly state-support and similar monthly-allowance services
/// (3.2-Z Suport financiar de stat lunar). Hosts the schedule registry +
/// the daily run-due primitive that generates <c>MPayOrder</c> rows for
/// every due schedule and advances <see cref="RecurrentPaymentScheduleDto.NextPaymentDate"/>
/// per cadence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency contract.</b> A schedule is "due" iff its
/// <c>NextPaymentDate</c> is on or before the run date AND
/// <c>IsActive=true</c>. The run primitive advances <c>NextPaymentDate</c>
/// by one cadence step in the same <c>SaveChangesAsync</c> call that creates
/// the <c>MPayOrder</c>, so a re-run on the same day is a no-op.
/// </para>
/// <para>
/// <b>Suspension semantics.</b> <see cref="SuspendAsync"/> flips
/// <c>IsActive</c> to <c>false</c>; <see cref="ResumeAsync"/> flips it back.
/// The run primitive consults the flag on every fire so an in-flight
/// schedule paused mid-cycle is honoured on the next execution.
/// </para>
/// </remarks>
public interface IRecurrentPaymentSchedulerService
{
    /// <summary>Stable failure code: the requested schedule does not exist.</summary>
    public const string ScheduleNotFoundCode = "RECURRENT_PAYMENT.NOT_FOUND";

    /// <summary>Stable failure code: invalid lifecycle transition (e.g. suspending an already-suspended schedule).</summary>
    public const string InvalidTransitionCode = "RECURRENT_PAYMENT.INVALID_TRANSITION";

    /// <summary>Stable audit event code emitted when a schedule is created.</summary>
    public const string AuditCreated = "RECURRENT_PAYMENT.CREATED";

    /// <summary>Stable audit event code emitted when a schedule is dispatched (per run-due iteration).</summary>
    public const string AuditDispatched = "RECURRENT_PAYMENT.DISPATCHED";

    /// <summary>Stable audit event code emitted when a schedule is suspended.</summary>
    public const string AuditSuspended = "RECURRENT_PAYMENT.SUSPENDED";

    /// <summary>Stable audit event code emitted when a schedule is resumed.</summary>
    public const string AuditResumed = "RECURRENT_PAYMENT.RESUMED";

    /// <summary>
    /// Registers a new recurrent-payment schedule. Returns the persisted
    /// row as a DTO.
    /// </summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted DTO on success.</returns>
    Task<Result<RecurrentPaymentScheduleDto>> CreateAsync(
        RecurrentPaymentScheduleCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans every Active schedule with <c>NextPaymentDate</c> ≤ today and
    /// generates an <c>MPayOrder</c> per due row, advancing
    /// <c>NextPaymentDate</c> by one cadence step.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of schedules dispatched on success.</returns>
    Task<Result<int>> RunDueAsync(CancellationToken cancellationToken = default);

    /// <summary>Suspends an Active schedule. Idempotent fail-on-noop.</summary>
    /// <param name="scheduleSqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<RecurrentPaymentScheduleDto>> SuspendAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Resumes a suspended schedule.</summary>
    /// <param name="scheduleSqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<RecurrentPaymentScheduleDto>> ResumeAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Lists schedules ordered by NextPaymentDate.</summary>
    /// <param name="skip">Page offset (≥ 0).</param>
    /// <param name="take">Page size (1..100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<RecurrentPaymentSchedulePageDto>> ListAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a schedule by Sqid (sets <c>IsActive=false</c>).</summary>
    /// <param name="scheduleSqid">Sqid-encoded schedule id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success when the row was found and deactivated.</returns>
    Task<Result> DeleteAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default);
}
