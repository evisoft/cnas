using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abac;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — production implementation of
/// <see cref="IAbacRuleRegistryService"/>. CRUD over
/// <see cref="AbacRuleSet"/> / <see cref="AbacRule"/> with Critical audit on
/// every mutation and evaluator-cache invalidation on every persisted write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parse-before-persist.</b> <see cref="AddRuleAsync"/> and
/// <see cref="ModifyRuleAsync"/> route the candidate expression through the
/// injected <see cref="IAbacExpressionParser"/> BEFORE inserting / updating
/// the row. A failing parse yields
/// <see cref="ErrorCodes.AbacParseError"/> with the parser's diagnostic.
/// </para>
/// <para>
/// <b>PII discipline.</b> The audit payload records the caller's Sqid and the
/// rule-set policy name only — never the raw <see cref="AbacRule.ConditionExpression"/>
/// (operator-supplied policy text could theoretically reference attribute keys
/// that are themselves sensitive).
/// </para>
/// </remarks>
public sealed class AbacRuleRegistryService : IAbacRuleRegistryService
{
    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IAbacExpressionParser _parser;
    private readonly IAbacRuleEvaluator _evaluator;
    private readonly IValidator<AbacRuleSetCreateInputDto> _createValidator;
    private readonly IValidator<AbacRuleSetModifyInputDto> _modifyValidator;
    private readonly IValidator<AbacRuleInputDto> _ruleValidator;
    private readonly IValidator<AbacRuleReorderInputDto> _reorderValidator;
    private readonly IValidator<AbacRuleSetFilterDto> _filterValidator;
    private readonly IValidator<AbacRuleReasonInputDto> _reasonValidator;
    private readonly IValidator<AbacExpressionTestInputDto> _testValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer DB context.</param>
    /// <param name="sqids">Sqid encoder/decoder for boundary id translation.</param>
    /// <param name="clock">UTC clock abstraction (CLAUDE.md RULE 4).</param>
    /// <param name="caller">Caller context (operator id source).</param>
    /// <param name="audit">Audit service — emits Critical lifecycle rows.</param>
    /// <param name="parser">ABAC expression parser.</param>
    /// <param name="evaluator">ABAC rule evaluator (used to invalidate cache + dry-run).</param>
    /// <param name="createValidator">Validator for the create envelope.</param>
    /// <param name="modifyValidator">Validator for the modify envelope.</param>
    /// <param name="ruleValidator">Validator for individual rules.</param>
    /// <param name="reorderValidator">Validator for reorder entries.</param>
    /// <param name="filterValidator">Validator for the list filter.</param>
    /// <param name="reasonValidator">Validator for disable / enable reasons.</param>
    /// <param name="testValidator">Validator for the dry-run test envelope.</param>
    public AbacRuleRegistryService(
        ICnasDbContext db,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService audit,
        IAbacExpressionParser parser,
        IAbacRuleEvaluator evaluator,
        IValidator<AbacRuleSetCreateInputDto> createValidator,
        IValidator<AbacRuleSetModifyInputDto> modifyValidator,
        IValidator<AbacRuleInputDto> ruleValidator,
        IValidator<AbacRuleReorderInputDto> reorderValidator,
        IValidator<AbacRuleSetFilterDto> filterValidator,
        IValidator<AbacRuleReasonInputDto> reasonValidator,
        IValidator<AbacExpressionTestInputDto> testValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(ruleValidator);
        ArgumentNullException.ThrowIfNull(reorderValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(testValidator);
        _db = db;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _audit = audit;
        _parser = parser;
        _evaluator = evaluator;
        _createValidator = createValidator;
        _modifyValidator = modifyValidator;
        _ruleValidator = ruleValidator;
        _reorderValidator = reorderValidator;
        _filterValidator = filterValidator;
        _reasonValidator = reasonValidator;
        _testValidator = testValidator;
    }

    /// <inheritdoc />
    public async Task<Result<AbacRuleSetDto>> CreateRuleSetAsync(AbacRuleSetCreateInputDto input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = await _createValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Failure(ErrorCodes.ValidationFailed, string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }
        if (_caller.UserId is not long registeredBy)
        {
            return Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // Uniqueness check — defence-in-depth against the DB unique index.
        var existing = await _db.AbacRuleSets
            .AnyAsync(rs => rs.PolicyName == input.PolicyName && rs.IsActive, ct)
            .ConfigureAwait(false);
        if (existing)
        {
            return Failure(ErrorCodes.AbacDuplicatePolicyName,
                $"An active ABAC rule set with policy name '{input.PolicyName}' already exists.");
        }

        var defaultEffect = AbacEffect.Deny;
        if (input.DefaultEffect is not null && Enum.TryParse<AbacEffect>(input.DefaultEffect, ignoreCase: false, out var parsedDefault))
        {
            defaultEffect = parsedDefault;
        }

        var now = _clock.UtcNow;
        var row = new AbacRuleSet
        {
            PolicyName = input.PolicyName,
            DisplayName = input.DisplayName,
            Description = input.Description,
            DefaultEffect = defaultEffect,
            IsActive = true,
            RegisteredByUserId = registeredBy,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
        };
        _db.AbacRuleSets.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        CnasMeter.AbacRuleSetCreated.Add(1);
        await EmitAuditAsync("ABAC.RULE_SET_CREATED", row, extraJson: null, ct).ConfigureAwait(false);
        _evaluator.InvalidateCache();

        return Result<AbacRuleSetDto>.Success(Project(row, includeRules: true));
    }

    /// <inheritdoc />
    public async Task<Result<AbacRuleSetDto>> ModifyRuleSetAsync(string sqid, AbacRuleSetModifyInputDto input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = await _modifyValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Failure(ErrorCodes.ValidationFailed, string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }
        var loaded = await LoadRuleSetWithRulesAsync(sqid, ct).ConfigureAwait(false);
        if (loaded.Failure is { } failure) return Result<AbacRuleSetDto>.From(failure);
        var row = loaded.Row!;

        var now = _clock.UtcNow;
        if (input.DisplayName is not null) row.DisplayName = input.DisplayName;
        if (input.Description is not null) row.Description = input.Description;
        if (input.DefaultEffect is not null
            && Enum.TryParse<AbacEffect>(input.DefaultEffect, ignoreCase: false, out var newEffect))
        {
            row.DefaultEffect = newEffect;
        }
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync("ABAC.RULE_SET_MODIFIED", row,
            extraJson: $"{{\"reason\":\"{JsonEncodedReason(input.ChangeReason)}\"}}", ct).ConfigureAwait(false);
        _evaluator.InvalidateCache();

        return Result<AbacRuleSetDto>.Success(Project(row, includeRules: true));
    }

    /// <inheritdoc />
    public Task<Result<AbacRuleSetDto>> DisableRuleSetAsync(string sqid, AbacRuleReasonInputDto input, CancellationToken ct = default)
        => FlipRuleSetActiveAsync(sqid, input, isActive: false, eventCode: "ABAC.RULE_SET_DISABLED", ct);

    /// <inheritdoc />
    public Task<Result<AbacRuleSetDto>> EnableRuleSetAsync(string sqid, AbacRuleReasonInputDto input, CancellationToken ct = default)
        => FlipRuleSetActiveAsync(sqid, input, isActive: true, eventCode: "ABAC.RULE_SET_ENABLED", ct);

    /// <summary>Shared implementation of the disable / enable transitions.</summary>
    /// <param name="sqid">Sqid-encoded rule-set id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="isActive">Target <c>IsActive</c> value.</param>
    /// <param name="eventCode">Stable audit event code.</param>
    /// <param name="ct">Cancellation propagation.</param>
    /// <returns>The updated DTO.</returns>
    private async Task<Result<AbacRuleSetDto>> FlipRuleSetActiveAsync(
        string sqid,
        AbacRuleReasonInputDto input,
        bool isActive,
        string eventCode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = await _reasonValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Failure(ErrorCodes.ValidationFailed, string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure) return Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        var row = await _db.AbacRuleSets
            .Include(rs => rs.Rules)
            .SingleOrDefaultAsync(rs => rs.Id == decoded.Value, ct)
            .ConfigureAwait(false);
        if (row is null) return Failure(ErrorCodes.AbacNotFound, "ABAC rule set not found.");

        if (row.IsActive == isActive)
        {
            // Idempotent — return the current state without re-emitting audit.
            return Result<AbacRuleSetDto>.Success(Project(row, includeRules: true));
        }

        var now = _clock.UtcNow;
        row.IsActive = isActive;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync(eventCode, row,
            extraJson: $"{{\"reason\":\"{JsonEncodedReason(input.Reason)}\"}}", ct).ConfigureAwait(false);
        _evaluator.InvalidateCache();

        return Result<AbacRuleSetDto>.Success(Project(row, includeRules: true));
    }

    /// <inheritdoc />
    public async Task<Result<AbacRuleDto>> AddRuleAsync(string ruleSetSqid, AbacRuleInputDto input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = await _ruleValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            // Distinguish the parse-error path so callers can branch on it.
            var hasParseError = validation.Errors.Any(e => e.ErrorMessage.Contains("failed to parse", StringComparison.Ordinal));
            return Result<AbacRuleDto>.Failure(
                hasParseError ? ErrorCodes.AbacParseError : ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }
        var decoded = _sqids.TryDecode(ruleSetSqid);
        if (decoded.IsFailure) return Result<AbacRuleDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        var ruleSet = await _db.AbacRuleSets
            .SingleOrDefaultAsync(rs => rs.Id == decoded.Value, ct)
            .ConfigureAwait(false);
        if (ruleSet is null) return Result<AbacRuleDto>.Failure(ErrorCodes.AbacNotFound, "ABAC rule set not found.");

        // Defensive re-parse — keeps the contract even if the validator is
        // mis-wired or bypassed.
        var parse = _parser.Parse(input.ConditionExpression);
        if (parse.IsFailure) return Result<AbacRuleDto>.Failure(ErrorCodes.AbacParseError, parse.ErrorMessage!);

        var effect = Enum.Parse<AbacEffect>(input.Effect, ignoreCase: false);
        var now = _clock.UtcNow;
        var rule = new AbacRule
        {
            RuleSetId = ruleSet.Id,
            OrderIndex = input.OrderIndex,
            Effect = effect,
            ConditionExpression = input.ConditionExpression,
            Description = input.Description,
            IsActive = true,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
        };
        _db.AbacRules.Add(rule);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        CnasMeter.AbacRuleAdded.Add(1,
            new KeyValuePair<string, object?>("effect", effect.ToString()));
        await EmitAuditAsync("ABAC.RULE_ADDED", ruleSet, extraJson: $"{{\"ruleSqid\":\"{_sqids.Encode(rule.Id)}\"}}", ct).ConfigureAwait(false);
        _evaluator.InvalidateCache();

        return Result<AbacRuleDto>.Success(ProjectRule(rule, ruleSet));
    }

    /// <inheritdoc />
    public async Task<Result<AbacRuleDto>> ModifyRuleAsync(string ruleSqid, AbacRuleInputDto input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = await _ruleValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            var hasParseError = validation.Errors.Any(e => e.ErrorMessage.Contains("failed to parse", StringComparison.Ordinal));
            return Result<AbacRuleDto>.Failure(
                hasParseError ? ErrorCodes.AbacParseError : ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }
        var decoded = _sqids.TryDecode(ruleSqid);
        if (decoded.IsFailure) return Result<AbacRuleDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        var rule = await _db.AbacRules
            .SingleOrDefaultAsync(r => r.Id == decoded.Value, ct)
            .ConfigureAwait(false);
        if (rule is null) return Result<AbacRuleDto>.Failure(ErrorCodes.AbacNotFound, "ABAC rule not found.");

        var parse = _parser.Parse(input.ConditionExpression);
        if (parse.IsFailure) return Result<AbacRuleDto>.Failure(ErrorCodes.AbacParseError, parse.ErrorMessage!);

        var effect = Enum.Parse<AbacEffect>(input.Effect, ignoreCase: false);
        var now = _clock.UtcNow;
        rule.OrderIndex = input.OrderIndex;
        rule.Effect = effect;
        rule.ConditionExpression = input.ConditionExpression;
        rule.Description = input.Description;
        rule.UpdatedAtUtc = now;
        rule.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var ruleSet = await _db.AbacRuleSets.SingleAsync(rs => rs.Id == rule.RuleSetId, ct).ConfigureAwait(false);
        await EmitAuditAsync("ABAC.RULE_MODIFIED", ruleSet,
            extraJson: $"{{\"ruleSqid\":\"{_sqids.Encode(rule.Id)}\"}}", ct).ConfigureAwait(false);
        _evaluator.InvalidateCache();

        return Result<AbacRuleDto>.Success(ProjectRule(rule, ruleSet));
    }

    /// <inheritdoc />
    public Task<Result<AbacRuleDto>> DisableRuleAsync(string ruleSqid, AbacRuleReasonInputDto input, CancellationToken ct = default)
        => FlipRuleActiveAsync(ruleSqid, input, isActive: false, eventCode: "ABAC.RULE_DISABLED", ct);

    /// <inheritdoc />
    public Task<Result<AbacRuleDto>> EnableRuleAsync(string ruleSqid, AbacRuleReasonInputDto input, CancellationToken ct = default)
        => FlipRuleActiveAsync(ruleSqid, input, isActive: true, eventCode: "ABAC.RULE_ENABLED", ct);

    /// <summary>Shared implementation of rule disable / enable.</summary>
    /// <param name="ruleSqid">Sqid-encoded rule id.</param>
    /// <param name="input">Reason envelope.</param>
    /// <param name="isActive">Target <c>IsActive</c> value.</param>
    /// <param name="eventCode">Stable audit event code.</param>
    /// <param name="ct">Cancellation propagation.</param>
    /// <returns>The updated DTO.</returns>
    private async Task<Result<AbacRuleDto>> FlipRuleActiveAsync(
        string ruleSqid,
        AbacRuleReasonInputDto input,
        bool isActive,
        string eventCode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = await _reasonValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AbacRuleDto>.Failure(ErrorCodes.ValidationFailed, string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }
        var decoded = _sqids.TryDecode(ruleSqid);
        if (decoded.IsFailure) return Result<AbacRuleDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        var rule = await _db.AbacRules
            .SingleOrDefaultAsync(r => r.Id == decoded.Value, ct)
            .ConfigureAwait(false);
        if (rule is null) return Result<AbacRuleDto>.Failure(ErrorCodes.AbacNotFound, "ABAC rule not found.");

        if (rule.IsActive == isActive)
        {
            var ruleSetIdempotent = await _db.AbacRuleSets.SingleAsync(rs => rs.Id == rule.RuleSetId, ct).ConfigureAwait(false);
            return Result<AbacRuleDto>.Success(ProjectRule(rule, ruleSetIdempotent));
        }

        var now = _clock.UtcNow;
        rule.IsActive = isActive;
        rule.UpdatedAtUtc = now;
        rule.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var ruleSet = await _db.AbacRuleSets.SingleAsync(rs => rs.Id == rule.RuleSetId, ct).ConfigureAwait(false);
        await EmitAuditAsync(eventCode, ruleSet,
            extraJson: $"{{\"ruleSqid\":\"{_sqids.Encode(rule.Id)}\",\"reason\":\"{JsonEncodedReason(input.Reason)}\"}}", ct)
            .ConfigureAwait(false);
        _evaluator.InvalidateCache();

        return Result<AbacRuleDto>.Success(ProjectRule(rule, ruleSet));
    }

    /// <inheritdoc />
    public async Task<Result<AbacRuleSetDto>> ReorderRulesAsync(
        string ruleSetSqid,
        IReadOnlyList<AbacRuleReorderInputDto> ordering,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ordering);
        if (ordering.Count == 0)
        {
            return Failure(ErrorCodes.ValidationFailed, "At least one reorder entry is required.");
        }
        foreach (var entry in ordering)
        {
            var v = await _reorderValidator.ValidateAsync(entry, ct).ConfigureAwait(false);
            if (!v.IsValid)
            {
                return Failure(ErrorCodes.ValidationFailed, string.Join("; ", v.Errors.Select(e => e.ErrorMessage)));
            }
        }
        var loaded = await LoadRuleSetWithRulesAsync(ruleSetSqid, ct).ConfigureAwait(false);
        if (loaded.Failure is { } failure) return Result<AbacRuleSetDto>.From(failure);
        var ruleSet = loaded.Row!;

        // Decode each Sqid up front and verify membership in this set.
        var newIndices = new Dictionary<long, int>();
        foreach (var entry in ordering)
        {
            var decoded = _sqids.TryDecode(entry.RuleSqid);
            if (decoded.IsFailure)
            {
                return Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            }
            if (!ruleSet.Rules.Any(r => r.Id == decoded.Value))
            {
                return Failure(ErrorCodes.AbacNotFound,
                    $"Rule {entry.RuleSqid} does not belong to rule set {ruleSetSqid}.");
            }
            newIndices[decoded.Value] = entry.NewOrderIndex;
        }

        // Phase 1 — flip every rule into the "negative" temporary range so the
        // partial unique index doesn't fire while we re-sequence. Stored as
        // (-(OrderIndex + 1)) to guarantee disjointness with the target range.
        var now = _clock.UtcNow;
        foreach (var rule in ruleSet.Rules)
        {
            if (newIndices.ContainsKey(rule.Id))
            {
                rule.OrderIndex = -(rule.OrderIndex + 1);
                rule.UpdatedAtUtc = now;
                rule.UpdatedBy = _caller.UserSqid;
            }
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Phase 2 — assign the requested indices.
        foreach (var rule in ruleSet.Rules)
        {
            if (newIndices.TryGetValue(rule.Id, out var target))
            {
                rule.OrderIndex = target;
            }
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync("ABAC.RULES_REORDERED", ruleSet,
            extraJson: $"{{\"count\":{ordering.Count}}}", ct).ConfigureAwait(false);
        _evaluator.InvalidateCache();

        // Reload to project a stable ordering.
        var reloaded = await _db.AbacRuleSets
            .Include(rs => rs.Rules)
            .SingleAsync(rs => rs.Id == ruleSet.Id, ct)
            .ConfigureAwait(false);
        return Result<AbacRuleSetDto>.Success(Project(reloaded, includeRules: true));
    }

    /// <inheritdoc />
    public async Task<Result<AbacRuleSetDto>> GetRuleSetByIdAsync(string sqid, CancellationToken ct = default)
    {
        var loaded = await LoadRuleSetWithRulesAsync(sqid, ct).ConfigureAwait(false);
        if (loaded.Failure is { } failure) return Result<AbacRuleSetDto>.From(failure);
        return Result<AbacRuleSetDto>.Success(Project(loaded.Row!, includeRules: true));
    }

    /// <inheritdoc />
    public async Task<Result<AbacRuleSetDto>> GetRuleSetByPolicyNameAsync(string policyName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        var row = await _db.AbacRuleSets
            .Include(rs => rs.Rules)
            .SingleOrDefaultAsync(rs => rs.PolicyName == policyName, ct)
            .ConfigureAwait(false);
        if (row is null) return Failure(ErrorCodes.AbacNotFound, $"No ABAC rule set found for policy '{policyName}'.");
        return Result<AbacRuleSetDto>.Success(Project(row, includeRules: true));
    }

    /// <inheritdoc />
    public async Task<Result<AbacRuleSetPageDto>> ListRuleSetsAsync(AbacRuleSetFilterDto filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var validation = await _filterValidator.ValidateAsync(filter, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AbacRuleSetPageDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        IQueryable<AbacRuleSet> q = _db.AbacRuleSets.Include(rs => rs.Rules);
        if (!string.IsNullOrEmpty(filter.PolicyName))
        {
            q = q.Where(rs => rs.PolicyName == filter.PolicyName);
        }
        if (filter.IsActive is { } isActive)
        {
            q = q.Where(rs => rs.IsActive == isActive);
        }
        var total = await q.CountAsync(ct).ConfigureAwait(false);
        var rows = await q.OrderBy(rs => rs.PolicyName)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var items = rows.Select(r => Project(r, includeRules: true)).ToList();
        return Result<AbacRuleSetPageDto>.Success(new AbacRuleSetPageDto(items, total, filter.Skip, filter.Take));
    }

    /// <inheritdoc />
    public async Task<Result<AbacDecisionDto>> TestExpressionAsync(AbacExpressionTestInputDto input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = await _testValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AbacDecisionDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var ruleSet = await _db.AbacRuleSets
            .AsNoTracking()
            .Include(rs => rs.Rules.Where(r => r.IsActive))
            .SingleOrDefaultAsync(rs => rs.PolicyName == input.PolicyName && rs.IsActive, ct)
            .ConfigureAwait(false);
        if (ruleSet is null)
        {
            return Result<AbacDecisionDto>.Failure(
                ErrorCodes.AbacNotFound,
                $"No active ABAC rule set found for policy '{input.PolicyName}'.");
        }
        var context = new AbacEvaluationContext(
            Subject: input.Subject,
            Resource: input.Resource,
            Environment: input.Environment,
            Action: input.Action);
        var decision = ((AbacRuleEvaluator)_evaluator).EvaluateRuleSet(ruleSet, context);
        return Result<AbacDecisionDto>.Success(decision);
    }

    /// <summary>
    /// Resolves a Sqid to a fully-loaded rule set (with rules), returning a
    /// structured failure on decode error / not-found.
    /// </summary>
    /// <param name="sqid">Sqid-encoded rule-set id.</param>
    /// <param name="ct">Cancellation propagation.</param>
    /// <returns>Tuple carrying the row OR a failure result.</returns>
    private async Task<(AbacRuleSet? Row, Result? Failure)> LoadRuleSetWithRulesAsync(string sqid, CancellationToken ct)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return (null, Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!));
        }
        var row = await _db.AbacRuleSets
            .Include(rs => rs.Rules)
            .SingleOrDefaultAsync(rs => rs.Id == decoded.Value, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return (null, Result.Failure(ErrorCodes.AbacNotFound, "ABAC rule set not found."));
        }
        return (row, null);
    }

    /// <summary>Emits a Critical audit row for a rule-set lifecycle event.</summary>
    /// <param name="eventCode">Stable event code (e.g. <c>ABAC.RULE_SET_CREATED</c>).</param>
    /// <param name="ruleSet">The affected rule set.</param>
    /// <param name="extraJson">Optional extra JSON merged into the details payload.</param>
    /// <param name="ct">Cancellation propagation.</param>
    /// <returns>Awaitable task.</returns>
    private async Task EmitAuditAsync(string eventCode, AbacRuleSet ruleSet, string? extraJson, CancellationToken ct)
    {
        var actor = _caller.UserSqid ?? "system";
        var details = JsonSerializer.Serialize(new
        {
            policyName = ruleSet.PolicyName,
            ruleSetSqid = _sqids.Encode(ruleSet.Id),
            extra = extraJson,
        });
        await _audit.RecordAsync(
            eventCode,
            AuditSeverity.Critical,
            actor,
            nameof(AbacRuleSet),
            ruleSet.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);
    }

    /// <summary>Escapes a free-form reason for safe inclusion in a JSON string.</summary>
    /// <param name="value">The raw reason.</param>
    /// <returns>The JSON-escaped reason (without surrounding quotes).</returns>
    private static string JsonEncodedReason(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var encoded = JsonSerializer.Serialize(value);
        // Strip the surrounding quotes JsonSerializer adds around strings.
        return encoded.Length >= 2 ? encoded.Substring(1, encoded.Length - 2) : encoded;
    }

    /// <summary>Projects a rule-set row to its DTO with Sqid-encoded ids.</summary>
    /// <param name="row">The rule-set row.</param>
    /// <param name="includeRules">Whether to project the children.</param>
    /// <returns>The DTO.</returns>
    private AbacRuleSetDto Project(AbacRuleSet row, bool includeRules)
    {
        var rules = includeRules
            ? row.Rules.OrderBy(r => r.OrderIndex).Select(r => ProjectRule(r, row)).ToList()
            : new List<AbacRuleDto>();
        return new AbacRuleSetDto(
            Id: _sqids.Encode(row.Id),
            PolicyName: row.PolicyName,
            DisplayName: row.DisplayName,
            Description: row.Description,
            DefaultEffect: row.DefaultEffect.ToString(),
            IsActive: row.IsActive,
            Rules: rules);
    }

    /// <summary>Projects a rule row to its DTO with Sqid-encoded ids.</summary>
    /// <param name="rule">The rule row.</param>
    /// <param name="ruleSet">The owning rule-set row.</param>
    /// <returns>The DTO.</returns>
    private AbacRuleDto ProjectRule(AbacRule rule, AbacRuleSet ruleSet)
        => new(
            Id: _sqids.Encode(rule.Id),
            RuleSetSqid: _sqids.Encode(ruleSet.Id),
            OrderIndex: rule.OrderIndex,
            Effect: rule.Effect.ToString(),
            ConditionExpression: rule.ConditionExpression,
            Description: rule.Description,
            IsActive: rule.IsActive);

    /// <summary>Shortcut helper for building a typed-failure <see cref="Result{T}"/>.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>The failure result.</returns>
    private static Result<AbacRuleSetDto> Failure(string code, string message)
        => Result<AbacRuleSetDto>.Failure(code, message);
}
