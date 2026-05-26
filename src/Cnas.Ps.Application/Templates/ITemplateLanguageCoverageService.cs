using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Templates;

/// <summary>
/// R2003 / R0133 — operational coverage layer on top of the
/// <c>ITemplateVariantService</c> registry (R0133). Reports which
/// <c>DocumentTemplate</c> rows are missing variants for any of the canonical
/// RO / EN / RU triple (or for any operator-supplied required-language set),
/// optionally persists each gap as a Critical-audited "finding", and lets
/// operators acknowledge findings once the translation is in flight or the
/// gap is by-design.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure-read vs persisted-run.</b>
/// <see cref="ComputeCoverageAsync"/> is a pure projection — no rows are
/// inserted, no audit events are emitted. The nightly Quartz job calls
/// <see cref="RecordCoverageRunAsync"/> which (a) runs the same projection
/// AND (b) inserts a deduped finding row per (TemplateId, MissingLanguage)
/// gap, emitting a Critical audit per new insertion. The two surfaces share
/// the same filter envelope and return the same report DTO so the admin
/// "preview the report" endpoint and the scan job stay in lock-step.
/// </para>
/// <para>
/// <b>Operator-owned data.</b> The service only DETECTS gaps. Filling the
/// missing RO / EN / RU template bodies remains operator work via
/// <c>ITemplateVariantService.UpsertAsync</c> + <c>ApproveAsync</c>. The
/// coverage layer is a closed-loop monitor — it tells operators what's
/// missing, persists the worklist, and emits the audit trail for forensic
/// review; it does NOT mint translations.
/// </para>
/// <para>
/// <b>Audit emission.</b>
/// <list type="bullet">
///   <item><see cref="RecordCoverageRunAsync"/> → <c>TEMPLATE.COVERAGE.GAP_DETECTED</c> (Critical), once per new finding inserted.</item>
///   <item><see cref="AcknowledgeFindingAsync"/> → <c>TEMPLATE.COVERAGE.GAP_ACKNOWLEDGED</c> (Critical).</item>
/// </list>
/// </para>
/// </remarks>
public interface ITemplateLanguageCoverageService
{
    /// <summary>
    /// Computes the coverage projection for the supplied filter without
    /// persisting any findings. Safe to call as often as needed from the
    /// admin dashboard — the projection is read-only.
    /// </summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the report on success;
    /// <see cref="ErrorCodes.ValidationFailed"/> when the filter fails the
    /// FluentValidation guard.
    /// </returns>
    Task<Result<TemplateLanguageCoverageReportDto>> ComputeCoverageAsync(
        TemplateLanguageCoverageFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the projection AND persists each (TemplateId, MissingLanguage)
    /// gap as a finding. Duplicate open findings are deduped on the
    /// filtered unique index so the second + later runs of the same gap
    /// return the existing row. Emits one Critical audit event per NEW
    /// finding inserted and increments the per-language gap counter.
    /// </summary>
    /// <param name="filter">Filter envelope (same shape as <see cref="ComputeCoverageAsync"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The same report shape as <see cref="ComputeCoverageAsync"/> on success.</returns>
    Task<Result<TemplateLanguageCoverageReportDto>> RecordCoverageRunAsync(
        TemplateLanguageCoverageFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges a persisted finding with an operator-supplied note.
    /// Emits a Critical audit event and increments the gap-acknowledged
    /// counter.
    /// </summary>
    /// <param name="findingSqid">Sqid-encoded id of the finding.</param>
    /// <param name="input">Acknowledgement payload (Note 3..1000 chars).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the updated DTO on
    /// success; <see cref="ErrorCodes.NotFound"/> when the sqid does not
    /// resolve; <see cref="ErrorCodes.Conflict"/> when the row is already
    /// acknowledged; <see cref="ErrorCodes.ValidationFailed"/> on validator
    /// failure; <see cref="ErrorCodes.InvalidSqid"/> on Sqid-decode failure.
    /// </returns>
    Task<Result<TemplateLanguageCoverageFindingDto>> AcknowledgeFindingAsync(
        string findingSqid,
        TemplateLanguageCoverageAcknowledgeInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists persisted coverage findings filtered by acknowledgement state
    /// and / or missing-language code, sorted by detection recency. Paged.
    /// </summary>
    /// <param name="filter">Filter envelope (page bounds 1..200).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the page envelope on
    /// success; <see cref="ErrorCodes.ValidationFailed"/> on validator
    /// failure.
    /// </returns>
    Task<Result<TemplateLanguageCoverageFindingPageDto>> ListFindingsAsync(
        TemplateLanguageCoverageFindingFilterDto filter,
        CancellationToken cancellationToken = default);
}
