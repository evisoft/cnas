using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R1906 / TOR Annex 6 — per-report distribution rules DTOs
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R1906 / TOR Annex 6 — projection of a
/// <c>ReportDistributionRule</c> as it leaves the system. Sqid-encoded
/// surrogate id; encrypted-at-rest recipient code is rendered post-
/// decryption with the <c>Internal</c> sensitivity label.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying rule row.</param>
/// <param name="ReportCode">Stable report code (e.g. <c>ACCESS_RIGHTS.FULL_MATRIX</c>).</param>
/// <param name="Channel">Stable enum-name representation of the channel.</param>
/// <param name="RecipientKind">Stable enum-name representation of the recipient kind.</param>
/// <param name="RecipientCode">Decrypted recipient code (email / group code / role code / user sqid / MNotify category).</param>
/// <param name="Format">Stable enum-name representation of the payload format.</param>
/// <param name="Priority">Stable enum-name representation of the delivery priority.</param>
/// <param name="IsActive">Soft-delete flag.</param>
/// <param name="EffectiveFrom">First date the rule applies (inclusive).</param>
/// <param name="EffectiveUntil">Last date the rule applies (inclusive); null when open-ended.</param>
/// <param name="CreatedAt">UTC timestamp the rule was first created.</param>
/// <param name="Notes">Operator-supplied free-form note.</param>
public sealed record ReportDistributionRuleDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ReportCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Channel,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RecipientKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RecipientCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Format,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Priority,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool IsActive,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly EffectiveFrom,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? EffectiveUntil,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime CreatedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes);

/// <summary>
/// R1906 / TOR Annex 6 — input payload to
/// <c>IReportDistributionService.CreateRuleAsync</c>. Carries every operator-
/// settable field of a new rule.
/// </summary>
/// <param name="ReportCode">Stable report code (validated against <c>^[A-Z][A-Z0-9_.]{1,63}$</c>).</param>
/// <param name="Channel">Channel enum-name.</param>
/// <param name="RecipientKind">Recipient-kind enum-name.</param>
/// <param name="RecipientCode">Address whose semantics depend on <paramref name="RecipientKind"/>.</param>
/// <param name="Format">Delivery-format enum-name.</param>
/// <param name="Priority">Delivery-priority enum-name.</param>
/// <param name="EffectiveFrom">First date the rule applies (inclusive).</param>
/// <param name="EffectiveUntil">Last date the rule applies; null for open-ended.</param>
/// <param name="Notes">Optional operator note (≤ 1000 chars).</param>
public sealed record ReportDistributionRuleCreateInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ReportCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Channel,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RecipientKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RecipientCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Format,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Priority,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly EffectiveFrom,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? EffectiveUntil,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes);

/// <summary>
/// R1906 / TOR Annex 6 — partial update payload to
/// <c>IReportDistributionService.ModifyRuleAsync</c>. Every operator-
/// settable field is nullable so the caller can specify only the columns
/// they want to change. <paramref name="ChangeReason"/> is mandatory.
/// </summary>
/// <param name="Channel">New channel; null = leave unchanged.</param>
/// <param name="RecipientKind">New recipient-kind; null = leave unchanged.</param>
/// <param name="RecipientCode">New recipient address; null = leave unchanged.</param>
/// <param name="Format">New delivery format; null = leave unchanged.</param>
/// <param name="Priority">New delivery priority; null = leave unchanged.</param>
/// <param name="EffectiveFrom">New effective-from date; null = leave unchanged.</param>
/// <param name="EffectiveUntil">New effective-until date; null = leave unchanged. Pass <see cref="DateOnly.MaxValue"/> to clear.</param>
/// <param name="Notes">New free-form note; null = leave unchanged.</param>
/// <param name="ChangeReason">Mandatory 3..500-char rationale recorded on the audit row.</param>
public sealed record ReportDistributionRuleModifyInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Channel,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? RecipientKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? RecipientCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Format,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Priority,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? EffectiveFrom,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? EffectiveUntil,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ChangeReason);

/// <summary>
/// R1906 / TOR Annex 6 — payload for the enable / disable / delete
/// transitions. Carries the operator's free-form rationale.
/// </summary>
/// <param name="Reason">Mandatory 3..500-char rationale recorded on the audit row.</param>
public sealed record ReportDistributionReasonInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R1906 / TOR Annex 6 — filter envelope for the list-rules endpoint.
/// Every column is optional; <c>Skip/Take</c> are bounded.
/// </summary>
/// <param name="ReportCode">Optional exact report-code filter.</param>
/// <param name="Channel">Optional channel enum-name filter.</param>
/// <param name="RecipientKind">Optional recipient-kind enum-name filter.</param>
/// <param name="IsActive">Optional active-flag filter (null = both).</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..200.</param>
public sealed record ReportDistributionRuleFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ReportCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Channel,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? RecipientKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    bool? IsActive = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 50);

