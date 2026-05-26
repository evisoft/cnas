using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2271 / TOR SEC 025 — Attribute-based access-control engine DTOs
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R2271 / TOR SEC 025 — projection of an ABAC rule set as it leaves the system.
/// Carries the ordered active rule list inline (typical rule sets are small —
/// dozens of rules at most — so paging the children is unnecessary).
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the rule set.</param>
/// <param name="PolicyName">Stable SCREAMING_SNAKE_CASE policy name referenced by <c>[AbacPolicy("…")]</c>.</param>
/// <param name="DisplayName">Short human-readable label.</param>
/// <param name="Description">Optional long-form description.</param>
/// <param name="DefaultEffect">Stable enum-name (Allow / Deny) applied when no rule matches.</param>
/// <param name="IsActive">Soft-delete flag — false means the substrate ignores the set.</param>
/// <param name="Rules">Rules ordered by <c>OrderIndex</c> ascending.</param>
public sealed record AbacRuleSetDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string PolicyName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DefaultEffect,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsActive,
    IReadOnlyList<AbacRuleDto> Rules);

/// <summary>
/// R2271 / TOR SEC 025 — projection of a single ABAC rule. The condition
/// expression is classified <see cref="SensitivityLabel.Internal"/> because it
/// is policy text, not subject data.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the rule.</param>
/// <param name="RuleSetSqid">Sqid-encoded parent rule-set id.</param>
/// <param name="OrderIndex">Priority within the rule set; lower evaluates first.</param>
/// <param name="Effect">Stable enum-name (Allow / Deny) applied when the condition matches.</param>
/// <param name="ConditionExpression">The textual ABAC condition expression.</param>
/// <param name="Description">Optional administrator-supplied description.</param>
/// <param name="IsActive">Soft-delete flag — false means the substrate ignores the rule.</param>
public sealed record AbacRuleDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RuleSetSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int OrderIndex,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Effect,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ConditionExpression,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsActive);

/// <summary>
/// R2271 / TOR SEC 025 — input envelope for creating a new ABAC rule set. The
/// rule set is created empty; rules are added by subsequent calls to
/// <c>AddRuleAsync</c>.
/// </summary>
/// <param name="PolicyName">Stable SCREAMING_SNAKE_CASE policy name. Required; max 64 chars.</param>
/// <param name="DisplayName">Short human-readable label. Required; 3..256 chars.</param>
/// <param name="Description">Optional long-form description (≤ 1000 chars).</param>
/// <param name="DefaultEffect">Stable enum-name (Allow / Deny). Defaults to Deny on the server when null.</param>
public sealed record AbacRuleSetCreateInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string PolicyName,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? DefaultEffect);

/// <summary>
/// R2271 / TOR SEC 025 — input envelope for modifying an existing rule set's
/// metadata. Every field is optional — null means "leave unchanged".
/// <see cref="ChangeReason"/> is mandatory so the audit row always carries a
/// human-readable "why".
/// </summary>
/// <param name="DisplayName">New display name; null leaves the existing value.</param>
/// <param name="Description">New description; null leaves the existing value.</param>
/// <param name="DefaultEffect">New default effect; null leaves the existing value.</param>
/// <param name="ChangeReason">Mandatory rationale for the change (3..1000 chars).</param>
public sealed record AbacRuleSetModifyInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? DefaultEffect,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ChangeReason);

/// <summary>
/// R2271 / TOR SEC 025 — input envelope for creating or updating a single ABAC rule.
/// </summary>
/// <param name="OrderIndex">Priority within the rule set; 0..10000.</param>
/// <param name="Effect">Stable enum-name (Allow / Deny).</param>
/// <param name="ConditionExpression">The textual ABAC condition expression. Parsed at validation time.</param>
/// <param name="Description">Optional administrator-supplied description (≤ 500 chars).</param>
public sealed record AbacRuleInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int OrderIndex,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Effect,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ConditionExpression,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description);

/// <summary>
/// R2271 / TOR SEC 025 — single entry in a bulk-reorder payload.
/// </summary>
/// <param name="RuleSqid">Sqid-encoded rule id to re-position.</param>
/// <param name="NewOrderIndex">New priority within the rule set; 0..10000.</param>
public sealed record AbacRuleReorderInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RuleSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int NewOrderIndex);

/// <summary>
/// R2271 / TOR SEC 025 — reason envelope used by disable / enable endpoints.
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..1000 chars).</param>
public sealed record AbacRuleReasonInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R2271 / TOR SEC 025 — payload accepted by the dry-run test endpoint. Allows
/// administrators to evaluate a rule set against a synthetic attribute payload
/// before going live.
/// </summary>
/// <param name="PolicyName">Stable SCREAMING_SNAKE_CASE policy name to dispatch against.</param>
/// <param name="Subject">Subject-attribute dictionary (≤ 64 keys).</param>
/// <param name="Resource">Resource-attribute dictionary (≤ 64 keys).</param>
/// <param name="Environment">Environment-attribute dictionary (≤ 64 keys).</param>
/// <param name="Action">Action-attribute dictionary (≤ 64 keys).</param>
public sealed record AbacExpressionTestInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string PolicyName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    Dictionary<string, object?> Subject,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    Dictionary<string, object?> Resource,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    Dictionary<string, object?> Environment,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    Dictionary<string, object?> Action);

/// <summary>
/// R2271 / TOR SEC 025 — verdict envelope returned by the ABAC evaluator. Carries
/// the matched rule (when any) and a structured trace so administrators can debug
/// why a particular rule did or did not match.
/// </summary>
/// <param name="Effect">Stable enum-name of the final effect (Allow / Deny).</param>
/// <param name="MatchedRuleSqid">Sqid-encoded id of the rule that matched, or null when none did.</param>
/// <param name="MatchedRuleOrderIndex">Order index of the matched rule, or null.</param>
/// <param name="EvaluationTraceJson">JSON array of <c>{ ruleSqid, orderIndex, matched, errorIfAny }</c>.</param>
public sealed record AbacDecisionDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Effect,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? MatchedRuleSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int? MatchedRuleOrderIndex,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string EvaluationTraceJson);

/// <summary>
/// R2271 / TOR SEC 025 — filter envelope for the rule-set list endpoint.
/// </summary>
/// <param name="PolicyName">Optional policy-name prefix filter.</param>
/// <param name="IsActive">Optional soft-delete-state filter.</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..100.</param>
public sealed record AbacRuleSetFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? PolicyName = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool? IsActive = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 25);

/// <summary>
/// R2271 / TOR SEC 025 — paged response envelope for the rule-set list endpoint.
/// </summary>
/// <param name="Items">Rule-set page.</param>
/// <param name="Total">Total matching rows across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
public sealed record AbacRuleSetPageDto(
    IReadOnlyList<AbacRuleSetDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take);
