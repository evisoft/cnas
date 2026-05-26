using System;

namespace Cnas.Ps.Application.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — declarative marker that ties an MVC controller or
/// action to a named ABAC rule set. The
/// <c>Cnas.Ps.Infrastructure.Authorization.AbacPolicyProvider</c> recognises
/// the synthetic policy name <c>abac:{<see cref="PolicyName"/>}</c> and
/// builds an authorization policy whose single requirement dispatches to the
/// <see cref="IAbacRuleEvaluator"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usage.</b> Apply the attribute alongside <c>[Authorize]</c> on the
/// controller / action; the framework treats <c>Policy = "abac:NAME"</c>
/// as the authorization policy, and <c>[AbacPolicy("NAME")]</c> is the
/// declarative sugar that makes the intent legible.
/// </para>
/// <para>
/// <b>Scope.</b> When applied at the controller class level the policy is
/// inherited by every action. Method-level annotations override the class-
/// level default for that action only. The substrate forbids more than one
/// <see cref="AbacPolicyAttribute"/> per target (<see cref="AttributeUsageAttribute.AllowMultiple"/>
/// = false) — a target needing multiple ABAC checks should combine them at
/// the rule-set level instead of stacking attributes.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AbacPolicyAttribute : Attribute
{
    /// <summary>
    /// Constructs the attribute with the supplied policy name. The name is
    /// validated against the SCREAMING_SNAKE_CASE shape at startup by the
    /// policy provider; an empty / null name will fail loudly.
    /// </summary>
    /// <param name="policyName">Stable rule-set policy name (e.g. <c>DOSSIER.READ</c>).</param>
    public AbacPolicyAttribute(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        PolicyName = policyName;
    }

    /// <summary>The rule-set policy name referenced by this attribute.</summary>
    public string PolicyName { get; }

    /// <summary>
    /// Synthetic policy-name prefix the
    /// <c>Cnas.Ps.Infrastructure.Authorization.AbacPolicyProvider</c>
    /// recognises. <c>abac:{policy}</c> dispatches to the ABAC evaluator;
    /// any other name falls through to the default policy provider.
    /// </summary>
    public const string PolicyNamePrefix = "abac:";

    /// <summary>
    /// Builds the full synthetic policy name for use in
    /// <c>[Authorize(Policy = …)]</c>.
    /// </summary>
    /// <returns>The string <c>abac:{<see cref="PolicyName"/>}</c>.</returns>
    public string ToPolicyName() => PolicyNamePrefix + PolicyName;
}
