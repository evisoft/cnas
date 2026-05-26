using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reports;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — façade for the CRUD half of the ad-hoc report
/// builder. Power users persist <see cref="ReportTemplateDto"/> rows (registry +
/// selected fields + QBE filter + ordering + optional group-by); the matching
/// <see cref="IReportEngine"/> then runs those templates and produces paged result
/// sets or exports.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sharing model.</b> Each template is owned by the caller that created it.
/// Non-owners receive READ access only when <see cref="ReportTemplateDto.IsShared"/>
/// is <c>true</c>; updates and deletes are owner-only (administrators are out of
/// scope for R0156 and may be layered on by a future ABAC pass).
/// </para>
/// <para>
/// <b>Stable error codes.</b>
/// <see cref="ErrorCodes.NotFound"/> — the template does not exist (or is soft-deleted).
/// <see cref="ErrorCodes.Forbidden"/> — the caller is not the owner.
/// <see cref="ErrorCodes.Unauthorized"/> — the caller is anonymous.
/// <see cref="ErrorCodes.Conflict"/> — a template with the same code already exists.
/// <see cref="ErrorCodes.ValidationFailed"/> — schema-level validation failed (field
/// not in registry, &gt;25 selected fields, etc.).
/// </para>
/// </remarks>
public interface IReportTemplateService
{
    /// <summary>
    /// Persists a new report template owned by the authenticated caller.
    /// </summary>
    /// <param name="input">Create payload; ownership is taken from the caller context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the persisted template (Sqid-encoded id assigned by the DB). On
    /// failure a <see cref="Result{T}.Failure"/> carrying one of the stable codes
    /// listed in the type-level remarks.
    /// </returns>
    Task<Result<ReportTemplateDto>> CreateAsync(ReportTemplateCreateDto input, CancellationToken ct = default);

    /// <summary>
    /// Updates every mutable field on a template. Only the owner may call.
    /// </summary>
    /// <param name="templateId">Internal id of the template to update.</param>
    /// <param name="input">Update payload — registry is immutable and ignored.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The refreshed template DTO, or a failure with a stable code.</returns>
    Task<Result<ReportTemplateDto>> UpdateAsync(long templateId, ReportTemplateUpdateDto input, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a template (flips <c>IsActive=false</c>) and emits an audit row.
    /// </summary>
    /// <param name="templateId">Internal id of the template to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or a failure with a stable code.</returns>
    Task<Result> DeleteAsync(long templateId, CancellationToken ct = default);

    /// <summary>
    /// Fetches a single template by internal id. Owners always read their own rows;
    /// non-owners may only read rows where <see cref="ReportTemplateDto.IsShared"/>
    /// is <c>true</c>.
    /// </summary>
    /// <param name="templateId">Internal id of the template.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The template DTO, or <c>null</c> when missing / forbidden.</returns>
    Task<ReportTemplateDto?> GetAsync(long templateId, CancellationToken ct = default);

    /// <summary>
    /// Lists every template the caller can access — own rows plus every shared row.
    /// Ordered by <see cref="ReportTemplateDto.Name"/> ascending for deterministic
    /// rendering in the picker drop-down.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The accessible templates; an empty list for anonymous callers.</returns>
    Task<IReadOnlyList<ReportTemplateDto>> ListAccessibleAsync(CancellationToken ct = default);
}