/// <summary>
/// R1906 / TOR Annex 6 — paged response envelope for the list-rules endpoint.
/// </summary>
/// <param name="Items">Rules on the current page.</param>
/// <param name="Total">Total matching rules across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
public sealed record ReportDistributionRulePageDto(
    IReadOnlyList<ReportDistributionRuleDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take);

/// <summary>
/// R1906 / TOR Annex 6 — input payload to
/// <c>IReportDistributionDispatcher.DispatchAsync</c>. Describes the report
/// run whose results should be fanned out and the deep link / payload
/// metadata the channel handlers will render into their messages.
/// </summary>
/// <param name="ReportCode">Stable report code matched against the active rule set.</param>
/// <param name="ReportRunSqid">Sqid-encoded id of the report run being distributed.</param>
/// <param name="Format">Format of the payload produced by the report engine.</param>
/// <param name="ReportTitle">Display title used in the notification subject / body header.</param>
/// <param name="ReportSummary">Short summary embedded in the notification body (≤ 1000 chars).</param>
/// <param name="PayloadDownloadUrl">Deep link to the in-system viewer for the payload (≤ 500 chars).</param>
/// <param name="PayloadSize">Optional payload size in bytes — surfaced to the recipient.</param>
/// <param name="EvaluatedAt">UTC instant the report was generated; surfaced as "as of".</param>
public sealed record ReportDispatchInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ReportCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ReportRunSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Format,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ReportTitle,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ReportSummary,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string PayloadDownloadUrl,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long? PayloadSize,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime EvaluatedAt);

/// <summary>
/// R1906 / TOR Annex 6 — projection of a
/// <c>ReportDistributionDispatch</c> as it leaves the system.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the dispatch row.</param>
/// <param name="RuleSqid">Sqid-encoded id of the parent rule.</param>
/// <param name="ReportRunSqid">Sqid-encoded id of the report run that triggered the dispatch.</param>
/// <param name="Channel">Snapshotted channel enum-name at dispatch time.</param>
/// <param name="RecipientKind">Snapshotted recipient-kind enum-name at dispatch time.</param>
/// <param name="RecipientCode">Decrypted recipient code (PII for the EmailAddress case).</param>
/// <param name="Status">Terminal status enum-name.</param>
/// <param name="DispatchedAt">UTC instant the dispatch was attempted.</param>
/// <param name="DeliveredAt">UTC instant the channel confirmed delivery; null for non-success.</param>
/// <param name="FailureReason">Sanitised reason populated for <c>Failed</c> / <c>Skipped</c>; never contains PII.</param>
/// <param name="RetryCount">Number of retry attempts; 0 on first-try terminals.</param>
public sealed record ReportDistributionDispatchDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RuleSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ReportRunSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Channel,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RecipientKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RecipientCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime DispatchedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? DeliveredAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FailureReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int RetryCount);

/// <summary>
/// R1906 / TOR Annex 6 — summary of one
/// <c>IReportDistributionDispatcher.DispatchAsync</c> invocation. Returned
/// to the caller so the upstream job can chart per-call distribution
/// outcomes without re-reading the dispatch table.
/// </summary>
/// <param name="TotalRules">Number of active matching rules consulted.</param>
/// <param name="Delivered">Number of rows that resulted in <c>Delivered</c>.</param>
/// <param name="Failed">Number of rows that resulted in <c>Failed</c>.</param>
/// <param name="Skipped">Number of rows that resulted in <c>Skipped</c>.</param>
public sealed record ReportDistributionDispatchSummaryDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalRules,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Delivered,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Failed,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skipped);

/// <summary>
/// R1906 / TOR Annex 6 — filter envelope for the list-dispatches endpoint.
/// </summary>
/// <param name="ReportRunSqid">Optional run-id filter — null returns all runs.</param>
/// <param name="Status">Optional status enum-name filter.</param>
/// <param name="RuleSqid">Optional rule-id filter — null returns all rules.</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..200.</param>
public sealed record ReportDispatchFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ReportRunSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? RuleSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 50);

/// <summary>
/// R1906 / TOR Annex 6 — paged response envelope for the list-dispatches endpoint.
/// </summary>
/// <param name="Items">Dispatches on the current page.</param>
/// <param name="Total">Total matching dispatches across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
public sealed record ReportDispatchPageDto(
    IReadOnlyList<ReportDistributionDispatchDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take);
