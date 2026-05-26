using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// UC07 — server-side form intake. Validates an inbound JSON payload against the
/// SIMPLIFIED JSON-schema subset stored on the referenced <see cref="ServicePassport"/>
/// BEFORE the workflow is started.
/// </summary>
/// <remarks>
/// <para>
/// The schema subset supported here is intentionally narrow (no full JSON-Schema
/// dependency): a root object that may declare <c>required</c> (array of field names)
/// and <c>properties</c> (object whose values describe per-field type + range
/// constraints). All present payload fields are checked against their schema entry;
/// fields not mentioned in the schema are allowed (lenient default) so passport
/// authors can iterate without breaking older clients.
/// </para>
/// <para>
/// Failure semantics:
/// <list type="bullet">
///   <item><description><see cref="ErrorCodes.ValidationFailed"/> — bad input, JSON parse error, or schema violations (all violations are accumulated into a single, semicolon-joined message).</description></item>
///   <item><description><see cref="ErrorCodes.InvalidSqid"/> — the supplied passport id is not a decodable Sqid.</description></item>
///   <item><description><see cref="ErrorCodes.NotFound"/> — the passport is missing, soft-deleted, or disabled.</description></item>
///   <item><description><see cref="ErrorCodes.Internal"/> — the schema stored on the passport is itself corrupt (configuration bug, not user fault).</description></item>
/// </list>
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping (CLAUDE.md RULE 3).</param>
/// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly (architecture test).</param>
public sealed class FormIntakeService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock) : IFormIntakeService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;

    // The clock is currently unused but injected by design — the architecture
    // verification asserts services flow time through ICnasTimeProvider, and future
    // date-aware field validators (e.g. "must be in the past") will plug in here.
    private readonly ICnasTimeProvider _clock = clock;

    /// <summary>Regex matching an ISO-8601 calendar date (date-only, or date + time prefix).</summary>
    private static readonly Regex DatePattern = new(
        @"^\d{4}-\d{2}-\d{2}(T.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Regex-cache key separator (kept private — fields can never contain '\n').</summary>
    private const string Sep = "; ";

    /// <inheritdoc />
    public async Task<Result> ValidateAsync(
        string servicePassportId,
        string formPayloadJson,
        CancellationToken cancellationToken = default)
    {
        // ── 1. Argument validation. Caller-supplied strings must be non-empty before we
        //       spend a DB round-trip or a JSON parse.
        if (string.IsNullOrWhiteSpace(servicePassportId))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Service passport id is required.");
        }
        if (string.IsNullOrWhiteSpace(formPayloadJson))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Form payload is required.");
        }

        // ── 2. Decode the Sqid → internal long (CLAUDE.md RULE 3). On failure surface
        //       the InvalidSqid code unchanged so the controller can map it to 400.
        var decoded = _sqids.TryDecode(servicePassportId);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        // ── 3. Load the passport. Must exist, be active (not soft-deleted), and enabled
        //       (production submissions allowed). All three failure modes map to NotFound
        //       to avoid leaking which of the three caused the rejection.
        var passport = await _db.ServicePassports
            .SingleOrDefaultAsync(
                p => p.Id == decoded.Value && p.IsActive && p.IsEnabled,
                cancellationToken)
            .ConfigureAwait(false);
        if (passport is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Service passport not found.");
        }

        // ── 4. Parse the inbound payload. A parse failure here is a USER-side problem,
        //       so it surfaces as ValidationFailed.
        JsonDocument payloadDoc;
        try
        {
            payloadDoc = JsonDocument.Parse(formPayloadJson);
        }
        catch (JsonException)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Form payload is not valid JSON.");
        }

        // ── 5. Parse the schema. A parse failure here means somebody saved an invalid
        //       schema on the passport — this is a SERVER-side problem so it surfaces as
        //       Internal, not ValidationFailed.
        JsonDocument schemaDoc;
        try
        {
            schemaDoc = JsonDocument.Parse(passport.FormSchemaJson);
        }
        catch (JsonException)
        {
            payloadDoc.Dispose();
            return Result.Failure(ErrorCodes.Internal, "Service passport form schema is corrupt");
        }

        try
        {
            // ── 6. Validate payload against schema, accumulating ALL violations.
            return ValidateAgainstSchema(payloadDoc.RootElement, schemaDoc.RootElement);
        }
        finally
        {
            payloadDoc.Dispose();
            schemaDoc.Dispose();
        }
    }

    /// <summary>
    /// Walks the schema applying the SIMPLIFIED rule set described on
    /// <see cref="FormIntakeService"/>. Collects every violation into a single,
    /// semicolon-joined failure message — never short-circuits on the first one.
    /// </summary>
    /// <param name="payload">Parsed payload root element.</param>
    /// <param name="schema">Parsed schema root element.</param>
    /// <returns>Success when all rules pass; <see cref="ErrorCodes.ValidationFailed"/> otherwise.</returns>
    private static Result ValidateAgainstSchema(JsonElement payload, JsonElement schema)
    {
        // The payload root must be an object — anything else cannot have named fields
        // to compare against the schema's 'properties' map.
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                "Form payload invalid: root must be a JSON object");
        }

        var violations = new List<string>();

        // 6a. Required-fields check. Any name in schema.required that is missing
        //     (or explicitly null) in the payload is a violation.
        if (schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("required", out var requiredElement) &&
            requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var requiredItem in requiredElement.EnumerateArray())
            {
                if (requiredItem.ValueKind != JsonValueKind.String) continue;
                var fieldName = requiredItem.GetString();
                if (string.IsNullOrEmpty(fieldName)) continue;

                if (!payload.TryGetProperty(fieldName, out var presentValue) ||
                    presentValue.ValueKind == JsonValueKind.Null)
                {
                    violations.Add($"'{fieldName}' is required");
                }
            }
        }

        // 6b. Per-field constraint check. Only properties that are BOTH declared in the
        //     schema AND present in the payload need to be evaluated; unknown payload
        //     fields are tolerated by design (lenient default), and missing-but-declared
        //     fields are already covered by the 'required' check above.
        if (schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("properties", out var propsElement) &&
            propsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in propsElement.EnumerateObject())
            {
                if (!payload.TryGetProperty(prop.Name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.Null) continue; // missing-ness handled above
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                ValidateField(prop.Name, value, prop.Value, violations);
            }
        }

        return violations.Count == 0
            ? Result.Success()
            : Result.Failure(
                ErrorCodes.ValidationFailed,
                $"Form payload invalid: {string.Join(Sep, violations)}");
    }

    /// <summary>
    /// Validates a single payload field against its schema entry. Mutates
    /// <paramref name="violations"/> in place — adds zero or more human-readable
    /// messages describing what is wrong with this field.
    /// </summary>
    /// <param name="name">Field name (used to prefix the violation messages).</param>
    /// <param name="value">Payload value for this field.</param>
    /// <param name="fieldSchema">Schema sub-object describing the constraints on this field.</param>
    /// <param name="violations">Accumulator for all collected violations.</param>
    private static void ValidateField(
        string name,
        JsonElement value,
        JsonElement fieldSchema,
        List<string> violations)
    {
        var typeText = fieldSchema.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

        switch (typeText)
        {
            case "string":
                ValidateString(name, value, fieldSchema, violations);
                break;

            case "integer":
                ValidateInteger(name, value, fieldSchema, violations);
                break;

            case "number":
                ValidateNumber(name, value, fieldSchema, violations);
                break;

            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    violations.Add($"'{name}' must be a boolean");
                }
                break;

            case "date":
                ValidateDate(name, value, violations);
                break;

            // Unknown type — passport configuration loosely permits arbitrary fields, so
            // we tolerate it silently rather than raising a per-field error.
            default:
                break;
        }
    }

    /// <summary>String-type validation: type kind + minLength + maxLength + pattern.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="value">Payload value.</param>
    /// <param name="fieldSchema">Schema entry for this field.</param>
    /// <param name="violations">Accumulator for violations.</param>
    private static void ValidateString(string name, JsonElement value, JsonElement fieldSchema, List<string> violations)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            violations.Add($"'{name}' must be a string");
            return;
        }
        var text = value.GetString() ?? string.Empty;

        if (fieldSchema.TryGetProperty("minLength", out var minLen) &&
            minLen.ValueKind == JsonValueKind.Number &&
            minLen.TryGetInt32(out var minLenValue) &&
            text.Length < minLenValue)
        {
            violations.Add($"'{name}' is shorter than minLength {minLenValue}");
        }
        if (fieldSchema.TryGetProperty("maxLength", out var maxLen) &&
            maxLen.ValueKind == JsonValueKind.Number &&
            maxLen.TryGetInt32(out var maxLenValue) &&
            text.Length > maxLenValue)
        {
            violations.Add($"'{name}' exceeds maxLength {maxLenValue}");
        }
        if (fieldSchema.TryGetProperty("pattern", out var patternElem) &&
            patternElem.ValueKind == JsonValueKind.String)
        {
            var patternText = patternElem.GetString();
            if (!string.IsNullOrEmpty(patternText))
            {
                bool matches;
                try
                {
                    matches = Regex.IsMatch(text, patternText, RegexOptions.CultureInvariant);
                }
                catch (ArgumentException)
                {
                    // A malformed regex is a passport-author bug. We still surface it as a
                    // user-facing violation rather than crashing — the operator will see
                    // the message in logs and fix the schema.
                    violations.Add($"'{name}' could not be evaluated (invalid pattern in schema)");
                    return;
                }
                if (!matches)
                {
                    violations.Add($"'{name}' does not match pattern");
                }
            }
        }
    }

    /// <summary>Integer-type validation: integral kind + minimum/maximum.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="value">Payload value.</param>
    /// <param name="fieldSchema">Schema entry for this field.</param>
    /// <param name="violations">Accumulator for violations.</param>
    private static void ValidateInteger(string name, JsonElement value, JsonElement fieldSchema, List<string> violations)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var intValue))
        {
            violations.Add($"'{name}' must be an integer");
            return;
        }

        if (fieldSchema.TryGetProperty("minimum", out var min) &&
            min.ValueKind == JsonValueKind.Number &&
            min.TryGetInt64(out var minValue) &&
            intValue < minValue)
        {
            violations.Add($"'{name}' is below minimum {minValue}");
        }
        if (fieldSchema.TryGetProperty("maximum", out var max) &&
            max.ValueKind == JsonValueKind.Number &&
            max.TryGetInt64(out var maxValue) &&
            intValue > maxValue)
        {
            violations.Add($"'{name}' is above maximum {maxValue}");
        }
    }

    /// <summary>Numeric (decimal-allowed) validation: numeric kind + minimum/maximum.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="value">Payload value.</param>
    /// <param name="fieldSchema">Schema entry for this field.</param>
    /// <param name="violations">Accumulator for violations.</param>
    private static void ValidateNumber(string name, JsonElement value, JsonElement fieldSchema, List<string> violations)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var numValue))
        {
            violations.Add($"'{name}' must be a number");
            return;
        }

        if (fieldSchema.TryGetProperty("minimum", out var min) &&
            min.ValueKind == JsonValueKind.Number &&
            min.TryGetDecimal(out var minValue) &&
            numValue < minValue)
        {
            violations.Add($"'{name}' is below minimum " +
                minValue.ToString(CultureInfo.InvariantCulture));
        }
        if (fieldSchema.TryGetProperty("maximum", out var max) &&
            max.ValueKind == JsonValueKind.Number &&
            max.TryGetDecimal(out var maxValue) &&
            numValue > maxValue)
        {
            violations.Add($"'{name}' is above maximum " +
                maxValue.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Date validation: must be a string matching <see cref="DatePattern"/>.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="value">Payload value.</param>
    /// <param name="violations">Accumulator for violations.</param>
    private static void ValidateDate(string name, JsonElement value, List<string> violations)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            violations.Add($"'{name}' must be a date string");
            return;
        }
        var text = value.GetString() ?? string.Empty;
        if (!DatePattern.IsMatch(text))
        {
            violations.Add($"'{name}' is not a valid date (expected YYYY-MM-DD)");
        }
    }
}
