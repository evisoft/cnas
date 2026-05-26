using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reporting;

/// <summary>
/// R1906 / TOR Annex 6 — administrative CRUD façade over the per-report
/// distribution-rule registry. Every mutation emits a Critical-severity
/// audit row (<c>REPORT_DIST.RULE_*</c>) so admin actions on the
/// distribution policy are end-to-end traceable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit codes.</b> The stable codes emitted by the implementation are
/// <c>REPORT_DIST.RULE_CREATED</c> / <c>REPORT_DIST.RULE_MODIFIED</c> /
/// <c>REPORT_DIST.RULE_DISABLED</c> / <c>REPORT_DIST.RULE_ENABLED</c> /
/// <c>REPORT_DIST.RULE_DELETED</c>. Each carries the rule's primary-key id
/// + the channel + recipient-kind in its <c>detailsJson</c>; recipient
/// addresses are NEVER logged verbatim — they remain encrypted at rest and
/// are referenced by id only.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> Every <c>sqid</c> parameter is decoded at the
/// service layer before reaching the DbContext; outbound DTOs carry
/// Sqid-encoded surrogate ids per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public interface IReportDistributionService
{
    /// <summary>
    /// Creates a new distribution rule. Validates the input through
    /// <c>ReportDistributionRuleCreateInputValidator</c>, encrypts the
    /// recipient code at rest, computes the email-only hash shadow for
    /// equality lookups, and persists the row. Emits the
    /// <c>REPORT_DIST.RULE_CREATED</c> audit row.
    /// </summary>
    /// <param name="input">Operator-supplied input payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the created rule's DTO on success;
    /// <see cref="ErrorCodes.ValidationFailed"/> on input failure;
    /// <see cref="ErrorCodes.Conflict"/> when the (report, channel, kind, recipient)
    /// tuple is already registered as an active rule.
    /// </returns>
    Task<Result<ReportDistributionRuleDto>> CreateRuleAsync(
        ReportDistributionRuleCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Modifies an existing rule. Only the non-null fields on
    /// <paramref name="input"/> are applied. Emits
    /// <c>REPORT_DIST.RULE_MODIFIED</c>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Partial-update payload (mandatory <c>ChangeReason</c>).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The updated DTO on success; <c>NOT_FOUND</c> / <c>INVALID_SQID</c> on the obvious failures.</returns>
    Task<Result<ReportDistributionRuleDto>> ModifyRuleAsync(
        string sqid,
        ReportDistributionRuleModifyInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables an active rule (sets <c>IsActive=false</c> on a soft-deleted-but-keep-row
    /// row, OR sets a sentinel on the rule's lifecycle column — implementation choice).
    /// Emits <c>REPORT_DIST.RULE_DISABLED</c>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Mandatory reason payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ReportDistributionRuleDto>> DisableRuleAsync(
        string sqid,
        ReportDistributionReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-enables a previously-disabled rule. Emits <c>REPORT_DIST.RULE_ENABLED</c>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Mandatory reason payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<ReportDistributionRuleDto>> EnableRuleAsync(
        string sqid,
        ReportDistributionReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a rule (flips <see cref="Cnas.Ps.Core.Domain.AuditableEntity.IsActive"/>
    /// to <c>false</c> and stamps update metadata). Emits <c>REPORT_DIST.RULE_DELETED</c>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="input">Mandatory reason payload.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Success on the happy path; <c>NOT_FOUND</c> when the row is missing.</returns>
    Task<Result> DeleteRuleAsync(
        string sqid,
        ReportDistributionReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches one rule by its Sqid. <c>NOT_FOUND</c> when missing or soft-deleted.
    /// </summary>
    /// <param name="sqid">Sqid-encoded rule id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<ReportDistributionRuleDto>> GetRuleByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists rules with optional filters and paging. Returns soft-deleted rows ONLY when
    /// <c>filter.IsActive == false</c> is explicitly specified.
    /// </summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The paged result.</returns>
    Task<Result<ReportDistributionRulePageDto>> ListRulesAsync(
        ReportDistributionRuleFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists dispatch attempts with optional filters and paging.
    /// </summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The paged result.</returns>
    Task<Result<ReportDispatchPageDto>> ListDispatchesAsync(
        ReportDispatchFilterDto filter,
        CancellationToken cancellationToken = default);
}
