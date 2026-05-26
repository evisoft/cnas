using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Decisions;

/// <summary>
/// Converts the schema-flexible <c>FormPayloadJson</c> stored on a
/// <see cref="Cnas.Ps.Core.Domain.ServiceApplication"/> into a strongly-typed
/// <see cref="DecisionFacts"/> bag that <see cref="IDecisionEngine"/> can evaluate.
/// <para>
/// Conversion rules (see CLAUDE.md RULE 6 — reuse existing engine types):
/// </para>
/// <list type="bullet">
///   <item>JSON booleans → <see cref="bool"/></item>
///   <item>JSON integers (no decimal point) → <see cref="long"/></item>
///   <item>JSON numbers with a decimal point → <see cref="decimal"/></item>
///   <item>JSON strings matching the ISO-8601 date-time prefix (<c>^\d{4}-\d{2}-\d{2}T</c>)
///         → <see cref="DateTime"/> parsed with <see cref="DateTimeStyles.AssumeUniversal"/>
///         | <see cref="DateTimeStyles.AdjustToUniversal"/></item>
///   <item>JSON strings otherwise → <see cref="string"/></item>
///   <item>JSON <c>null</c> → <see langword="null"/> entry in the fact bag</item>
///   <item>JSON arrays / nested objects: silently ignored (engine does not yet
///         support nested facts)</item>
/// </list>
/// </summary>
/// <example>
/// <code>
/// var factsResult = FormPayloadParser.Parse(app.FormPayloadJson, clock.UtcNow);
/// if (factsResult.IsFailure) return Result.From(factsResult);
/// var outcome = engine.Evaluate(passport.DecisionRulesJson, factsResult.Value);
/// </code>
/// </example>
public static class FormPayloadParser
{
    /// <summary>
    /// Detects a string value that looks like an ISO-8601 date-time prefix; conservative
    /// (we only match if the first 11 characters are <c>YYYY-MM-DDT</c>) to avoid
    /// accidentally promoting plain strings like <c>"2026-policy"</c> to a DateTime.
    /// </summary>
    private static readonly Regex IsoDateTimePrefix =
        new(@"^\d{4}-\d{2}-\d{2}T", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses <paramref name="formPayloadJson"/> into a <see cref="DecisionFacts"/> bag,
    /// adding the synthetic <c>claimDateUtc</c> fact (set to <paramref name="claimDateUtc"/>)
    /// when the payload does not already contain it.
    /// </summary>
    /// <param name="formPayloadJson">
    /// The JSON object stored on <c>ServiceApplication.FormPayloadJson</c>. Must be a
    /// JSON object at the root; arrays/scalars at the root are rejected with
    /// <see cref="ErrorCodes.BadRule"/>.
    /// </param>
    /// <param name="claimDateUtc">
    /// The decision clock instant (UTC). Used as the fallback value for
    /// <c>claimDateUtc</c> when the payload omits it. Must be <see cref="DateTimeKind.Utc"/>.
    /// </param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the populated <see cref="DecisionFacts"/>,
    /// or <see cref="Result{T}.Failure(string, string)"/> with <see cref="ErrorCodes.BadRule"/>
    /// when the JSON is malformed or its root is not an object.
    /// </returns>
    /// <example>
    /// <code>
    /// var facts = FormPayloadParser.Parse("{\"birthOrder\":2}", DateTime.UtcNow).Value;
    /// facts.Values["birthOrder"].Should().Be(2L);
    /// </code>
    /// </example>
    public static Result<DecisionFacts> Parse(string formPayloadJson, DateTime claimDateUtc)
    {
        // Empty or null payload defensively becomes a single-fact bag containing only
        // the claim date so simple rule-sets that only depend on claimDateUtc still run.
        if (string.IsNullOrWhiteSpace(formPayloadJson))
        {
            return Result<DecisionFacts>.Failure(
                ErrorCodes.BadRule,
                "Form payload is not valid JSON.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(formPayloadJson);
        }
        catch (JsonException ex)
        {
            return Result<DecisionFacts>.Failure(
                ErrorCodes.BadRule,
                $"Form payload is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Result<DecisionFacts>.Failure(
                    ErrorCodes.BadRule,
                    "Form payload must be a JSON object at the root.");
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                // Arrays and nested objects are silently dropped — the current engine
                // does not support nested fact graphs (TODO §3).
                if (TryConvert(property.Value, out var converted))
                {
                    values[property.Name] = converted;
                }
            }

            // Synthesize claimDateUtc when absent so rule-sets that compare a date
            // fact against "now" don't need every form to carry the timestamp.
            if (!values.ContainsKey("claimDateUtc"))
            {
                values["claimDateUtc"] = DateTime.SpecifyKind(claimDateUtc, DateTimeKind.Utc);
            }

            return Result<DecisionFacts>.Success(new DecisionFacts(values));
        }
    }

    /// <summary>
    /// Maps a single JSON value to its strongly-typed CLR representation per the rules
    /// documented on the class. Returns <see langword="false"/> for arrays / nested objects
    /// so the caller can skip them.
    /// </summary>
    /// <param name="element">The JSON value to translate.</param>
    /// <param name="value">The converted CLR value on success; <see langword="null"/> on miss.</param>
    /// <returns><see langword="true"/> when the value was converted (including JSON null → null).</returns>
    private static bool TryConvert(JsonElement element, out object? value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;

            case JsonValueKind.False:
                value = false;
                return true;

            case JsonValueKind.Null:
                value = null;
                return true;

            case JsonValueKind.Number:
                // Integer values (no decimal point in the source text) become long;
                // anything else becomes decimal so monetary precision is preserved.
                if (element.TryGetInt64(out var asLong)
                    && !element.GetRawText().Contains('.'))
                {
                    value = asLong;
                    return true;
                }
                value = element.GetDecimal();
                return true;

            case JsonValueKind.String:
                var raw = element.GetString() ?? string.Empty;
                if (IsoDateTimePrefix.IsMatch(raw)
                    && DateTime.TryParse(
                        raw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                    return true;
                }
                value = raw;
                return true;

            case JsonValueKind.Array:
            case JsonValueKind.Object:
            default:
                // Engine does not currently support nested facts — silently drop.
                value = null;
                return false;
        }
    }
}
