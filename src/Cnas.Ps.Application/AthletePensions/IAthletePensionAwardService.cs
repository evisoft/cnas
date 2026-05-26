using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.AthletePensions;

/// <summary>
/// R1403 / TOR §3.6-D — service façade for the athlete-pension registry.
/// Owns the award lifecycle (create / add-record / verify-record / submit /
/// evaluate-eligibility / approve / reject / activate / suspend / resume /
/// terminate) plus the lookup / listing surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b> Every successful mutation emits a stable audit
/// event at <c>AuditSeverity.Critical</c> severity (PII / financial
/// data — CLAUDE.md §5.6):
/// <list type="bullet">
///   <item><see cref="CreateAsync"/> → <c>ATHLETE_PENSION.CREATED</c>.</item>
///   <item><see cref="AddCareerRecordAsync"/> → <c>ATHLETE_PENSION.RECORD_ADDED</c>.</item>
///   <item><see cref="VerifyCareerRecordAsync"/> → <c>ATHLETE_PENSION.RECORD_VERIFIED</c>.</item>
///   <item><see cref="SubmitAsync"/> → <c>ATHLETE_PENSION.SUBMITTED</c>.</item>
///   <item><see cref="EvaluateEligibilityAsync"/> → <c>ATHLETE_PENSION.ELIGIBILITY_EVALUATED</c>.</item>
///   <item><see cref="ApproveAsync"/> → <c>ATHLETE_PENSION.APPROVED</c>.</item>
///   <item><see cref="RejectAsync"/> → <c>ATHLETE_PENSION.REJECTED</c>.</item>
///   <item><see cref="ActivateAsync"/> → <c>ATHLETE_PENSION.ACTIVATED</c>.</item>
///   <item><see cref="SuspendAsync"/> → <c>ATHLETE_PENSION.SUSPENDED</c>.</item>
///   <item><see cref="ResumeAsync"/> → <c>ATHLETE_PENSION.RESUMED</c>.</item>
///   <item><see cref="TerminateAsync"/> → <c>ATHLETE_PENSION.TERMINATED</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>State transitions.</b> Strict:
/// <c>Draft → Submitted</c>;
/// <c>Submitted → Approved | Rejected</c>;
/// <c>Approved → Active</c>;
/// <c>Active → Suspended | Terminated</c>;
/// <c>Suspended → Active | Terminated</c>.
/// Invalid transitions return <see cref="ErrorCodes.Conflict"/> with the
/// stable message <c>ATHLETE_PENSION.INVALID_TRANSITION</c>.
/// </para>
/// <para>
/// <b>Sqids everywhere.</b> Identifiers crossing the boundary are Sqid-
/// encoded per CLAUDE.md RULE 3; the service decodes them internally before
/// touching the DbContext. IDNP is NOT a Sqid — it's a stable external
/// identifier that the application encrypts at rest and never returns
/// plaintext on reads.
/// </para>
/// </remarks>
public interface IAthletePensionAwardService
{
    /// <summary>R1403 — opens a new award in <c>Draft</c>. Auto-generates the award number.</summary>
    /// <param name="input">Validated create envelope.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the persisted <see cref="AthletePensionAwardDto"/>; otherwise a typed failure.</returns>
    Task<Result<AthletePensionAwardDto>> CreateAsync(
        AthletePensionAwardCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1403 — adds an unverified career-record row to a Draft / Submitted award.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Validated record payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the refreshed award DTO; otherwise a typed failure.</returns>
    Task<Result<AthletePensionAwardDto>> AddCareerRecordAsync(
        string sqid,
        AthleteCareerRecordInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1403 — operator verifies a previously-added career-record row.</summary>
    /// <param name="awardSqid">Sqid-encoded award id.</param>
    /// <param name="recordSqid">Sqid-encoded record id.</param>
    /// <param name="input">Verification envelope (mandatory note).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the refreshed award DTO; otherwise a typed failure.</returns>
    Task<Result<AthletePensionAwardDto>> VerifyCareerRecordAsync(
        string awardSqid,
        string recordSqid,
        AthleteCareerRecordVerificationInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1403 — transitions <c>Draft</c> → <c>Submitted</c>.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the updated DTO; otherwise a typed failure.</returns>
    Task<Result<AthletePensionAwardDto>> SubmitAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>R1403 — evaluates eligibility without changing state (pure read + audit).</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the verdict DTO; otherwise a typed failure.</returns>
    Task<Result<AthletePensionEligibilityVerdictDto>> EvaluateEligibilityAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// R1403 — transitions <c>Submitted</c> → <c>Approved</c>. Requires a
    /// positive eligibility verdict; recomputes the monthly amount and
    /// snapshots the multiplier components on the row.
    /// </summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Approval envelope (mandatory note + regulatory base).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the updated DTO; otherwise a typed failure.</returns>
    Task<Result<AthletePensionAwardDto>> ApproveAsync(
        string sqid,
        AthletePensionApprovalInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1403 — transitions <c>Submitted</c> → <c>Rejected</c>.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the updated DTO; otherwise a typed failure.</returns>
    Task<Result<AthletePensionAwardDto>> RejectAsync(
        string sqid,
        AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1403 — transitions <c>Approved</c> → <c>Active</c>.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Activation envelope (effective-from + note).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the updated DTO; otherwise a typed failure.</returns>
    Task<Result<AthletePensionAwardDto>> ActivateAsync(
        string sqid,
        AthletePensionActivationInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1403 — transitions <c>Active</c> → <c>Suspended</c>.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the updated DTO; otherwise a typed failure.</returns>
    Task<Result<AthletePensionAwardDto>> SuspendAsync(
        string sqid,
        AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1403 — transitions <c>Suspended</c> → <c>Active</c>.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the updated DTO; otherwise a typed failure.</returns>
    Task<Result<AthletePensionAwardDto>> ResumeAsync(
        string sqid,
        AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1403 — transitions any non-terminal status → <c>Terminated</c>.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the updated DTO; otherwise a typed failure.</returns>
    Task<Result<AthletePensionAwardDto>> TerminateAsync(
        string sqid,
        AthletePensionReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>R1403 — fetches a single award by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded award id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the DTO; otherwise <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result<AthletePensionAwardDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>R1403 — paged list filtered by status / role / discipline / beneficiary hash.</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>On success the page DTO; on validation failure <see cref="ErrorCodes.ValidationFailed"/>.</returns>
    Task<Result<AthletePensionAwardPageDto>> ListAsync(
        AthletePensionAwardFilterDto filter,
        CancellationToken cancellationToken = default);
}
