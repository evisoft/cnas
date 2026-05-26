using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0141 / TOR CF 15.03 — per-passport business-rule DTOs surfaced by the
// admin business-rule editor. All Id fields are Sqid-encoded strings per
// CLAUDE.md RULE 3. The "id" string is opaque from the caller's perspective
// — the editor service derives it from a deterministic stable hash of the
// rule's payload so a rule survives across save round-trips without needing
// a separate DB table.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0141 / TOR CF 15.03 — applicant-type discriminator carried on every business
/// rule. Lets operators express rules that apply only to natural persons (citizens),
/// only to legal entities (employers / NGOs), or to both. Mirrors the TOR §15.03
/// requirement to filter the eligibility logic by applicant category.
/// </summary>
public enum BusinessRuleApplicantType
{
    /// <summary>Rule applies to natural persons (citizens / individual applicants).</summary>
    Natural = 0,

    /// <summary>Rule applies to legal entities (employers, NGOs, government bodies).</summary>
    Legal = 1,

    /// <summary>Rule applies to both natural persons and legal entities.</summary>
    Both = 2,
}

/// <summary>
/// R0141 / TOR CF 15.03 — decision outcome a business rule yields when its
/// condition evaluates to <see langword="true"/>. The runtime engine wires
/// these into the existing <c>IDecisionEngine</c> result via the editor's
/// translator.
/// </summary>
public enum BusinessRuleDecisionOutcome
{
    /// <summary>Grant the benefit.</summary>
    Granted = 0,

    /// <summary>Reject the application.</summary>
    Rejected = 1,

    /// <summary>Route the application to manual review by an examiner.</summary>
    RequiresReview = 2,
}

/// <summary>
/// R0141 / TOR CF 15.03 — outbound projection of one business rule within a
/// <c>ServicePassport</c>'s <c>DecisionRulesJson</c>. Each rule is uniquely
/// addressable inside the passport via <see cref="Id"/> (Sqid-shaped opaque
/// stable hash) so the editor can list / update / delete one at a time.
/// </summary>
/// <param name="Id">Opaque stable rule id within the passport (see remarks).</param>
/// <param name="Name">Operator-visible rule name (3..256 chars).</param>
/// <param name="ApplicantType">Applicant-type filter; <see cref="BusinessRuleApplicantType.Both"/> for global rules.</param>
/// <param name="ConditionJson">JSON object/array expressing the condition. Re-uses the
/// shape understood by the existing JSON-rules engine
/// (<c>{"rule": "fact-equals", "fact": "isInsured", "value": true, ...}</c>).</param>
/// <param name="DecisionOutcome">Outcome when the condition matches.</param>
/// <param name="Notes">Optional operator note (≤ 2000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BusinessRuleDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Name,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    BusinessRuleApplicantType ApplicantType,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ConditionJson,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    BusinessRuleDecisionOutcome DecisionOutcome,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Notes);

/// <summary>
/// R0141 / TOR CF 15.03 — input envelope used by the admin editor to upsert a
/// business rule. The <see cref="Id"/> field is null on create and carries the
/// existing rule's opaque id on update.
/// </summary>
/// <param name="Id">Existing rule id (null on create).</param>
/// <param name="Name">Operator-visible rule name (3..256 chars).</param>
/// <param name="ApplicantType">Applicant-type filter; <see cref="BusinessRuleApplicantType.Both"/> for global rules.</param>
/// <param name="ConditionJson">JSON object expressing the condition; validated by the engine parser before save.</param>
/// <param name="DecisionOutcome">Outcome when the condition matches.</param>
/// <param name="Notes">Optional operator note (≤ 2000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BusinessRuleInputDto(
    string? Id,
    string Name,
    BusinessRuleApplicantType ApplicantType,
    string ConditionJson,
    BusinessRuleDecisionOutcome DecisionOutcome,
    string? Notes);
