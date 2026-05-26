using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — CRUD façade for ABAC rule sets and their child rules.
/// All mutations emit Critical audit rows and invalidate the
/// <see cref="IAbacRuleEvaluator"/> in-process parse cache so subsequent
/// evaluations see the updated rule set immediately.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sqid round-trip.</b> Every identifier crossing the boundary is
/// Sqid-encoded per CLAUDE.md RULE 3 — the service decodes them internally
/// before touching the DbContext.
/// </para>
/// <para>
/// <b>Parse-before-persist.</b> <see cref="AddRuleAsync"/> and
/// <see cref="ModifyRuleAsync"/> parse <c>ConditionExpression</c> through the
/// injected <see cref="IAbacExpressionParser"/> BEFORE inserting / updating
/// the row. A failing parse yields
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.AbacParseError"/>.
/// </para>
/// </remarks>
public interface IAbacRuleRegistryService
{
    /// <summary>Creates a new <see cref="Cnas.Ps.Core.Domain.AbacRuleSet"/>.</summary>
    /// <param name="input">Validated create envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The persisted DTO on success; structured failure otherwise.</returns>
    Task<Result<AbacRuleSetDto>> CreateRuleSetAsync(AbacRuleSetCreateInputDto input, CancellationToken ct = default);

    /// <summary>Modifies an existing rule set's metadata.</summary>
    /// <param name="sqid">Sqid-encoded rule-set id.</param>
    /// <param name="input">Validated modify envelope (only non-null fields are applied).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<AbacRuleSetDto>> ModifyRuleSetAsync(string sqid, AbacRuleSetModifyInputDto input, CancellationToken ct = default);

    /// <summary>Soft-deletes a rule set (flips <c>IsActive</c> to false).</summary>
    /// <param name="sqid">Sqid-encoded rule-set id.</param>
    /// <param name="input">Reason envelope (3..1000 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The updated DTO.</returns>
    Task<Result<AbacRuleSetDto>> DisableRuleSetAsync(string sqid, AbacRuleReasonInputDto input, CancellationToken ct = default);

    /// <summary>Re-activates a previously-disabled rule set.</summary>
    /// <param name="sqid">Sqid-encoded rule-set id.</param>
    /// <param name="input">Reason envelope (3..1000 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The updated DTO.</returns>
    Task<Result<AbacRuleSetDto>> EnableRuleSetAsync(string sqid, AbacRuleReasonInputDto input, CancellationToken ct = default);

    /// <summary>Appends a new rule to a rule set after parsing + validating its condition expression.</summary>
    /// <param name="ruleSetSqid">Sqid-encoded parent rule-set id.</param>
    /// <param name="input">Validated rule envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The persisted rule DTO on success.</returns>
    Task<Result<AbacRuleDto>> AddRuleAsync(string ruleSetSqid, AbacRuleInputDto input, CancellationToken ct = default);

    /// <summary>Updates a rule (order, effect, expression, description).</summary>
    /// <param name="ruleSqid">Sqid-encoded rule id.</param>
    /// <param name="input">Validated rule envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The updated rule DTO on success.</returns>
    Task<Result<AbacRuleDto>> ModifyRuleAsync(string ruleSqid, AbacRuleInputDto input, CancellationToken ct = default);

    /// <summary>Soft-deletes a rule.</summary>
    /// <param name="ruleSqid">Sqid-encoded rule id.</param>
    /// <param name="input">Reason envelope (3..1000 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The updated rule DTO.</returns>
    Task<Result<AbacRuleDto>> DisableRuleAsync(string ruleSqid, AbacRuleReasonInputDto input, CancellationToken ct = default);

    /// <summary>Re-activates a previously-disabled rule.</summary>
    /// <param name="ruleSqid">Sqid-encoded rule id.</param>
    /// <param name="input">Reason envelope (3..1000 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The updated rule DTO.</returns>
    Task<Result<AbacRuleDto>> EnableRuleAsync(string ruleSqid, AbacRuleReasonInputDto input, CancellationToken ct = default);

    /// <summary>
    /// Bulk-reorders the rules belonging to a single rule set atomically.
    /// </summary>
    /// <param name="ruleSetSqid">Sqid-encoded parent rule-set id.</param>
    /// <param name="ordering">New <c>OrderIndex</c> assignments keyed by rule Sqid.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The updated rule set DTO on success.</returns>
    Task<Result<AbacRuleSetDto>> ReorderRulesAsync(
        string ruleSetSqid,
        IReadOnlyList<AbacRuleReorderInputDto> ordering,
        CancellationToken ct = default);

    /// <summary>Fetches a rule set with its rules (sorted by <c>OrderIndex</c> ascending).</summary>
    /// <param name="sqid">Sqid-encoded rule-set id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO when found; <see cref="ErrorCodes.AbacNotFound"/> otherwise.</returns>
    Task<Result<AbacRuleSetDto>> GetRuleSetByIdAsync(string sqid, CancellationToken ct = default);

    /// <summary>Fetches a rule set by its stable policy name.</summary>
    /// <param name="policyName">The SCREAMING_SNAKE_CASE policy name.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO when found; <see cref="ErrorCodes.AbacNotFound"/> otherwise.</returns>
    Task<Result<AbacRuleSetDto>> GetRuleSetByPolicyNameAsync(string policyName, CancellationToken ct = default);

    /// <summary>Lists rule sets matching the supplied filter envelope.</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The paged DTO; never null.</returns>
    Task<Result<AbacRuleSetPageDto>> ListRuleSetsAsync(AbacRuleSetFilterDto filter, CancellationToken ct = default);

    /// <summary>Dry-runs the evaluator against a synthetic attribute payload.</summary>
    /// <param name="input">Validated test envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The decision envelope when the policy is known.</returns>
    Task<Result<AbacDecisionDto>> TestExpressionAsync(AbacExpressionTestInputDto input, CancellationToken ct = default);
}
