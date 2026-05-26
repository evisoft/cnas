using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Application.Templates;

/// <summary>
/// R0131 / CF 17.15 — default implementation of
/// <see cref="ITemplateValidationService"/>. Reads the
/// <see cref="DocumentTemplate.ValidationRulesJson"/> column for the addressed code,
/// deserialises the array of rule rows, builds validated
/// <see cref="TemplateValidationRule"/> instances, and applies each one in array order.
/// </summary>
/// <remarks>
/// <para>
/// Stateless and thread-safe; register as Scoped (it depends on
/// <see cref="IReadOnlyCnasDbContext"/> which is per-request).
/// </para>
/// </remarks>
public sealed class TemplateValidationService : ITemplateValidationService
{
    /// <summary>Tolerant JSON parsing — comments + trailing commas allowed.</summary>
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IReadOnlyCnasDbContext _db;

    /// <summary>
    /// Wires the service with its read-only database collaborator.
    /// </summary>
    /// <param name="db">Read-only EF Core context — used to look up the template's rule-set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> is <see langword="null"/>.</exception>
    public TemplateValidationService(IReadOnlyCnasDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<Result> ValidateAsync(
        string templateCode,
        IReadOnlyDictionary<string, string?> formValues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(formValues);
        if (string.IsNullOrWhiteSpace(templateCode))
        {
            return Result.Success();
        }

        // Look up the row's persisted rule-set. We DO NOT fail when the code is unknown
        // (the renderer may dispatch to a non-persistent baked-in IDocxTemplate of the
        // same code) — absence of a rule-set is "no metadata to check" which the gate
        // treats as a no-op pass.
        var code = templateCode.Trim();
        var rulesJson = await _db.DocumentTemplates
            .Where(t => t.IsActive && t.IsCurrent && t.Code == code)
            .Select(t => t.ValidationRulesJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            return Result.Success();
        }

        var rules = ParseRules(rulesJson!);
        foreach (var rule in rules)
        {
            formValues.TryGetValue(rule.FieldName, out var rawValue);
            var ruleResult = ApplyRule(rule, rawValue);
            if (ruleResult.IsFailure)
            {
                return ruleResult;
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Parses the persisted rule-array JSON into validated
    /// <see cref="TemplateValidationRule"/> instances. Rows whose <c>ruleKind</c> string
    /// does not match any enum member are SILENTLY SKIPPED (forward-compat with future
    /// kinds rolled to admins ahead of the engine upgrade); rows whose argument fails
    /// the per-kind shape check are also skipped (defensive — bad input cannot become a
    /// fail-closed gate).
    /// </summary>
    /// <param name="json">The persisted JSON array.</param>
    /// <returns>Validated rules, in array order.</returns>
    private static IReadOnlyList<TemplateValidationRule> ParseRules(string json)
    {
        var rules = new List<TemplateValidationRule>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException)
        {
            // Malformed JSON ⇒ treat as no rules. The admin upload validator should
            // have caught this; defensive shutoff here keeps a corrupt row from
            // breaking every render. The validator service has no logger so we cannot
            // emit a structured warning at this seam.
            return rules;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return rules;
            }

            foreach (var ruleElement in document.RootElement.EnumerateArray())
            {
                if (ruleElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!ruleElement.TryGetProperty("fieldName", out var fieldNameElement)
                    || fieldNameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (!ruleElement.TryGetProperty("ruleKind", out var ruleKindElement)
                    || ruleKindElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (!Enum.TryParse<TemplateValidationRuleKind>(
                        ruleKindElement.GetString(), ignoreCase: true, out var kind))
                {
                    // Unknown rule kind — silently ignored.
                    continue;
                }

                string? argument = null;
                if (ruleElement.TryGetProperty("argument", out var argumentElement))
                {
                    argument = argumentElement.ValueKind switch
                    {
                        JsonValueKind.String => argumentElement.GetString(),
                        JsonValueKind.Number => argumentElement.GetRawText(),
                        _ => null,
                    };
                }

                var ruleResult = TemplateValidationRule.Create(
                    fieldNameElement.GetString()!, kind, argument);
                if (ruleResult.IsSuccess)
                {
                    rules.Add(ruleResult.Value);
                }
                // Else: malformed argument; skip silently (defensive).
            }
        }

        return rules;
    }

    /// <summary>
    /// Applies a single rule to the supplied raw value. Returns <see cref="Result.Success()"/>
    /// when the value satisfies the rule (including the no-op <see cref="TemplateValidationRuleKind.Custom"/>
    /// path), or a failure carrying <see cref="ErrorCodes.TemplateValidationFailed"/>.
    /// </summary>
    /// <param name="rule">The rule to apply.</param>
    /// <param name="rawValue">The raw value from the form-values dictionary; null when missing.</param>
    /// <returns>Success or a TEMPLATE_VALIDATION_FAILED failure with a descriptive message.</returns>
    private static Result ApplyRule(TemplateValidationRule rule, string? rawValue)
    {
        switch (rule.RuleKind)
        {
            case TemplateValidationRuleKind.Required:
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    return Fail(rule, "field is required.");
                }
                return Result.Success();

            case TemplateValidationRuleKind.MaxLength:
                if (rawValue is not null
                    && int.TryParse(rule.Argument, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var maxLen)
                    && rawValue.Length > maxLen)
                {
                    return Fail(rule, $"length {rawValue.Length} exceeds MaxLength={maxLen}.");
                }
                return Result.Success();

            case TemplateValidationRuleKind.MinLength:
                if (int.TryParse(rule.Argument, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var minLen)
                    && (rawValue?.Length ?? 0) < minLen)
                {
                    return Fail(rule, $"length {(rawValue?.Length ?? 0)} below MinLength={minLen}.");
                }
                return Result.Success();

            case TemplateValidationRuleKind.Regex:
                if (rawValue is null)
                {
                    return Fail(rule, "field is required for Regex validation.");
                }
                try
                {
                    var regex = new Regex(
                        rule.Argument ?? string.Empty,
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250));
                    if (!regex.IsMatch(rawValue))
                    {
                        return Fail(rule, $"value does not match Regex='{rule.Argument}'.");
                    }
                }
                catch (ArgumentException)
                {
                    // Invalid regex pattern — skip silently (defensive). Returning success
                    // here mirrors the "unknown rule kind" path: bad metadata cannot
                    // become a fail-closed gate.
                    return Result.Success();
                }
                catch (RegexMatchTimeoutException)
                {
                    return Fail(rule, "Regex evaluation timed out.");
                }
                return Result.Success();

            case TemplateValidationRuleKind.Range:
                if (rawValue is null
                    || !decimal.TryParse(rawValue,
                        System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var numericValue))
                {
                    return Fail(rule, "value is not a valid decimal for Range validation.");
                }
                if (TemplateValidationRule.TryParseRange(rule.Argument ?? string.Empty, out var min, out var max)
                    && (numericValue < min || numericValue > max))
                {
                    return Fail(rule, $"value {numericValue} outside Range=[{min}..{max}].");
                }
                return Result.Success();

            case TemplateValidationRuleKind.Custom:
            default:
                // Custom + unrecognised kinds are silently ignored — the gate passes.
                return Result.Success();
        }
    }

    /// <summary>
    /// Builds a deterministic failure result naming the offending field and rule kind so
    /// the UI can highlight the problematic input.
    /// </summary>
    /// <param name="rule">The rule that failed.</param>
    /// <param name="reason">Human-readable continuation of the message.</param>
    /// <returns>Failure carrying <see cref="ErrorCodes.TemplateValidationFailed"/>.</returns>
    private static Result Fail(TemplateValidationRule rule, string reason) =>
        Result.Failure(
            ErrorCodes.TemplateValidationFailed,
            $"Template validation failed for field '{rule.FieldName}' (rule {rule.RuleKind}): {reason}");
}
