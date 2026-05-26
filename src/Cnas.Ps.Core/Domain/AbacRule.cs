using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2271 / TOR SEC 025 — one rule of an <see cref="AbacRuleSet"/>. The substrate
/// walks the active rules in <see cref="OrderIndex"/> ASC order and returns the
/// <see cref="Effect"/> of the first rule whose
/// <see cref="ConditionExpression"/> evaluates to <c>true</c>. When NO rule
/// matches, the substrate falls back to
/// <see cref="AbacRuleSet.DefaultEffect"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safe-by-default failure mode.</b> If the expression fails to parse OR
/// throws at evaluation time, the rule is treated as "did not match" — not as
/// "match with Effect=Allow". This prevents a malformed rule from accidentally
/// granting access (CLAUDE.md discipline).
/// </para>
/// <para>
/// <b>Order is significant.</b> The first matching rule wins, so administrators
/// place specific Allow rules BEFORE catch-all Deny rules (or vice versa,
/// depending on the policy intent). The substrate exposes a bulk-reorder
/// service endpoint so operators can re-sequence atomically without temporarily
/// disabling rules.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// surrogate id is exposed via the admin surface as a Sqid (CLAUDE.md RULE 3 /
/// ARH 027).
/// </para>
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Critical, EventCodePrefix = "ABAC_RULE")]
public sealed class AbacRule : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="AbacRuleSet"/>.</summary>
    public long RuleSetId { get; set; }

    /// <summary>
    /// Numeric priority within the rule set; lower numbers evaluate first.
    /// 0..10000. A composite unique index over <c>(RuleSetId, OrderIndex)</c>
    /// filtered to <see cref="Cnas.Ps.Core.Domain.AuditableEntity.IsActive"/> =
    /// <c>true</c> prevents two active rules from sharing a slot.
    /// </summary>
    public int OrderIndex { get; set; }

    /// <summary>The verdict to apply when <see cref="ConditionExpression"/> matches.</summary>
    public AbacEffect Effect { get; set; }

    /// <summary>
    /// The textual condition expression in the ABAC safe sub-language. Parsed
    /// at registration time (the validator rejects malformed expressions) and
    /// at evaluation time (the parsed AST is cached in process memory keyed
    /// by rule id).
    /// </summary>
    public required string ConditionExpression { get; set; }

    /// <summary>Optional administrator-supplied description (≤ 500 chars).</summary>
    public string? Description { get; set; }

    /// <summary>Navigation back to the owning rule set.</summary>
    public AbacRuleSet? RuleSet { get; set; }
}
