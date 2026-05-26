using System.Collections.Generic;
using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2271 / TOR SEC 025 — ordered container of attribute-based access-control rules
/// keyed by a stable policy name. The policy name is the contract surface that
/// <c>[AbacPolicy("…")]</c> annotations and the
/// <c>Cnas.Ps.Infrastructure.Authorization.AbacPolicyProvider</c> reference at
/// runtime; the rule set itself can evolve (add, remove, re-order rules) without
/// touching the controller annotations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why declarative.</b> RBAC alone cannot answer "may THIS user read THIS
/// resource right now" because the verdict depends on subject + resource + context
/// attributes (region match, document clearance level, business hours). The ABAC
/// engine evaluates a textual condition expression over an
/// <c>AbacEvaluationContext</c> at request time and returns the first matching
/// rule's <see cref="AbacEffect"/>. Operators can iterate on policies without
/// shipping a new binary.
/// </para>
/// <para>
/// <b>Default effect.</b> When NO rule in the set matches, the substrate falls
/// back to <see cref="DefaultEffect"/>. Production policies set this to
/// <see cref="AbacEffect.Deny"/> so a missing rule means "no access" rather than
/// the dangerous "everyone may pass" interpretation.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the surrogate
/// id crosses the boundary on the admin REST surface as a Sqid (CLAUDE.md
/// RULE 3 / ARH 027).
/// </para>
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Critical, EventCodePrefix = "ABAC_RULESET")]
public sealed class AbacRuleSet : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable SCREAMING_SNAKE_CASE identifier that <c>[AbacPolicy("…")]</c>
    /// references. Validated against the regex
    /// <c>^[A-Z][A-Z0-9_.]{1,63}$</c> at the service boundary and unique across
    /// the table (a duplicate policy name is rejected with
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.Conflict"/>).
    /// </summary>
    public required string PolicyName { get; set; }

    /// <summary>Short human-readable label used by the admin UI listings.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Optional long-form description (≤ 1000 chars).</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Verdict applied when no rule in the set matches. Defaults to
    /// <see cref="AbacEffect.Deny"/> — secure by default.
    /// </summary>
    public AbacEffect DefaultEffect { get; set; } = AbacEffect.Deny;

    /// <summary>Raw <c>UserProfile.Id</c> of the operator that originally registered this set.</summary>
    public long RegisteredByUserId { get; set; }

    /// <summary>
    /// Navigation collection of the rules belonging to this set. Loaded in
    /// <see cref="AbacRule.OrderIndex"/> ascending order when the substrate
    /// evaluates a decision.
    /// </summary>
    public ICollection<AbacRule> Rules { get; set; } = new List<AbacRule>();
}
