using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Decisions;

/// <summary>
/// JSON-driven implementation of <see cref="IDecisionEngine"/>. Interprets a small
/// declarative DSL whose shape is documented in the project TODO §3 (Generic
/// service-decision workflow). The engine is stateless and thread-safe; register as
/// a singleton via <c>AddCnasApplication</c>.
/// </summary>
/// <remarks>
/// <para><b>DSL shape</b>:</para>
/// <code>
/// {
///   "code": "BIRTH_GRANT",
///   "eligibility": [
///     { "rule": "fact-equals", "fact": "isInsured", "value": true,
///       "failCode": "INELIGIBLE_NOT_INSURED" },
///     { "rule": "date-within-days", "fact": "birthDateUtc",
///       "referenceFact": "claimDateUtc", "maxDays": 365,
///       "failCode": "INELIGIBLE_LATE_CLAIM" }
///   ],
///   "amount": { "kind": "table", "lookupFact": "birthOrder", "currency": "MDL",
///               "table": { "1": 11000.00, "2": 12000.00, "default": 13000.00 } },
///   "successCode": "BIRTH_GRANT_ELIGIBLE"
/// }
/// </code>
/// <para><b>Supported eligibility rule kinds</b>: <c>fact-equals</c>, <c>fact-greater-than</c>,
/// <c>fact-less-than</c>, <c>fact-in-set</c>, <c>date-within-days</c>, <c>age-at-date-between</c>.</para>
/// <para><b>Supported amount kinds</b>: <c>fixed</c>, <c>table</c>, <c>percent-of-fact</c>.</para>
/// </remarks>
public sealed class JsonRulesDecisionEngine : IDecisionEngine
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <inheritdoc />
    public Result<DecisionOutcome> Evaluate(string ruleSetJson, DecisionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (string.IsNullOrWhiteSpace(ruleSetJson))
        {
            return Result<DecisionOutcome>.Failure(
                ErrorCodes.BadRule,
                "Rule-set JSON is null or empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(ruleSetJson, DocOptions);
        }
        catch (JsonException ex)
        {
            return Result<DecisionOutcome>.Failure(
                ErrorCodes.BadRule,
                $"Rule-set JSON is malformed: {ex.Message}");
        }

        using (document)
        {
            return EvaluateRoot(document.RootElement, facts);
        }
    }

    private static Result<DecisionOutcome> EvaluateRoot(JsonElement root, DecisionFacts facts)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Result<DecisionOutcome>.Failure(
                ErrorCodes.BadRule,
                "Rule-set root must be a JSON object.");
        }

        // Optional success code.
        string successCode = root.TryGetProperty("successCode", out var successProp)
                             && successProp.ValueKind == JsonValueKind.String
            ? successProp.GetString()!
            : "ELIGIBLE";

        // Eligibility array (may be missing → treated as empty / always eligible).
        var reasonCodes = new List<string>();
        bool eligible = true;

        if (root.TryGetProperty("eligibility", out var eligibilityProp))
        {
            if (eligibilityProp.ValueKind != JsonValueKind.Array)
            {
                return Result<DecisionOutcome>.Failure(
                    ErrorCodes.BadRule,
                    "'eligibility' must be a JSON array when present.");
            }

            foreach (var ruleNode in eligibilityProp.EnumerateArray())
            {
                var ruleResult = EvaluateEligibilityRule(ruleNode, facts);
                if (ruleResult.IsFailure)
                {
                    return Result<DecisionOutcome>.Failure(ruleResult.ErrorCode!, ruleResult.ErrorMessage!);
                }

                if (ruleResult.Value is { } code)
                {
                    eligible = false;
                    reasonCodes.Add(code);
                }
            }
        }

        if (!eligible)
        {
            return Result<DecisionOutcome>.Success(new DecisionOutcome(
                IsEligible: false,
                Amount: null,
                ReasonCodes: reasonCodes,
                ComputedValues: new Dictionary<string, object?>()));
        }

        // Eligible — compute amount.
        if (!root.TryGetProperty("amount", out var amountProp))
        {
            return Result<DecisionOutcome>.Failure(
                ErrorCodes.BadRule,
                "Rule-set is missing the required 'amount' section.");
        }

        var amount = ComputeAmount(amountProp, facts);
        if (amount.IsFailure)
        {
            return Result<DecisionOutcome>.Failure(amount.ErrorCode!, amount.ErrorMessage!);
        }

        reasonCodes.Add(successCode);
        return Result<DecisionOutcome>.Success(new DecisionOutcome(
            IsEligible: true,
            Amount: amount.Value,
            ReasonCodes: reasonCodes,
            ComputedValues: new Dictionary<string, object?>()));
    }

    // Returns Success(null) when the rule passed, Success(failCode) when it failed.
    // Returns Failure(...) for engine-level problems (missing fact, bad rule shape).
    private static Result<string?> EvaluateEligibilityRule(JsonElement ruleNode, DecisionFacts facts)
    {
        if (ruleNode.ValueKind != JsonValueKind.Object
            || !ruleNode.TryGetProperty("rule", out var kindProp)
            || kindProp.ValueKind != JsonValueKind.String)
        {
            return Result<string?>.Failure(
                ErrorCodes.BadRule,
                "Each eligibility rule must be an object with a string 'rule' property.");
        }

        string kind = kindProp.GetString()!;
        string? failCode = ruleNode.TryGetProperty("failCode", out var failProp)
                           && failProp.ValueKind == JsonValueKind.String
            ? failProp.GetString()
            : null;

        return kind switch
        {
            "fact-equals" => EvalFactEquals(ruleNode, facts, failCode),
            "fact-greater-than" => EvalNumericCompare(ruleNode, facts, failCode, greater: true),
            "fact-less-than" => EvalNumericCompare(ruleNode, facts, failCode, greater: false),
            "fact-in-set" => EvalFactInSet(ruleNode, facts, failCode),
            "date-within-days" => EvalDateWithinDays(ruleNode, facts, failCode),
            "age-at-date-between" => EvalAgeAtDateBetween(ruleNode, facts, failCode),
            _ => Result<string?>.Failure(
                ErrorCodes.BadRule,
                $"Unknown eligibility rule kind '{kind}'."),
        };
    }

    // ───────────────────────────── Rule evaluators ─────────────────────────────

    private static Result<string?> EvalFactEquals(JsonElement node, DecisionFacts facts, string? failCode)
    {
        if (!TryGetString(node, "fact", out var factKey)
            || !node.TryGetProperty("value", out var expected))
        {
            return BadRule("'fact-equals' requires 'fact' and 'value' properties.");
        }

        if (!facts.Values.TryGetValue(factKey, out var actual) || actual is null)
        {
            return Result<string?>.Failure(
                ErrorCodes.MissingFact,
                $"Required fact '{factKey}' was not supplied.");
        }

        bool match = expected.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False =>
                actual is bool b && b == (expected.ValueKind == JsonValueKind.True),
            JsonValueKind.String =>
                actual is string s && string.Equals(s, expected.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number =>
                TryAsDecimal(actual, out var dec) && dec == expected.GetDecimal(),
            JsonValueKind.Null =>
                actual is null,
            _ => false,
        };

        return Pass(match, failCode);
    }

    private static Result<string?> EvalNumericCompare(
        JsonElement node, DecisionFacts facts, string? failCode, bool greater)
    {
        if (!TryGetString(node, "fact", out var factKey)
            || !node.TryGetProperty("value", out var thresholdProp)
            || thresholdProp.ValueKind != JsonValueKind.Number)
        {
            return BadRule($"'fact-{(greater ? "greater" : "less")}-than' requires 'fact' and numeric 'value'.");
        }

        if (!facts.Values.TryGetValue(factKey, out var raw) || raw is null)
        {
            return Result<string?>.Failure(
                ErrorCodes.MissingFact,
                $"Required fact '{factKey}' was not supplied.");
        }

        if (!TryAsDecimal(raw, out var actual))
        {
            return BadRule($"Fact '{factKey}' is not numeric (was '{raw.GetType().Name}').");
        }

        decimal threshold = thresholdProp.GetDecimal();
        bool pass = greater ? actual > threshold : actual < threshold;
        return Pass(pass, failCode);
    }

    private static Result<string?> EvalFactInSet(JsonElement node, DecisionFacts facts, string? failCode)
    {
        if (!TryGetString(node, "fact", out var factKey)
            || !node.TryGetProperty("values", out var valuesProp)
            || valuesProp.ValueKind != JsonValueKind.Array)
        {
            return BadRule("'fact-in-set' requires 'fact' and 'values' (array).");
        }

        if (!facts.Values.TryGetValue(factKey, out var raw) || raw is null)
        {
            return Result<string?>.Failure(
                ErrorCodes.MissingFact,
                $"Required fact '{factKey}' was not supplied.");
        }

        foreach (var candidate in valuesProp.EnumerateArray())
        {
            bool match = candidate.ValueKind switch
            {
                JsonValueKind.String =>
                    raw is string s && string.Equals(s, candidate.GetString(), StringComparison.Ordinal),
                JsonValueKind.Number =>
                    TryAsDecimal(raw, out var dec) && dec == candidate.GetDecimal(),
                JsonValueKind.True or JsonValueKind.False =>
                    raw is bool b && b == (candidate.ValueKind == JsonValueKind.True),
                _ => false,
            };

            if (match)
            {
                return Pass(true, failCode);
            }
        }

        return Pass(false, failCode);
    }

    private static Result<string?> EvalDateWithinDays(JsonElement node, DecisionFacts facts, string? failCode)
    {
        if (!TryGetString(node, "fact", out var factKey)
            || !TryGetString(node, "referenceFact", out var refKey)
            || !node.TryGetProperty("maxDays", out var maxDaysProp)
            || maxDaysProp.ValueKind != JsonValueKind.Number)
        {
            return BadRule("'date-within-days' requires 'fact', 'referenceFact', and numeric 'maxDays'.");
        }

        var subjectResult = facts.Require<DateTime>(factKey);
        if (subjectResult.IsFailure)
            return Result<string?>.Failure(subjectResult.ErrorCode!, subjectResult.ErrorMessage!);

        var referenceResult = facts.Require<DateTime>(refKey);
        if (referenceResult.IsFailure)
            return Result<string?>.Failure(referenceResult.ErrorCode!, referenceResult.ErrorMessage!);

        int maxDays = maxDaysProp.GetInt32();
        var delta = referenceResult.Value - subjectResult.Value;
        bool within = delta.TotalDays >= 0 && delta.TotalDays <= maxDays;
        return Pass(within, failCode);
    }

    private static Result<string?> EvalAgeAtDateBetween(JsonElement node, DecisionFacts facts, string? failCode)
    {
        if (!TryGetString(node, "dobFact", out var dobKey)
            || !TryGetString(node, "referenceFact", out var refKey)
            || !node.TryGetProperty("min", out var minProp)
            || !node.TryGetProperty("max", out var maxProp)
            || minProp.ValueKind != JsonValueKind.Number
            || maxProp.ValueKind != JsonValueKind.Number)
        {
            return BadRule("'age-at-date-between' requires 'dobFact', 'referenceFact', numeric 'min' and 'max'.");
        }

        var dobResult = facts.Require<DateTime>(dobKey);
        if (dobResult.IsFailure)
            return Result<string?>.Failure(dobResult.ErrorCode!, dobResult.ErrorMessage!);

        var refResult = facts.Require<DateTime>(refKey);
        if (refResult.IsFailure)
            return Result<string?>.Failure(refResult.ErrorCode!, refResult.ErrorMessage!);

        int age = ComputeAgeYears(dobResult.Value, refResult.Value);
        int min = minProp.GetInt32();
        int max = maxProp.GetInt32();
        bool inRange = age >= min && age <= max;
        return Pass(inRange, failCode);
    }

    // ───────────────────────────── Amount evaluators ─────────────────────────────

    private static Result<Money> ComputeAmount(JsonElement amountNode, DecisionFacts facts)
    {
        if (amountNode.ValueKind != JsonValueKind.Object
            || !TryGetString(amountNode, "kind", out var kind))
        {
            return Result<Money>.Failure(
                ErrorCodes.AmountComputationFailed,
                "'amount' must be an object with a string 'kind' property.");
        }

        return kind switch
        {
            "fixed" => AmountFixed(amountNode),
            "table" => AmountTable(amountNode, facts),
            "percent-of-fact" => AmountPercentOfFact(amountNode, facts),
            _ => Result<Money>.Failure(
                ErrorCodes.AmountComputationFailed,
                $"Unknown amount kind '{kind}'."),
        };
    }

    private static Result<Money> AmountFixed(JsonElement node)
    {
        if (!node.TryGetProperty("value", out var valueProp)
            || valueProp.ValueKind != JsonValueKind.Number
            || !TryGetString(node, "currency", out var currency))
        {
            return Result<Money>.Failure(
                ErrorCodes.AmountComputationFailed,
                "'fixed' amount requires numeric 'value' and string 'currency'.");
        }

        var money = Money.TryCreate(valueProp.GetDecimal(), currency);
        if (money.IsFailure)
        {
            return Result<Money>.Failure(
                ErrorCodes.AmountComputationFailed,
                money.ErrorMessage!);
        }

        return money;
    }

    private static Result<Money> AmountTable(JsonElement node, DecisionFacts facts)
    {
        if (!TryGetString(node, "lookupFact", out var lookupKey)
            || !TryGetString(node, "currency", out var currency)
            || !node.TryGetProperty("table", out var tableProp)
            || tableProp.ValueKind != JsonValueKind.Object)
        {
            return Result<Money>.Failure(
                ErrorCodes.AmountComputationFailed,
                "'table' amount requires 'lookupFact', 'currency', and object 'table'.");
        }

        if (!facts.Values.TryGetValue(lookupKey, out var raw) || raw is null)
        {
            return Result<Money>.Failure(
                ErrorCodes.MissingFact,
                $"Required fact '{lookupKey}' was not supplied.");
        }

        string lookupString = raw switch
        {
            string s => s,
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => raw.ToString() ?? string.Empty,
        };

        JsonElement match;
        if (tableProp.TryGetProperty(lookupString, out var direct))
        {
            match = direct;
        }
        else if (tableProp.TryGetProperty("default", out var fallback))
        {
            match = fallback;
        }
        else
        {
            return Result<Money>.Failure(
                ErrorCodes.AmountComputationFailed,
                $"Lookup key '{lookupString}' not found in table and no 'default' entry exists.");
        }

        if (match.ValueKind != JsonValueKind.Number)
        {
            return Result<Money>.Failure(
                ErrorCodes.AmountComputationFailed,
                "Table entries must be numeric values.");
        }

        var money = Money.TryCreate(match.GetDecimal(), currency);
        if (money.IsFailure)
        {
            return Result<Money>.Failure(
                ErrorCodes.AmountComputationFailed,
                money.ErrorMessage!);
        }

        return money;
    }

    private static Result<Money> AmountPercentOfFact(JsonElement node, DecisionFacts facts)
    {
        if (!node.TryGetProperty("percent", out var percentProp)
            || percentProp.ValueKind != JsonValueKind.Number
            || !TryGetString(node, "referenceFact", out var refKey))
        {
            return Result<Money>.Failure(
                ErrorCodes.AmountComputationFailed,
                "'percent-of-fact' requires numeric 'percent' and string 'referenceFact'.");
        }

        var rateResult = PercentRate.TryCreate(percentProp.GetDecimal());
        if (rateResult.IsFailure)
        {
            return Result<Money>.Failure(
                ErrorCodes.AmountComputationFailed,
                rateResult.ErrorMessage!);
        }

        if (!facts.Values.TryGetValue(refKey, out var raw) || raw is null)
        {
            return Result<Money>.Failure(
                ErrorCodes.MissingFact,
                $"Required fact '{refKey}' was not supplied.");
        }

        if (raw is Money baseMoney)
        {
            return Result<Money>.Success(rateResult.Value.Apply(baseMoney));
        }

        return Result<Money>.Failure(
            ErrorCodes.AmountComputationFailed,
            $"Reference fact '{refKey}' must be a Money value for 'percent-of-fact'.");
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    /// <summary>
    /// Computes whole-year age difference using the calendar-anniversary rule:
    /// if the reference date has not yet reached the birthday in its current year,
    /// subtract one. This mirrors the standard Moldovan civil-status calculation.
    /// </summary>
    private static int ComputeAgeYears(DateTime dob, DateTime asOf)
    {
        int age = asOf.Year - dob.Year;
        if (asOf < dob.AddYears(age))
        {
            age--;
        }
        return age;
    }

    private static bool TryGetString(JsonElement node, string name, out string value)
    {
        if (node.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Converts a fact value to <see cref="decimal"/> without introducing precision
    /// artifacts. Accepted types: <see cref="decimal"/>, <see cref="int"/>,
    /// <see cref="long"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="double"/> and <see cref="float"/> are deliberately rejected: the
    /// raw <c>(decimal)dbl</c> cast surfaces IEEE-754 rounding error (e.g.
    /// <c>0.1 + 0.2 → 0.30000000000000004</c>) into eligibility / amount
    /// comparisons, which can flip a decision near the boundary. Decisions must be
    /// fed exact decimal/long values at the fact-collection boundary — pre-convert
    /// monetary inputs to <see cref="decimal"/> before handing them to the engine.
    /// </para>
    /// </remarks>
    /// <param name="raw">Fact value supplied by the caller.</param>
    /// <param name="value">Resulting decimal, or <c>0m</c> when conversion is refused.</param>
    /// <returns><c>true</c> when the value was converted; otherwise <c>false</c>.</returns>
    private static bool TryAsDecimal(object raw, out decimal value)
    {
        switch (raw)
        {
            case decimal d: value = d; return true;
            case int i: value = i; return true;
            case long l: value = l; return true;
            // double/float deliberately refused — see XML doc above. The caller must
            // pre-convert to decimal at the boundary so we don't silently round.
            case double: value = 0m; return false;
            case float: value = 0m; return false;
            default: value = 0m; return false;
        }
    }

    private static Result<string?> Pass(bool ok, string? failCode) =>
        ok
            ? Result<string?>.Success(null)
            : Result<string?>.Success(failCode ?? "INELIGIBLE");

    private static Result<string?> BadRule(string message) =>
        Result<string?>.Failure(ErrorCodes.BadRule, message);
}
